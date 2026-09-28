namespace BeyondMovement.Modules.Athletes.Domain;

/// <summary>
/// One entry in the sports catalogue — what an athlete picks on Complete and Edit Profile.
/// <para>
/// Global reference data, not the coach's: there is no <c>CoachId</c>, and no endpoint creates,
/// edits or deletes one. The catalogue is maintained by the application — a new sport is a row
/// added to <see cref="SportCatalogue"/> and shipped as a migration.
/// </para>
/// <para>
/// A row, not an enum, so adding a sport is a data change the app picks up from
/// <c>GET /sports</c> rather than a contract change it has to be rebuilt for.
/// </para>
/// </summary>
public sealed class Sport
{
    public Guid Id { get; private set; }

    /// <summary>What the athlete sees and what the profile reports. Unique, ignoring case.</summary>
    public string Name { get; private set; } = null!;

    /// <summary>
    /// The ordering <em>group</em>, not a position: the catalogue is ordered by this, then by
    /// name. Every ordinary sport is <see cref="SportCatalogue.Ordinary"/>, so they read
    /// alphabetically and a new one slots into place without renumbering anything; only
    /// <c>Other</c> sits in a later group, so it is always last.
    /// </summary>
    public int SortOrder { get; private set; }

    private Sport() { }

    internal Sport(Guid id, string name, int sortOrder)
    {
        Id = id;
        Name = name;
        SortOrder = sortOrder;
    }
}
