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
/// Complete Profile, and Edit Profile afterwards — one endpoint serves both, because they set
/// the same fields and a second edit endpoint could only drift from this one. Every field,
/// phone included, is required and enforced server-side, so an athlete cannot reach
/// <c>profileCompleted: true</c> with a half-filled profile by bypassing the app.
/// <para>
/// A <b>full replacement</b>, not a patch: everything is sent every time, and a field left out
/// of the body is refused rather than left alone. The same shape the
/// Admin's <c>PUT /auth/me/profile</c> has, for the same reason.
/// </para>
/// <para>
/// Profile photo is not accepted: it needs file storage, upload and public serving, which is a
/// phase of its own. The app shows initials until then.
/// </para>
/// </summary>
/// <param name="Phone">
/// <b>Required</b>, like every other field here: missing, null, <c>""</c> and whitespace are all
/// <c>400 VALIDATION_FAILED</c>. Accepted as typed, stored and returned in E.164; the rules are
/// shared with the Admin's profile edit — see <see cref="PhonePolicy"/>. An athlete who
/// completed their profile before phone numbers were collected has none on record, and has to
/// supply one on their next save.
/// </param>
/// <param name="SportId">
/// <b>Required</b>: the <c>id</c> of an entry from <c>GET /sports</c>. There is no free-text
/// sport — an athlete whose sport is not listed picks <c>Other</c>. Missing, null and an id the
/// catalogue does not have are all <c>400 VALIDATION_FAILED</c> on <c>SportId</c>. Nullable here
/// only so that a missing value reaches validation and is reported against the field.
/// </param>
public sealed record CompleteProfileRequest(
    string FullName,
    DateOnly DateOfBirth,
    Gender Gender,
    Guid? SportId,
    string Phone);

/// <summary>
/// The athlete's own profile — what the Athlete Profile screen reads, and what an edit returns.
/// </summary>
/// <param name="FullName">
/// Null for an athlete who has registered but not finished Complete Profile, which is the state
/// the read endpoint exists to be honest about. Non-null whenever <paramref name="ProfileCompleted"/>
/// is true; that pairing is an invariant the app relies on, kept by <c>User.MarkProfileCompleted</c>.
/// </param>
/// <param name="Email">
/// <b>Read-only.</b> Returned so the screen can show it, and deliberately not accepted by
/// <see cref="CompleteProfileRequest"/>: the address is the login identity, the unique key on
/// the users table and what Google sign-in matches on, so changing it means re-verifying
/// ownership and re-issuing tokens. That is a feature of its own, not a field on a form.
/// </param>
/// <param name="Phone">
/// E.164. Null only for an athlete who has not saved their profile since phone numbers became
/// required — every save now has to supply one.
/// </param>
/// <param name="SportId">
/// The catalogue id of the athlete's sport — what an edit screen preselects in the sports list.
/// Null exactly when <paramref name="Sport"/> is.
/// </param>
/// <param name="Sport">
/// The sport's display name, read from the catalogue. Null until the athlete picks one; non-null
/// whenever <paramref name="ProfileCompleted"/> is true.
/// </param>
public sealed record AthleteProfileResponse(
    Guid UserId,
    string? FullName,
    string Email,
    string? Phone,
    DateOnly? DateOfBirth,
    Gender? Gender,
    Guid? SportId,
    string? Sport,
    bool ProfileCompleted);
