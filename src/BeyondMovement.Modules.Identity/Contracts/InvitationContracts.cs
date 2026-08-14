using BeyondMovement.Modules.Identity.Domain;

namespace BeyondMovement.Modules.Identity.Contracts;

public sealed record CreateInvitationRequest(string Email);

/// <summary>What the Admin sees. The raw code is never returned — only the athlete's inbox gets it.</summary>
public sealed record InvitationResponse(
    Guid Id,
    string Email,
    InvitationStatus Status,
    DateTime ExpiresAtUtc,
    DateTime CreatedAtUtc,
    DateTime? RedeemedAtUtc,
    int SendCount);

/// <summary>
/// The answer to a valid code. <paramref name="RegistrationToken"/> is what Create Account
/// posts back — the code itself is not reused.
/// </summary>
public sealed record ValidateInvitationResponse(
    string Email,
    DateTime ExpiresAtUtc,
    string RegistrationToken,
    int RegistrationTokenExpiresInSeconds);

/// <summary>
/// Create Account. Supply exactly one of <paramref name="Password"/> or
/// <paramref name="GoogleIdToken"/>.
/// </summary>
/// <param name="FullName">
/// Required with a password; optional with Google, which supplies a display name that the
/// athlete confirms on Complete Profile.
/// </param>
public sealed record RegisterRequest(
    string RegistrationToken,
    bool TermsAccepted,
    string? Password = null,
    string? GoogleIdToken = null,
    string? FullName = null);

public sealed record CompleteProfileRequest(
    string FullName,
    DateOnly? DateOfBirth = null,
    string? Gender = null,
    string? Sport = null);

public sealed record AthleteProfileResponse(
    Guid UserId,
    string FullName,
    string Email,
    DateOnly? DateOfBirth,
    string? Gender,
    string? Sport,
    bool ProfileCompleted);
