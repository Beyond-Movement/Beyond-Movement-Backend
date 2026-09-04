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
/// <param name="FullName">
/// Null until the athlete finishes Complete Profile — registration establishes authentication
/// only and never collects a name. Whenever <paramref name="ProfileCompleted"/> is true this
/// is guaranteed non-null and non-blank; the domain refuses to mark a profile complete
/// otherwise, so the app may treat the pair as an invariant rather than re-checking.
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
    string? FullName,
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
/// <param name="FullName">
/// Null until Complete Profile is finished, and guaranteed non-null once
/// <paramref name="ProfileCompleted"/> is true. Same invariant as <see cref="UserSummary"/>.
/// </param>
/// <param name="MinimumSupportedAppVersion">
/// The oldest mobile build this API still supports, for the forced-upgrade prompt.
/// </param>
/// <param name="TimeZone">
/// The zone currently stored for this user, exactly as it was last written — an IANA id such as
/// <c>Africa/Cairo</c>, or <c>UTC</c> for a user whose zone has never been set.
/// <para>
/// Here rather than on the profile because it is <b>device state, not a profile field</b>: there
/// is no time-zone setting in the app and the user never chooses it. On start-up the app detects
/// the device zone, compares it with this value, and calls
/// <c>PUT /api/v1/auth/me/timezone</c> only when the two differ. Without this field that
/// comparison is impossible and the app would have to write on every launch.
/// </para>
/// </param>
public sealed record CurrentUserResponse(
    Guid Id,
    UserRole Role,
    UserStatus Status,
    string? FullName,
    string Email,
    Guid CoachId,
    bool ProfileCompleted,
    AthleteListSort? AthleteListSort,
    string MinimumSupportedAppVersion,
    string TimeZone);

/// <summary>
/// The device's detected zone, pushed by the app rather than chosen by the user.
/// </summary>
/// <param name="TimeZone">
/// An IANA id such as <c>Africa/Cairo</c>. A Windows id is accepted too, but send IANA: it is
/// what a mobile platform reports, and it is what comes back from <c>/auth/me</c> for comparison.
/// Anything this server cannot resolve is refused with <c>TIME_ZONE_INVALID</c>.
/// </param>
public sealed record UpdateTimeZoneRequest(string TimeZone);

/// <summary>
/// The zone as stored after the write — the value a later <c>/auth/me</c> will return, so the
/// app can settle its local copy without a follow-up read.
/// </summary>
public sealed record TimeZoneResponse(string TimeZone);
