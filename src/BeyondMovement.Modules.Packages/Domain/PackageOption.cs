using BeyondMovement.SharedKernel;

namespace BeyondMovement.Modules.Packages.Domain;

/// <summary>
/// A reusable entry in the coach's catalogue — "8 sessions, 4000 EGP, these features".
/// <para>
/// This is <b>not</b> a package an athlete has bought. The purchased package, its remaining
/// sessions and its history are a later phase, and keeping them apart is the point: a catalogue
/// entry can be renamed, repriced or archived, and none of that may reach back and alter what
/// somebody already paid for.
/// </para>
/// </summary>
public sealed class PackageOption
{
    public const int MaxNameLength = 100;
    public const int MinSessions = 1;
    public const int MaxSessions = 1000;
    public const int MinFeatures = 1;
    public const int MaxFeatures = 10;

    private readonly List<PackageOptionFeature> _features = [];

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid CoachId { get; private set; }

    public string Name { get; private set; } = null!;
    public int Sessions { get; private set; }

    /// <summary>Piastres. See <see cref="PackagePricing"/> for why this is not a decimal.</summary>
    public long DefaultPriceMinor { get; private set; }

    public bool IsArchived { get; private set; }
    public DateTime? ArchivedAtUtc { get; private set; }

    /// <summary>
    /// Incremented on every change, and checked against the value the caller last read.
    /// The coach may have the catalogue open on a phone and a tablet; without this, the second
    /// save silently overwrites the first and neither device shows anything wrong.
    /// </summary>
    public int Version { get; private set; } = 1;

    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    /// <summary>
    /// The features as the coach arranged them. The order is meaning, not presentation — it is
    /// what the athlete reads down the card — so it is sorted here rather than left to whatever
    /// order the database returns rows in.
    /// <para>
    /// The list itself is mapped as a field-only navigation (see the configuration), so nothing
    /// outside this class can add or remove a feature without going through <see cref="Edit"/>.
    /// </para>
    /// </summary>
    public IReadOnlyList<PackageOptionFeature> OrderedFeatures =>
        [.. _features.OrderBy(f => f.Position)];

    /// <summary>The EF navigation, by name. Used for Include, never for reading.</summary>
    public const string FeaturesNavigation = "_features";

    private PackageOption() { }   // EF Core

    public static PackageOption Create(
        Guid coachId, string name, int sessions, long defaultPriceMinor,
        IReadOnlyList<string> features, DateTime nowUtc)
    {
        var option = new PackageOption
        {
            CoachId = coachId,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc
        };

        option.Apply(name, sessions, defaultPriceMinor, features, nowUtc);
        option.Version = 1;   // Apply bumps it; a brand-new option starts at 1
        return option;
    }

    /// <summary>
    /// Edits every field at once rather than offering a setter each. A package option is read as
    /// a whole — name, sessions, price and features on one card — so it is edited as a whole,
    /// and there is no order of individual setters that can leave it half-changed.
    /// </summary>
    public Result Edit(
        string name, int sessions, long defaultPriceMinor,
        IReadOnlyList<string> features, DateTime nowUtc)
    {
        // Restoring is a deliberate, separate act. Editing an archived option would quietly
        // resurrect it in the Admin list while it is still hidden from athletes.
        if (IsArchived)
            return Result.Failure(PackageErrors.Archived);

        Apply(name, sessions, defaultPriceMinor, features, nowUtc);
        return Result.Success();
    }

    private void Apply(
        string name, int sessions, long defaultPriceMinor,
        IReadOnlyList<string> features, DateTime nowUtc)
    {
        Name = name.Trim();
        Sessions = sessions;
        DefaultPriceMinor = defaultPriceMinor;

        // Existing rows are rewritten in place and only the difference is added or dropped.
        // Clearing the list and re-adding would be simpler to read, but positions are unique per
        // option, and the new rows are written before the old ones are deleted - so the second
        // feature of the new list collides with the second feature of the old one.
        var existing = _features.OrderBy(f => f.Position).ToList();

        for (var i = 0; i < features.Count; i++)
        {
            var text = features[i].Trim();

            if (i < existing.Count)
                existing[i].MoveTo(i, text);
            else
                _features.Add(PackageOptionFeature.At(i, text));
        }

        // Whatever the new list did not use.
        for (var i = features.Count; i < existing.Count; i++)
            _features.Remove(existing[i]);

        UpdatedAtUtc = nowUtc;
        Version++;
    }

    /// <summary>
    /// Hides the option from the athlete catalogue. Nothing is deleted, and nothing an athlete
    /// has already bought is touched — a price the coach withdrew today is still the price
    /// somebody paid last week.
    /// </summary>
    public Result Archive(DateTime nowUtc)
    {
        if (IsArchived)
            return Result.Failure(PackageErrors.Archived);

        IsArchived = true;
        ArchivedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
        Version++;
        return Result.Success();
    }

    public Result Restore(DateTime nowUtc)
    {
        if (!IsArchived)
            return Result.Failure(PackageErrors.NotArchived);

        IsArchived = false;
        ArchivedAtUtc = null;
        UpdatedAtUtc = nowUtc;
        Version++;
        return Result.Success();
    }
}

/// <summary>One line on the package card, at a fixed position in the list.</summary>
public sealed class PackageOptionFeature
{
    public const int MaxTextLength = 100;

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid PackageOptionId { get; private set; }

    /// <summary>Zero-based, contiguous, and unique within the option.</summary>
    public int Position { get; private set; }
    public string Text { get; private set; } = null!;

    private PackageOptionFeature() { }

    public static PackageOptionFeature At(int position, string text) =>
        new() { Position = position, Text = text };

    /// <summary>Rewrites this row rather than replacing it, so its position never collides.</summary>
    public void MoveTo(int position, string text)
    {
        Position = position;
        Text = text;
    }
}
