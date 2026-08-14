using System.Text.Json.Serialization;

namespace BeyondMovement.Modules.Identity.Contracts;

/// <summary>
/// RFC 7807 Problem Details plus the two fields CLAUDE.md section 7 requires on every error.
/// <para>
/// This is a declared type rather than the framework's built-in ProblemDetails so that
/// <c>errorCode</c> and <c>correlationId</c> appear in the generated contract. The mobile app
/// switches on <see cref="ErrorCode"/>; it must never branch on <see cref="Title"/>, which is
/// human-readable text that may change without notice.
/// </para>
/// </summary>
public sealed record ApiProblemDetails
{
    /// <summary>A URI identifying the problem type.</summary>
    public string? Type { get; init; }

    /// <summary>Human-readable summary. For display only — never branch on this.</summary>
    public string? Title { get; init; }

    /// <summary>The HTTP status code, repeated in the body.</summary>
    public int Status { get; init; }

    /// <summary>Optional longer explanation.</summary>
    public string? Detail { get; init; }

    /// <summary>The stable machine-readable code. This is what clients switch on.</summary>
    public required string ErrorCode { get; init; }

    /// <summary>Identifies this exact request in the server logs. Include it in bug reports.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>
    /// Seconds until the caller may retry, when knowable. Present on <c>ACCOUNT_LOCKED</c>;
    /// mirrored in the <c>Retry-After</c> header.
    /// </summary>
    public int? RetryAfterSeconds { get; init; }

    /// <summary>
    /// Per-field validation messages, keyed by property name. Present only on
    /// <c>VALIDATION_FAILED</c>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IDictionary<string, string[]>? Errors { get; init; }
}

/// <summary>
/// Every <c>errorCode</c> this API can return. Kept here so the value set is visible in the
/// generated contract instead of living only in prose.
/// </summary>
public static class ApiErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string AccountLocked = "ACCOUNT_LOCKED";
    public const string AccountPaused = "ACCOUNT_PAUSED";
    public const string InvalidToken = "INVALID_TOKEN";
    public const string InvalidRefreshToken = "INVALID_REFRESH_TOKEN";
    public const string InvalidResetToken = "INVALID_RESET_TOKEN";
    public const string InvalidGoogleToken = "INVALID_GOOGLE_TOKEN";
    public const string InvitationRequired = "INVITATION_REQUIRED";
    public const string PasswordNotSet = "PASSWORD_NOT_SET";

    public const string InvitationInvalid = "INVITATION_INVALID";
    public const string InvitationExpired = "INVITATION_EXPIRED";
    public const string InvitationUsed = "INVITATION_USED";
    public const string InvitationRevoked = "INVITATION_REVOKED";
    public const string RegistrationTokenInvalid = "REGISTRATION_TOKEN_INVALID";
    public const string GoogleEmailMismatch = "GOOGLE_EMAIL_MISMATCH";
    public const string TermsNotAccepted = "TERMS_NOT_ACCEPTED";
    public const string EmailAlreadyRegistered = "EMAIL_ALREADY_REGISTERED";
    public const string ProfileAlreadyCompleted = "PROFILE_ALREADY_COMPLETED";
    public const string TooManyRequests = "TOO_MANY_REQUESTS";

    public static readonly string[] All =
    [
        ValidationFailed, InvalidCredentials, AccountLocked, AccountPaused, InvalidToken,
        InvalidRefreshToken, InvalidResetToken, InvalidGoogleToken, InvitationRequired, PasswordNotSet,
        InvitationInvalid, InvitationExpired, InvitationUsed, InvitationRevoked,
        RegistrationTokenInvalid, GoogleEmailMismatch, TermsNotAccepted, EmailAlreadyRegistered,
        ProfileAlreadyCompleted, TooManyRequests
    ];
}
