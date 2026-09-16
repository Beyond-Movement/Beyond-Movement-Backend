using BeyondMovement.SharedKernel;

namespace BeyondMovement.Modules.Finance.Domain;

/// <summary>
/// The only two payment states this product has — open decision <b>C-01</b>, resolved.
/// <para>
/// The specification proposed <c>Unpaid | PartiallyPaid | Paid</c> and the UI document proposed
/// <c>Pending | Paid</c>; the client ruled for the UI document's pair. There is deliberately no
/// partial state — nothing in the product can record a part payment — and no cancelled state: a
/// purchase is either awaiting the coach's confirmation or confirmed.
/// </para>
/// </summary>
public enum PurchasePaymentStatus { Pending, Paid }

/// <summary>
/// How the purchase came to exist. Both kinds end as a paid purchase and a package; they differ
/// only in who started it and whether money moved through InstaPay.
/// </summary>
public enum PurchaseOrigin
{
    /// <summary>The athlete chose an option in the app and the Admin later confirmed payment.</summary>
    Athlete,

    /// <summary>
    /// The Admin recorded a package directly — cash, bank transfer, a sale agreed off-app. It is
    /// born <see cref="PurchasePaymentStatus.Paid"/>, because recording it <em>is</em> the
    /// confirmation, so payment history has a row for every package rather than only for the
    /// ones bought through the app.
    /// </summary>
    AdminDirect
}

/// <summary>
/// An athlete's request to buy a package, and the record of its payment.
/// <para>
/// This is the third of the three package-shaped things in this codebase, and they are kept
/// apart on purpose:
/// </para>
/// <list type="bullet">
/// <item><c>PackageOption</c> — the catalogue entry the coach sells.</item>
/// <item><c>PurchasedPackage</c> — the thing the athlete owns and sessions deduct from.</item>
/// <item><c>PackagePurchase</c> — this: the money, and the snapshot of what was agreed.</item>
/// </list>
/// <para>
/// <b>Everything the athlete was shown is copied here at selection time</b> — name, session
/// count, features, and the price resolved by the Phase 4 rules. Editing the catalogue option or
/// the athlete's pricing afterwards never reaches back into a purchase, so the number the coach
/// confirms is always the number the athlete saw. The price is never accepted from the client.
/// </para>
/// <para>
/// A purchase produces a package exactly once. <see cref="PurchasedPackageId"/> records that,
/// and a unique index on it means even a repeated or concurrent confirmation cannot make a
/// second one.
/// </para>
/// </summary>
public sealed class PackagePurchase
{
    public const int MaxPackageNameLength = 100;
    public const int MaxFeatureLength = 100;
    public const int MaxFeatureCodeLength = 40;
    public const int MaxFeatures = 10;

    private readonly List<PackagePurchaseFeature> _features = [];

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid CoachId { get; private set; }

    /// <summary>The profile id, which is what a <c>PurchasedPackage</c> is keyed by.</summary>
    public Guid AthleteProfileId { get; private set; }

    /// <summary>
    /// The user id, which is what every <c>/athletes/{athleteId}</c> route uses. Both are stored
    /// because the Admin filters by the id the routes expose while the package is created against
    /// the other, and joining profiles on every list read to convert between them is a cost paid
    /// on every request to save sixteen bytes.
    /// </summary>
    public Guid AthleteUserId { get; private set; }

    /// <summary>
    /// Provenance only, never read for values. Nullable because a purchase outlives the catalogue
    /// entry it came from — see the SetNull relationship in the configuration.
    /// </summary>
    public Guid? PackageOptionId { get; private set; }

    // --- the snapshot -----------------------------------------------------
    // Frozen at selection. Nothing in this block is ever recomputed from the catalogue.

    public string PackageName { get; private set; } = null!;
    public int SessionCount { get; private set; }

    /// <summary>
    /// The included features as the athlete read them down the card, each with the
    /// <see cref="PackageFeatureCode"/> it carried at the time, or null for an ordinary one. Order
    /// is meaning, so this is an ordered list rather than a set, and it is sorted here rather than
    /// left to whatever order the database returns rows in.
    /// <para>
    /// <b>Empty for every purchase backfilled onto a package that predates Phase 8.</b> The
    /// snapshot must be what the athlete was shown, and the catalogue option may have been edited
    /// since, so an empty list was recorded rather than a fabricated one. That is also why nothing
    /// reads eligibility from here - <c>PurchasedPackage.IncludedFeatures</c> is the authority.
    /// </para>
    /// </summary>
    public IReadOnlyList<PackageFeature> Features =>
        [.. _features.OrderBy(f => f.Position).Select(f => f.ToFeature())];

