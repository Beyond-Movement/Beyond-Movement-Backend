using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.SharedKernel;

namespace BeyondMovement.Modules.Identity;

/// <summary>
/// Stable error codes for this module. The mobile app switches on these strings —
/// renaming one is a contract change (CLAUDE.md section 7).
/// </summary>
public static class IdentityErrors
{
    /// <summary>
    /// Deliberately identical for "no such user" and "wrong password". Different messages
    /// would let an attacker enumerate accounts.
    /// </summary>
    public static readonly Error InvalidCredentials =
        new(ApiErrorCodes.InvalidCredentials, "Email or password is incorrect.", Status.Unauthorized);

    public static readonly Error AccountLocked =
        new(ApiErrorCodes.AccountLocked, "Too many failed attempts. Try again later.", Status.Locked);

    public static readonly Error AccountPaused =
        new(ApiErrorCodes.AccountPaused, "This account is paused.", Status.Forbidden);

    public static readonly Error InvalidRefreshToken =
        new(ApiErrorCodes.InvalidRefreshToken, "The refresh token is invalid or expired.", Status.Unauthorized);

    public static readonly Error InvalidResetToken =
        new(ApiErrorCodes.InvalidResetToken, "The reset link is invalid or has expired.", Status.BadRequest);

    public static readonly Error InvalidGoogleToken =
        new(ApiErrorCodes.InvalidGoogleToken, "The Google sign-in could not be verified.", Status.Unauthorized);

    /// <summary>
    /// BR-01. Google sign-in authenticates; it never registers. A Google account with no
    /// matching user is turned away rather than onboarded.
    /// </summary>
    public static readonly Error InvitationRequired =
        new(ApiErrorCodes.InvitationRequired,
            "This platform is invitation-only. Ask your coach for an invitation.", Status.Forbidden);

    /// <summary>
    /// A Google-only account has no password to change. Such users set their first password
    /// through Forgot Password instead.
    /// </summary>
    public static readonly Error PasswordNotSet =
        new(ApiErrorCodes.PasswordNotSet,
            "This account has no password yet. Use Forgot Password to set one.", Status.BadRequest);

    /// <summary>Lockout with the remaining time, so the app can show a real countdown.</summary>
    public static Error LockedFor(TimeSpan remaining) =>
        AccountLocked with { RetryAfterSeconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds)) };

    private static class Status
    {
        public const int BadRequest = 400;
        public const int Unauthorized = 401;
        public const int Forbidden = 403;
        public const int Locked = 423;
    }
}
