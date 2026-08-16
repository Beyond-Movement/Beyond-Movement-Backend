using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.SharedKernel;

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
/// <para>
/// This establishes authentication and nothing else. The athlete's name and details are
/// collected by <see cref="CompleteProfileRequest"/>, so there is deliberately no name here:
/// two places to set it is two places for them to disagree.
/// </para>
/// </summary>
public sealed record RegisterRequest(
    string RegistrationToken,
    string? Password = null,
    string? GoogleIdToken = null);

/// <summary>
/// Complete Profile. Every field is required and enforced server-side, so an athlete cannot
/// reach <c>profileCompleted: true</c> with a half-filled profile by bypassing the app.
/// <para>
/// Profile photo is not accepted: it needs file storage, which arrives in phase 13. The app
/// shows initials until then.
/// </para>
/// </summary>
public sealed record CompleteProfileRequest(
    string FullName,
    DateOnly DateOfBirth,
    Gender Gender,
    string Sport);

public sealed record AthleteProfileResponse(
    Guid UserId,
    string FullName,
    string Email,
    DateOnly? DateOfBirth,
    Gender? Gender,
    string? Sport,
    bool ProfileCompleted);
