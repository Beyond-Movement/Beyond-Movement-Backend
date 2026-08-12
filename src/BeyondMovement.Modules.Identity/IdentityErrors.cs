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
        new("INVALID_CREDENTIALS", "Email or password is incorrect.", StatusCodes.Unauthorized);

    public static readonly Error AccountLocked =
        new("ACCOUNT_LOCKED", "Too many failed attempts. Try again later.", StatusCodes.Locked);

    public static readonly Error AccountPaused =
        new("ACCOUNT_PAUSED", "This account is paused.", StatusCodes.Forbidden);

    public static readonly Error InvalidRefreshToken =
        new("INVALID_REFRESH_TOKEN", "The refresh token is invalid or expired.", StatusCodes.Unauthorized);

    public static readonly Error InvalidResetToken =
        new("INVALID_RESET_TOKEN", "The reset link is invalid or has expired.", StatusCodes.BadRequest);

    private static class StatusCodes
    {
        public const int BadRequest = 400;
        public const int Unauthorized = 401;
        public const int Forbidden = 403;
        public const int Locked = 423;
    }
}