    /// <summary>
    /// The <see cref="PackageFeatureCode"/>s in the snapshot, which is what confirming a payment
    /// puts on the package it creates. Distinct - the code is the identity.
    /// </summary>
    public IReadOnlyList<PackageFeatureCode> FeatureCodes =>
        [.. _features.OrderBy(f => f.Position)
            .Where(f => f.Code is not null).Select(f => f.Code!.Value).Distinct()];

    /// <summary>The EF navigation, by name. Used for Include, never for reading.</summary>
    public const string FeaturesNavigation = "_features";

    /// <summary>
    /// Piastres — the output of the Phase 4 pricing rule (custom override, else loyalty, else
    /// default) at the moment of selection. Never sent by the client.
    /// </summary>
    public long PriceMinor { get; private set; }

    public string Currency { get; private set; } = null!;

    // --- payment ----------------------------------------------------------

    public PurchasePaymentStatus Status { get; private set; } = PurchasePaymentStatus.Pending;
    public PurchaseOrigin Origin { get; private set; } = PurchaseOrigin.Athlete;

    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public DateTime? PaidAtUtc { get; private set; }

    /// <summary>
    /// The Admin who confirmed the payment. Nullable for the purchases backfilled onto packages
    /// that existed before this phase, where the confirming user is not recoverable.
    /// </summary>
    public Guid? PaidByUserId { get; private set; }

    /// <summary>The package this purchase produced. Null until paid, and set exactly once.</summary>
    public Guid? PurchasedPackageId { get; private set; }

    /// <summary>
    /// Maps to Postgres' <c>xmin</c>, as <c>PurchasedPackage</c> and <c>Session</c> do. The
    /// confirmation path also takes a row lock, so this is the second line of defence rather
    /// than the first.
    /// </summary>
    public uint Version { get; private set; }

    private PackagePurchase() { }   // EF Core

    /// <summary>
    /// The athlete selects an option. Starts <see cref="PurchasePaymentStatus.Pending"/> — no
    /// package exists yet, and the athlete can never activate one.
    /// </summary>
    public static PackagePurchase Select(
        Guid coachId, Guid athleteProfileId, Guid athleteUserId, Guid packageOptionId,
        string packageName, int sessionCount, IReadOnlyList<PackageFeature> features,
        long priceMinor, string currency, DateTime nowUtc)
    {
        var purchase = new PackagePurchase
        {
            CoachId = coachId,
            AthleteProfileId = athleteProfileId,
            AthleteUserId = athleteUserId,
            CreatedAtUtc = nowUtc
        };

        purchase.ApplySnapshot(
            packageOptionId, packageName, sessionCount, features, priceMinor, currency, nowUtc);

        return purchase;
    }

    /// <summary>
    /// The Admin recorded a package directly, so the purchase is born paid and already linked to
    /// the package it produced. This exists so every package has payment history behind it, not
    /// only the ones bought through the app.
    /// </summary>
    public static PackagePurchase RecordAdminSale(
        Guid coachId, Guid athleteProfileId, Guid athleteUserId, Guid? packageOptionId,
        string packageName, int sessionCount, IReadOnlyList<PackageFeature> features,
        long priceMinor, string currency, Guid purchasedPackageId, Guid actorUserId,
        DateTime nowUtc)
    {
        var purchase = new PackagePurchase
        {
            CoachId = coachId,
            AthleteProfileId = athleteProfileId,
            AthleteUserId = athleteUserId,
            PackageOptionId = packageOptionId,
            PackageName = packageName.Trim(),
            SessionCount = sessionCount,
            PriceMinor = priceMinor,
            Currency = currency,
            Origin = PurchaseOrigin.AdminDirect,
            Status = PurchasePaymentStatus.Paid,
            PurchasedPackageId = purchasedPackageId,
            PaidByUserId = actorUserId,
            PaidAtUtc = nowUtc,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc
        };

        purchase.WriteFeatures(features);
        return purchase;
    }

