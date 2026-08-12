namespace BeyondMovement.Modules.Identity.Contracts;

// Explicit request/response DTOs — EF entities are never serialised (CLAUDE.md section 7).
// These shapes are the contract the Flutter app generates its client from.

public sealed record LoginRequest(string Email, string Password, string? DeviceId = null);

public sealed record RefreshRequest(string RefreshToken, string? DeviceId = null);

public sealed record LogoutRequest(string RefreshToken);

public sealed record ForgotPasswordRequest(string Email);

public sealed record ResetPasswordRequest(string Token, string NewPassword);

public sealed record UserSummary(Guid Id, string Role, string FullName, string Email);

public sealed record AuthResponse(
    string AccessToken,
    string RefreshToken,
    int ExpiresInSeconds,
    UserSummary User);
