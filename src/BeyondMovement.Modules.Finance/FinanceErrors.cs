using BeyondMovement.SharedKernel;

namespace BeyondMovement.Modules.Finance;

/// <summary>
/// The authoritative error codes for package purchases and payment, published in the OpenAPI
/// document by <c>SchemaNormalizingTransformer</c>.
/// <para>
/// Codes the catalogue and purchase model already own are deliberately reused rather than
/// duplicated with a Finance-flavoured name: selecting an option that does not exist is still
/// <c>PACKAGE_OPTION_NOT_FOUND</c>, selecting an archived one is still
/// <c>PACKAGE_OPTION_ARCHIVED</c>, and an athlete who already has a package still gets
/// <c>ACTIVE_PACKAGE_EXISTS</c>. Two names for one condition is how clients end up handling
/// only one of them.
/// </para>
/// </summary>
public static class FinanceErrorCodes
{
    public const string PurchaseNotFound = "PURCHASE_NOT_FOUND";
    public const string InstaPayNotConfigured = "INSTAPAY_NOT_CONFIGURED";

    /// <summary>
    /// Deliberately <b>absent from <see cref="All"/></b>, so it is not published in the contract.
    /// <para>
    /// No request can produce it. Both places that return it re-ask a question their caller has
    /// already answered: <c>mark-paid</c> returns its idempotent 200 before it ever calls
    /// <c>MarkPaid</c>, and <c>ReviseSelection</c> is only ever handed a row a
    /// <c>WHERE Status = 'Pending'</c> query returned. They are kept as guards so that a later
    /// change which drops an outer check cannot silently overwrite who paid and when — but
    /// publishing a 409 no client can receive only invites handling for an impossible case.
    /// </para>
    /// <para>
    /// If a refactor ever does make one reachable, it must be added here in the same change.
    /// </para>
    /// </summary>
    public const string PurchaseAlreadyPaid = "PURCHASE_ALREADY_PAID";

    public static readonly string[] All =
    [
        PurchaseNotFound, InstaPayNotConfigured
    ];
}

public static class FinanceErrors
{
    /// <summary>
    /// Also returned for a purchase belonging to another coach, and for an athlete asking for a
    /// purchase that is not theirs — the API never confirms that an id it will not serve exists.
    /// </summary>
    public static readonly Error PurchaseNotFound = new(
        FinanceErrorCodes.PurchaseNotFound, "No such purchase.", 404);

    /// <summary>
    /// A guard, not a response. <b>Unreachable by any request</b> and therefore not published in
    /// the contract — see <see cref="FinanceErrorCodes.PurchaseAlreadyPaid"/> for why it is kept.
    /// </summary>
    public static readonly Error PurchaseAlreadyPaid = new(
        FinanceErrorCodes.PurchaseAlreadyPaid,
        "This purchase has already been paid and can no longer be changed.", 409);

    /// <summary>
    /// 503 rather than 404: the endpoint exists and will work once the coach's InstaPay details
    /// are configured. A 404 would tell the app the feature is absent, and it would reasonably
    /// hide the Pay button for good.
    /// </summary>
    public static readonly Error InstaPayNotConfigured = new(
        FinanceErrorCodes.InstaPayNotConfigured,
        "Payment instructions have not been configured yet.", 503);
}