    /// <summary>
    /// The athlete changed their mind before paying. Rather than leaving them stuck behind a
    /// pending request for the wrong package — there is no Cancel in this product — the existing
    /// request is re-pointed at the new option and re-priced under today's rules.
    /// <para>
    /// Only while Pending. Once paid, the snapshot is the record of what somebody paid for, and
    /// nobody may edit it.
    /// </para>
    /// </summary>
    public Result ReviseSelection(
        Guid packageOptionId, string packageName, int sessionCount,
        IReadOnlyList<PackageFeature> features, long priceMinor, string currency, DateTime nowUtc)
    {
        if (Status == PurchasePaymentStatus.Paid)
            return Result.Failure(FinanceErrors.PurchaseAlreadyPaid);

        ApplySnapshot(
            packageOptionId, packageName, sessionCount, features, priceMinor, currency, nowUtc);

        return Result.Success();
    }

    /// <summary>
    /// The Admin confirms the money arrived, and the package the athlete bought comes into
    /// existence. The single allowed transition.
    /// <para>
    /// Repeating it is not an error at the API surface: the endpoint hands back the package this
    /// purchase already produced rather than making a second one. This guard is what tells the
    /// endpoint the transition has already happened; the guarantee that a second package cannot
    /// exist is the unique index on <see cref="PurchasedPackageId"/> and the row lock the
    /// confirmation path takes.
    /// </para>
    /// </summary>
    public Result MarkPaid(Guid purchasedPackageId, Guid actorUserId, DateTime nowUtc)
    {
        if (Status == PurchasePaymentStatus.Paid)
            return Result.Failure(FinanceErrors.PurchaseAlreadyPaid);

        Status = PurchasePaymentStatus.Paid;
        PurchasedPackageId = purchasedPackageId;
        PaidByUserId = actorUserId;
        PaidAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
        return Result.Success();
    }

    private void ApplySnapshot(
        Guid packageOptionId, string packageName, int sessionCount,
        IReadOnlyList<PackageFeature> features, long priceMinor, string currency, DateTime nowUtc)
    {
        PackageOptionId = packageOptionId;
        PackageName = packageName.Trim();
        SessionCount = sessionCount;
        WriteFeatures(features);
        PriceMinor = priceMinor;
        Currency = currency;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>
    /// Replaces the snapshot's feature rows. Existing rows are rewritten in place and only the
    /// difference is added or dropped, exactly as <c>PackageOption.Apply</c> does and for the same
    /// reason: positions are unique per purchase, and the new rows are written before the old ones
    /// are deleted, so clearing and re-adding makes the second new row collide with the second old
    /// one.
    /// </summary>
    private void WriteFeatures(IReadOnlyList<PackageFeature> features)
    {
        var existing = _features.OrderBy(f => f.Position).ToList();

        for (var i = 0; i < features.Count; i++)
        {
            var text = features[i].Text.Trim();
            var code = features[i].Code;

            if (i < existing.Count)
                existing[i].MoveTo(i, text, code);
            else
                _features.Add(PackagePurchaseFeature.At(i, text, code));
        }

        // Whatever the new list did not use.
        for (var i = features.Count; i < existing.Count; i++)
            _features.Remove(existing[i]);
    }
}

/// <summary>
/// One line of a purchase's feature snapshot, at a fixed position in the list.
/// <para>
/// A child table, where this was an array column before recognised feature codes existed. A
/// feature is now two values - the text the athlete read and the code it carried - and two
/// parallel arrays that must be kept the same length is the trap this codebase avoids everywhere
/// else. It is the shape <c>PackageOptionFeature</c> already has, for the same reason.
/// </para>
/// </summary>
public sealed class PackagePurchaseFeature
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid PackagePurchaseId { get; private set; }

    /// <summary>Zero-based, contiguous, and unique within the purchase.</summary>
    public int Position { get; private set; }

    /// <summary>What the athlete read. Never used to decide anything.</summary>
    public string Text { get; private set; } = null!;

    /// <summary>The recognised feature this line was, or null for an ordinary one.</summary>
    public PackageFeatureCode? Code { get; private set; }

    private PackagePurchaseFeature() { }   // EF Core

    public static PackagePurchaseFeature At(int position, string text, PackageFeatureCode? code) =>
        new() { Position = position, Text = text, Code = code };

    /// <summary>Rewrites this row rather than replacing it, so its position never collides.</summary>
    public void MoveTo(int position, string text, PackageFeatureCode? code)
    {
        Position = position;
        Text = text;
        Code = code;
    }

    public PackageFeature ToFeature() => new(Text, Code);
}
