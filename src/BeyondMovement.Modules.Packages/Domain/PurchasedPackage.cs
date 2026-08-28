using BeyondMovement.SharedKernel;

// The property below is called Currency, which hides the static Currency class from every
// expression inside this file. The alias is how the class stays reachable.
using PlatformCurrency = BeyondMovement.Modules.Packages.Currency;

namespace BeyondMovement.Modules.Packages.Domain;

/// <summary>Active until it runs out (Completed) or the coach ends it early (Closed).</summary>
public enum PurchasedPackageStatus { Active, Completed, Closed }

/// <summary>
/// A package an athlete actually owns — the thing sessions are deducted from.
/// <para>
/// Deliberately a separate entity from <see cref="PackageOption"/>, which is the catalogue.
/// Everything the athlete bought is <b>copied</b> here at purchase time: the name, the session
/// count and the price as paid. A catalogue entry can be renamed, repriced or archived
/// afterwards and none of it reaches back — what somebody paid last week is a fact, not a
/// lookup. <see cref="PackageOptionId"/> is kept only as provenance, and is nullable because a
/// package may outlive the option it came from.
/// </para>
/// <para>
/// <b>BR-03</b> — at most one Active package per athlete — is held by a partial unique index in
/// the configuration, not by a check in a handler, because two Admin devices can purchase at the
/// same moment and only the database sees both.
/// </para>
/// </summary>
public sealed class PurchasedPackage
{
    public const int MaxNameLength = 100;
    public const int MaxNotesLength = 1000;

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid CoachId { get; private set; }
    public Guid AthleteProfileId { get; private set; }

    /// <summary>The catalogue entry this came from, for provenance only. Never read for values.</summary>
    public Guid? PackageOptionId { get; private set; }

    /// <summary>Copied from the option at purchase. Renaming the option does not rename this.</summary>
    public string Name { get; private set; } = null!;

    public int TotalSessions { get; private set; }
    public int UsedSessions { get; private set; }

    /// <summary>
    /// Piastres, as paid — the output of <see cref="PackagePricing.Effective"/> at the moment of
    /// purchase, with the athlete's loyalty and any override already applied. See
    /// <see cref="PackagePricing"/> for why money is never a decimal here.
    /// </summary>
    public long PricePaidMinor { get; private set; }

    public string Currency { get; private set; } = PlatformCurrency.Egp;

    public DateOnly StartDate { get; private set; }
    public DateOnly? EndDate { get; private set; }

    public PurchasedPackageStatus Status { get; private set; } = PurchasedPackageStatus.Active;
    public string? Notes { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    /// <summary>
    /// Optimistic concurrency on the deduction path. Two "Mark as Attended" taps that arrive
    /// together must produce one success and one conflict, never two deductions — this is the
    /// half of that guarantee the database enforces (architecture section 6.6).
    /// </summary>
    public uint Version { get; private set; }

    /// <summary>
    /// <b>Computed, never stored</b>, so it cannot drift away from the two numbers it comes from.
    /// A stored balance and a used count are two facts that can disagree; this is one fact.
    /// </summary>
    public int RemainingSessions => TotalSessions - UsedSessions;

    private PurchasedPackage() { }   // EF Core

    /// <summary>
    /// Records a purchase. <paramref name="pricePaidMinor"/> is passed in already decided rather
    /// than computed here: the price depends on the athlete's loyalty flag and their overrides,
    /// which live in other modules, and <see cref="PackagePricing"/> is the only place allowed to
    /// combine them.
    /// </summary>
    public static PurchasedPackage Purchase(
        Guid coachId, Guid athleteProfileId, Guid? packageOptionId, string name, int totalSessions,
        long pricePaidMinor, DateOnly startDate, DateOnly? endDate, string? notes, DateTime nowUtc) => new()
        {
            CoachId = coachId,
            AthleteProfileId = athleteProfileId,
            PackageOptionId = packageOptionId,
            Name = name.Trim(),
            TotalSessions = totalSessions,
            UsedSessions = 0,
            PricePaidMinor = pricePaidMinor,
            StartDate = startDate,
            EndDate = endDate,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc
        };

    /// <summary>
    /// Takes <paramref name="count"/> sessions off the balance. The only way <see cref="UsedSessions"/>
    /// ever moves, and only <c>0</c> or <c>1</c> is meaningful — a session consumes one session or
    /// none (BR-07). Anything else is a caller bug rather than a business failure, so it throws.
    /// <para>
    /// A package that reaches zero becomes <see cref="PurchasedPackageStatus.Completed"/> in the
    /// same operation. Leaving it Active with nothing left would let BR-03 block the athlete's
    /// next purchase, since the partial unique index counts an exhausted package as still active.
    /// </para>
    /// </summary>
    public Result Consume(int count, DateTime nowUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 1);

        // A non-deducting observation is attended and consumes nothing (BR-07). It is not a failure and
        // must not touch the balance, the status or the row version.
        if (count == 0)
            return Result.Success();

        if (Status != PurchasedPackageStatus.Active)
            return Result.Failure(PackageErrors.PackageNotActive);

        if (RemainingSessions < count)
            return Result.Failure(PackageErrors.NoSessionsRemaining);

        UsedSessions += count;

        if (RemainingSessions == 0)
            Status = PurchasedPackageStatus.Completed;

        UpdatedAtUtc = nowUtc;
        return Result.Success();
    }

    /// <summary>
    /// Ends the package early. Nothing is deleted and the balance is left as it stands, so the
    /// history still shows what was used.
    /// </summary>
    public Result Close(DateTime nowUtc)
    {
        if (Status == PurchasedPackageStatus.Closed)
            return Result.Failure(PackageErrors.PackageAlreadyClosed);

        Status = PurchasedPackageStatus.Closed;
        UpdatedAtUtc = nowUtc;
        return Result.Success();
    }
}
