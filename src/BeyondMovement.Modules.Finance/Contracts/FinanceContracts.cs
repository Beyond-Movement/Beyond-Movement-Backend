using BeyondMovement.Modules.Finance.Domain;

namespace BeyondMovement.Modules.Finance.Contracts;

// Money crosses the wire as an integer count of piastres, never a decimal, exactly as it does
// everywhere else in this API. See PackagePricing for why. Every price field is named ...Minor.

/// <summary>
/// The athlete selects a package option to buy.
/// <para>
/// There is deliberately no price, no session count and no name in this request. All of it is
/// resolved and snapshotted server-side from the option and the athlete's own pricing, so a
/// client cannot name its own price, and so the app never has to reproduce the loyalty,
/// override or rounding rules — it does not know them and must not learn them.
/// </para>
/// <para>
/// Posting this again with a different option while a purchase is still Pending <b>replaces</b>
/// the selection on the existing request rather than opening a second one. An athlete who picked
/// the wrong package must be able to correct it; there is no Cancel in this product.
/// </para>
/// </summary>
public sealed record CreatePurchaseRequest(Guid PackageOptionId);

/// <summary>
/// A purchase and its payment state.
/// <para>
/// The snapshot fields — <see cref="PackageName"/>, <see cref="SessionCount"/>,
/// <see cref="Features"/>, <see cref="PriceMinor"/> — are what was agreed when the athlete
/// selected, not what the catalogue says now. Renaming or repricing the option afterwards does
/// not change them, so what the app shows on a paid receipt is what was actually paid.
/// </para>
/// </summary>
/// <param name="PackageOptionId">
/// Provenance only. Null when the catalogue entry has since been deleted; the snapshot above it
/// is still complete, so the app never needs to follow this id to render the purchase.
/// </param>
/// <param name="PurchasedPackageId">
/// The package this purchase produced. Null while Pending, set once and never changed after.
/// This is the id to hand to <c>GET /api/v1/packages/{id}</c>.
/// </param>
/// <param name="PaidByUserId">
/// The Admin who confirmed payment. Null while Pending, and also null on the purchases
/// backfilled onto packages that pre-date this phase, where the confirming user is unknown.
/// </param>
public sealed record PackagePurchaseResponse(
    Guid Id,
    Guid AthleteUserId,
    Guid AthleteProfileId,
    Guid? PackageOptionId,
    string PackageName,
    int SessionCount,
    IReadOnlyList<string> Features,
    long PriceMinor,
    string Currency,
    PurchasePaymentStatus Status,
    PurchaseOrigin Origin,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? PaidAtUtc,
    Guid? PaidByUserId,
    Guid? PurchasedPackageId);

/// <summary>
/// Where to send the money. Every field is configuration, supplied by the coach and never
/// hard-coded, so the destination can change without a deployment of the mobile app.
/// <para>
/// The whole endpoint is <c>503 INSTAPAY_NOT_CONFIGURED</c> until real values are supplied.
/// </para>
/// </summary>
/// <param name="QrImageUrl">
/// Absolute URL of the InstaPay QR code, for display in the app. Served unauthenticated, like
/// the email logo, because an image tag cannot carry a bearer token.
/// </param>
/// <param name="PaymentUrl">
/// The InstaPay destination to open. The app opens this directly; the backend never proxies
/// InstaPay and never sees a payment.
/// </param>
/// <param name="Instructions">
/// Ordered steps to show beside the QR code. A list rather than one blob so the app can render
/// them as steps without parsing text.
/// </param>
public sealed record PaymentInstructionsResponse(
    string? QrImageUrl,
    string? PaymentUrl,
    string? RecipientName,
    string? RecipientHandle,
    IReadOnlyList<string> Instructions);

public static class PackagePurchaseMapping
{
    public static PackagePurchaseResponse ToResponse(this PackagePurchase x) => new(
        x.Id, x.AthleteUserId, x.AthleteProfileId, x.PackageOptionId, x.PackageName,
        x.SessionCount, x.Features, x.PriceMinor, x.Currency, x.Status, x.Origin,
        x.CreatedAtUtc, x.UpdatedAtUtc, x.PaidAtUtc, x.PaidByUserId, x.PurchasedPackageId);
}
