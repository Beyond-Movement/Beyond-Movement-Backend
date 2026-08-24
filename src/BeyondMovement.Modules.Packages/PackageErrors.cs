using BeyondMovement.SharedKernel;

namespace BeyondMovement.Modules.Packages;

/// <summary>
/// The authoritative error codes for the catalogue, published in the OpenAPI document.
/// <para>
/// Three of the codes the mobile handoff suggested are deliberately absent, because the API
/// already has a code that says the same thing and two names for one condition is how clients
/// end up handling only one of them:
/// </para>
/// <list type="bullet">
/// <item><c>PACKAGE_OPTION_VALIDATION_FAILED</c> and <c>CUSTOM_PRICE_INVALID</c> are both
/// <c>VALIDATION_FAILED</c>, which every endpoint in the API already returns with per-field
/// detail in <c>errors</c>.</item>
/// <item><c>PACKAGE_OPTION_CONFLICT</c> is <c>CONCURRENCY_CONFLICT</c>, which is not
/// package-specific — sessions will raise the identical condition in a later phase, and one
/// code per entity multiplies without telling the client anything new.</item>
/// </list>
/// </summary>
public static class PackageErrorCodes
{
    public const string PackageOptionNotFound = "PACKAGE_OPTION_NOT_FOUND";
    public const string PackageNameConflict = "PACKAGE_NAME_CONFLICT";
    public const string PackageOptionArchived = "PACKAGE_OPTION_ARCHIVED";
    public const string PackageOptionNotArchived = "PACKAGE_OPTION_NOT_ARCHIVED";
    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";
    public const string CustomPriceNotFound = "CUSTOM_PRICE_NOT_FOUND";

    // The purchase model. Distinct from the catalogue codes above on purpose: PACKAGE_NOT_FOUND
    // is a package somebody bought, PACKAGE_OPTION_NOT_FOUND is an entry in the catalogue, and a
    // client that confuses the two shows the wrong screen.
    public const string PackageNotFound = "PACKAGE_NOT_FOUND";
    public const string ActivePackageExists = "ACTIVE_PACKAGE_EXISTS";
    public const string NoSessionsRemaining = "NO_SESSIONS_REMAINING";
    public const string PackageNotActive = "PACKAGE_NOT_ACTIVE";
    public const string PackageAlreadyClosed = "PACKAGE_ALREADY_CLOSED";

    public static readonly string[] All =
    [
        PackageOptionNotFound, PackageNameConflict, PackageOptionArchived,
        PackageOptionNotArchived, ConcurrencyConflict, CustomPriceNotFound,
        PackageNotFound, ActivePackageExists, NoSessionsRemaining, PackageNotActive,
        PackageAlreadyClosed
    ];
}

public static class PackageErrors
{
    public static readonly Error NotFound = new(
        PackageErrorCodes.PackageOptionNotFound, "No such package option.", 404);

    /// <summary>
    /// Case-insensitive, because "8 Sessions" and "8 sessions" are the same package to everyone
    /// except a database collation.
    /// </summary>
    public static readonly Error NameConflict = new(
        PackageErrorCodes.PackageNameConflict,
        "Another package option already uses this name.", 409);

    public static readonly Error Archived = new(
        PackageErrorCodes.PackageOptionArchived,
        "This package option is archived. Restore it before editing.", 409);

    public static readonly Error NotArchived = new(
        PackageErrorCodes.PackageOptionNotArchived,
        "This package option is not archived.", 409);

    public static readonly Error ConcurrencyConflict = new(
        PackageErrorCodes.ConcurrencyConflict,
        "This package option changed since you loaded it. Reload and try again.", 409);

    public static readonly Error CustomPriceNotFound = new(
        PackageErrorCodes.CustomPriceNotFound,
        "This athlete has no custom price for this package option.", 404);

    public static readonly Error PackageNotFound = new(
        PackageErrorCodes.PackageNotFound, "No such package.", 404);

    /// <summary>
    /// BR-03. The database holds this with a partial unique index; the handler checks first only
    /// so the Admin gets this code instead of a constraint violation.
    /// </summary>
    public static readonly Error ActivePackageExists = new(
        PackageErrorCodes.ActivePackageExists,
        "This athlete already has an active package. Close it before starting another.", 409);

    /// <summary>
    /// The athlete has an active package but nothing left in it. Distinct from having no package
    /// at all, which the attendance endpoint reports as PACKAGE_NOT_FOUND, because the coach's
    /// next action differs: renew versus sell a first package.
    /// </summary>
    public static readonly Error NoSessionsRemaining = new(
        PackageErrorCodes.NoSessionsRemaining,
        "This package has no sessions remaining.", 409);

    public static readonly Error PackageNotActive = new(
        PackageErrorCodes.PackageNotActive,
        "This package is no longer active.", 409);

    public static readonly Error PackageAlreadyClosed = new(
        PackageErrorCodes.PackageAlreadyClosed, "This package is already closed.", 409);
}
