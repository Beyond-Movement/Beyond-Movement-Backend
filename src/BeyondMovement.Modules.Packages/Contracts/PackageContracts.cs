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
/// One row of the Admin's pricing view for an athlete: the list price, what this athlete
/// actually pays, and which rule decided it.
/// <para>
/// The Admin counterpart of <see cref="CatalogueItemResponse"/>, and deliberately a different
/// shape rather than more fields on it. The athlete is shown a price; the coach is shown the
/// policy behind it, and a "was 4000" the athlete never agreed to must never reach them.
/// </para>
/// </summary>
/// <param name="DefaultPriceMinor">
/// The option's list price in piastres, before anything is applied. Equal to
/// <paramref name="EffectivePriceMinor"/> when <paramref name="PricingSource"/> is
/// <c>Default</c>.
/// </param>
/// <param name="EffectivePriceMinor">
/// What this athlete pays in piastres, and the number a purchase would be snapshotted at today.
/// Decided server-side by <c>PackagePricing.Resolve</c> — the client must not reproduce the rule.
/// </param>
/// <param name="PricingSource">
/// Which rule produced <paramref name="EffectivePriceMinor"/>: <c>Default</c>, <c>Loyalty</c>, or
/// <c>Custom</c>. <c>Custom</c> means an override exists for this athlete and this option, so
/// Remove Custom Price is the action that applies; the other two mean there is none to remove.
/// </param>
public sealed record AthletePricingItem(
    Guid PackageOptionId,
    string Name,
    int Sessions,
    long DefaultPriceMinor,
    long EffectivePriceMinor,
    PricingSource PricingSource);

/// <summary>
/// Everything the Athlete Pricing screen needs, in one call: the athlete's loyalty standing and
/// a priced row for every active package option.
/// </summary>
/// <param name="IsLoyal">
/// The same flag the athlete list and athlete detail carry, repeated here so the screen does not
/// need a second call to render the loyalty toggle beside the prices it affects.
/// </param>
/// <param name="LoyaltyDiscountPercent">
/// The discount this athlete's loyalty earns, or <b>null when they are not loyal</b> — there is
/// no percentage to state, and a number here would invite the screen to show "-15%" to someone
/// who does not get it.
/// <para>
/// It applies only to items whose <c>pricingSource</c> is <c>Loyalty</c>. A loyal athlete with an
/// override on one option pays the override on that row, undiscounted, and the loyalty price on
/// the others.
/// </para>
/// </summary>
/// <param name="Items">
/// Active options only — archived ones cannot be sold, so pricing them would be pricing something
/// nobody can buy. Ordered by name, matching the Admin's package-options list.
/// </param>
public sealed record AthletePricingResponse(
    Guid AthleteUserId,
    bool IsLoyal,
    int? LoyaltyDiscountPercent,
    string Currency,
    IReadOnlyList<AthletePricingItem> Items);

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
