using BeyondMovement.Modules.Packages.Domain;

namespace BeyondMovement.Modules.Packages.Contracts;

// Money crosses the wire as an integer count of piastres, never a decimal. See PackagePricing
// for why. Every price field in this file is named ...Minor to make that impossible to miss.

/// <param name="Features">
/// Ordered. The order sent is the order stored and the order returned; there is no separate
/// position field to keep in step.
/// </param>
public sealed record SavePackageOptionRequest(
    string Name,
    int Sessions,
    long DefaultPriceMinor,
    IReadOnlyList<string> Features);

/// <param name="Version">
/// The version the caller last read. Editing, archiving or restoring with a stale version is
/// refused with CONCURRENCY_CONFLICT rather than silently overwriting the other device's change.
/// </param>
public sealed record EditPackageOptionRequest(
    string Name,
    int Sessions,
    long DefaultPriceMinor,
    IReadOnlyList<string> Features,
    int Version);

/// <summary>Sent when archiving or restoring, which are also changes and also versioned.</summary>
public sealed record PackageOptionVersionRequest(int Version);

/// <summary>The Admin's view. Athletes never see this shape — they get a price, not a policy.</summary>
public sealed record PackageOptionResponse(
    Guid Id,
    string Name,
    int Sessions,
    long DefaultPriceMinor,
    string Currency,
    IReadOnlyList<string> Features,
    bool IsArchived,
    DateTime? ArchivedAtUtc,
    int Version,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

/// <param name="PriceMinor">Piastres. Stored exactly as sent — never rounded.</param>
public sealed record SetCustomPriceRequest(long PriceMinor);

public sealed record CustomPriceResponse(
    Guid AthleteUserId,
    Guid PackageOptionId,
    long PriceMinor,
    string Currency);

/// <summary>
/// One card in the athlete's catalogue.
/// <para>
/// <see cref="PriceMinor"/> is the final price for this athlete, already accounting for a
/// custom override or loyalty. There is deliberately no field saying which applied, and no
/// default price to compare against: the athlete is shown a price, not the coach's pricing
/// policy, and a "was 4000" the athlete never agreed to would be an invention.
/// </para>
/// </summary>
public sealed record CatalogueItemResponse(
    Guid Id,
    string Name,
    int Sessions,
    IReadOnlyList<string> Features,
    long PriceMinor,
    string Currency);

/// <summary>
/// Records that an athlete bought a catalogue option. Nothing about the price is in the request:
/// it is decided server-side from the option, the athlete's loyalty and any override, exactly as
/// the catalogue already shows it. An Admin who could send a price could send a different one
/// from the one the athlete was quoted.
/// </summary>
/// <param name="StartDate">Defaults to today, in UTC, when omitted.</param>
/// <param name="EndDate">Optional. A package normally ends when its sessions run out, not on a date.</param>
public sealed record PurchasePackageRequest(
    Guid PackageOptionId,
    DateOnly? StartDate = null,
    DateOnly? EndDate = null,
    string? Notes = null);

/// <summary>
/// A package an athlete owns.
/// <para>
/// <see cref="RemainingSessions"/> is sent even though it is <see cref="TotalSessions"/> minus
/// <see cref="UsedSessions"/>, so that the number the app displays and the number the server
/// deducts against are the same arithmetic, done once, here.
/// </para>
/// <para>
/// Note for the app: the UI shows <b>"New sessions pending"</b> rather than "0 sessions
/// remaining" (architecture C-04). That is a presentation rule — this field really is <c>0</c>
/// and must stay a number, or reports and deduction would each need a special case.
/// </para>
/// </summary>
public sealed record PurchasedPackageResponse(
    Guid Id,
    Guid AthleteProfileId,
    Guid? PackageOptionId,
    string Name,
    int TotalSessions,
    int UsedSessions,
    int RemainingSessions,
    long PricePaidMinor,
    string Currency,
    DateOnly StartDate,
    DateOnly? EndDate,
    PurchasedPackageStatus Status,
    string? Notes,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public static class PurchasedPackageMapping
{
    public static PurchasedPackageResponse ToResponse(this PurchasedPackage x) => new(
        x.Id, x.AthleteProfileId, x.PackageOptionId, x.Name, x.TotalSessions, x.UsedSessions,
        x.RemainingSessions, x.PricePaidMinor, x.Currency, x.StartDate, x.EndDate, x.Status,
        x.Notes, x.CreatedAtUtc, x.UpdatedAtUtc);
}
