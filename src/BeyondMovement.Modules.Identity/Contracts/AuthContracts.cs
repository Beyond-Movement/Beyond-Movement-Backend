using BeyondMovement.Modules.Identity.Domain;

namespace BeyondMovement.Modules.Identity.Contracts;

// Explicit request/response DTOs — EF entities are never serialised (CLAUDE.md section 7).
// These shapes are the contract the Flutter app generates its client from.

public sealed record LoginRequest(string Email, string Password, string? DeviceId = null);

/// <param name="IdToken">The ID token from the native Google sign-in on the device.</param>
public sealed record GoogleSignInRequest(string IdToken, string? DeviceId = null);

public sealed record RefreshRequest(string RefreshToken, string? DeviceId = null);

public sealed record LogoutRequest(string RefreshToken);

public sealed record ForgotPasswordRequest(string Email);

public sealed record ResetPasswordRequest(string Token, string NewPassword);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>
/// Role and status are enums, not free strings, so the generated client gets exact values.
/// They serialise as their names: "Admin"/"Athlete" and "Active"/"Paused"/"Deleted".
/// </summary>
/// <param name="ProfileCompleted">
/// False for an athlete who has an account but has not finished Complete Profile. Present on
/// every authentication response so the app can route straight from login, Google sign-in,
/// refresh or registration without a follow-up call. The Admin has no such step, so it is
/// always true for them.
/// </param>
/// <param name="AthleteListSort">
/// The coach's saved athlete-list order, hydrated at login so the choice survives a restart
/// and follows them to another device (architecture section 6). Null for athletes, who have
/// no such list, and null for a coach who has not chosen one.
/// </param>
public sealed record UserSummary(
    Guid Id,
    UserRole Role,
    UserStatus Status,
    string FullName,
    string Email,
    bool ProfileCompleted,
    AthleteListSort? AthleteListSort);

public sealed record AuthResponse(
    string AccessToken,
    string RefreshToken,
    int ExpiresInSeconds,
    int RefreshExpiresInSeconds,
    UserSummary User);

/// <summary>
/// Everything the app needs to restore a session and decide where to route the user.
/// </summary>
/// <param name="ProfileCompleted">
/// False for an athlete who has created an account but not yet finished Complete Profile.
/// The app routes to Complete Profile rather than Home when this is false.
/// </param>
/// <param name="MinimumSupportedAppVersion">
/// The oldest mobile build this API still supports, for the forced-upgrade prompt.
/// </param>
public sealed record CurrentUserResponse(
    Guid Id,
    UserRole Role,
    UserStatus Status,
    string FullName,
    string Email,
    Guid CoachId,
    bool ProfileCompleted,
    AthleteListSort? AthleteListSort,
    string MinimumSupportedAppVersion);
