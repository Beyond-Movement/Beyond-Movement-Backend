using BeyondMovement.Modules.Finance.Domain;
using BeyondMovement.SharedKernel;

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
/// <param name="AthleteName">
/// Who the purchase is for, denormalised onto the row so the payments screen can label it
/// without fetching the athlete directory to resolve one field per row.
/// <para>
/// <b>Null for an athlete who registered but never finished Complete Profile</b> — the same
/// state <c>AthleteListItem.FullName</c> reports null for. Fall back to
/// <see cref="AthleteEmail"/>, which is what the athlete list itself shows in that case.
/// </para>
/// <para>
/// Unlike the snapshot fields below, this is the athlete's name <em>now</em>, not as it was when
/// they selected. A purchase records what was bought and for how much; who they are is not part
/// of that bargain, and a renamed athlete should not show an old name on their own history.
/// </para>
/// </param>
/// <param name="AthleteEmail">
/// The fallback label, and always present in practice. Null only if the user record behind the
/// purchase has gone, which nothing in this API can currently do — payment history is kept even
/// then rather than the row being dropped, so an app should treat the pair as
/// <c>athleteName ?? athleteEmail ?? "Athlete"</c> and never assume both are set.
/// </param>
/// <param name="PackageOptionId">
/// Provenance only. Null when the catalogue entry has since been deleted; the snapshot above it
/// is still complete, so the app never needs to follow this id to render the purchase.
/// </param>
/// <param name="PurchasedPackageId">
/// The package this purchase produced. Null while Pending, set once and never changed after.
/// This is the id to hand to <c>GET /api/v1/packages/{id}</c>.
/// </param>
/// <param name="PaidByUserId">
/// The Admin who confirmed payment. Null in three cases, and <b>never a reason to disbelieve
/// <see cref="Status"/></b>: while Pending; on the purchases backfilled onto packages that
/// pre-date this phase, where the confirming user is unknown; and on a purchase whose
/// <see cref="PriceMinor"/> was zero, which completed itself because there was no payment to
/// confirm and so has no confirming Admin to name.
/// <para>
/// Read <see cref="Status"/> and <see cref="PurchasedPackageId"/> to know whether a purchase is
/// done. This field says who, not whether.
/// </para>
/// </param>
public sealed record PackagePurchaseResponse(
    Guid Id,
    Guid AthleteUserId,
    Guid AthleteProfileId,
    string? AthleteName,
    string? AthleteEmail,
    Guid? PackageOptionId,
    string PackageName,
    int SessionCount,
    IReadOnlyList<PackageFeature> Features,
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
    /// <summary>
    /// The athlete's name and email are passed in rather than read here: they live on
    /// <c>Users</c>, which is the Identity module, and this module may not reference it. Every
    /// caller sits in the Api composition root, which is the only project that sees both.
    /// </summary>
    public static PackagePurchaseResponse ToResponse(
        this PackagePurchase x, string? athleteName, string? athleteEmail) => new(
        x.Id, x.AthleteUserId, x.AthleteProfileId, athleteName, athleteEmail,
        x.PackageOptionId, x.PackageName,
        x.SessionCount, x.Features, x.PriceMinor, x.Currency, x.Status, x.Origin,
        x.CreatedAtUtc, x.UpdatedAtUtc, x.PaidAtUtc, x.PaidByUserId, x.PurchasedPackageId);
}

/// <summary>
/// Records or rewrites an expense. One shape for both, because they set the same fields and a
/// second would only drift.
/// <para>
/// A <b>full replacement</b> on PUT, not a patch: send every field every time, and a field left
/// out is one being cleared rather than one being left alone. The same rule the profile, purchase
/// and session-note endpoints follow.
/// </para>
/// <para>
/// There is deliberately no <c>coachId</c> and no <c>currency</c>. The coach comes from the
/// token — an Admin who could send one could file a cost against somebody else — and the currency
/// is server-controlled, exactly as every package price is.
/// </para>
/// </summary>
/// <param name="Title">
/// What the money went on: "Office rental". Required, non-blank, trimmed, at most
/// <see cref="Expense.MaxTitleLength"/> characters.
/// </param>
/// <param name="AmountMinor">
/// Piastres, and strictly <b>greater than zero</b> — a zero-value expense is a typo, not a
/// record. At most <see cref="Expense.MaxAmountMinor"/>, which is ten million pounds and is a
/// guard against a fat-fingered number rather than a business rule.
/// </param>
/// <param name="IncurredOn">
/// The date on the receipt, as <c>YYYY-MM-DD</c>. This is the date the expense counts towards in
/// the summary, not the date it was typed in, so a receipt entered late still lands in the month
/// it belongs to.
/// </param>
/// <param name="Note">
/// Optional context, at most <see cref="Expense.MaxNoteLength"/> characters. Null and an empty
/// string both clear it and both read back as null.
/// </param>
public sealed record SaveExpenseRequest(
    string Title,
    long AmountMinor,
    DateOnly IncurredOn,
    string? Note = null);

/// <summary>
/// One recorded expense.
/// <para>
/// There is no category, no receipt and no athlete on it, and none is coming in this phase: the
/// coach wanted to write down what they spent, not to keep books.
/// </para>
/// </summary>
public sealed record ExpenseResponse(
    Guid Id,
    string Title,
    long AmountMinor,
    string Currency,
    DateOnly IncurredOn,
    string? Note,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public static class ExpenseMapping
{
    public static ExpenseResponse ToResponse(this Expense x) => new(
        x.Id, x.Title, x.AmountMinor, x.Currency, x.IncurredOn, x.Note,
        x.CreatedAtUtc, x.UpdatedAtUtc);
}
