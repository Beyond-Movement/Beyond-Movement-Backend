namespace BeyondMovement.Modules.Packages.Domain;

/// <summary>
/// A price the coach agreed with one athlete for one package option, overriding both the
/// default price and any loyalty discount.
/// <para>
/// The athlete is held as a bare id rather than a navigation property: this module may not
/// reference Athletes or Identity (CLAUDE.md section 4), the same way <c>CoachId</c> is carried
/// elsewhere. A unique index on the pair enforces "only one override per athlete and option".
/// </para>
/// </summary>
public sealed class AthletePackagePrice
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid AthleteUserId { get; private set; }
    public Guid PackageOptionId { get; private set; }

    /// <summary>Piastres, stored exactly as the coach entered it — never rounded.</summary>
    public long PriceMinor { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    private AthletePackagePrice() { }

    public static AthletePackagePrice Create(
        Guid athleteUserId, Guid packageOptionId, long priceMinor, DateTime nowUtc) => new()
    {
        AthleteUserId = athleteUserId,
        PackageOptionId = packageOptionId,
        PriceMinor = priceMinor,
        CreatedAtUtc = nowUtc,
        UpdatedAtUtc = nowUtc
    };

    /// <summary>
    /// Setting an override for a pair that already has one moves the price rather than adding a
    /// second row, so "only one override may exist" holds without the caller checking first.
    /// </summary>
    public void SetPrice(long priceMinor, DateTime nowUtc)
    {
        PriceMinor = priceMinor;
        UpdatedAtUtc = nowUtc;
    }
}
