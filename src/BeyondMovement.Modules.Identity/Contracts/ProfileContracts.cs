namespace BeyondMovement.Modules.Identity.Contracts;

/// <summary>
/// The Admin's own profile — the "Personal Information" screen, and nothing else.
/// <para>
/// Deliberately separate from <see cref="CurrentUserResponse"/>. <c>/auth/me</c> answers "who is
/// signed in and where should the app route them"; it is called on every app start and its
/// fields drive routing, forced upgrade and time-zone sync. Profile fields are read when a
/// screen is opened and written when a form is saved. Growing <c>/auth/me</c> with them would
/// put contact details on the session-restore path and make one response serve two lifetimes.
/// </para>
/// </summary>
/// <param name="FullName">
/// Required, and never blank for an Admin — they are created named and the domain refuses to
/// set an empty one. Nullable in the shape only because it is nullable on the user, where an
/// athlete may exist between registering and completing their profile.
/// </param>
/// <param name="Email">
/// <b>Read-only.</b> Returned so the screen can show it, and deliberately not accepted by
/// <see cref="UpdateAdminProfileRequest"/>: the address is the login identity and the unique key
/// on the users table, so changing it means re-verifying ownership and re-issuing tokens. That
/// is a feature of its own, not a field on a form.
/// </param>
/// <param name="Phone">
/// Null when it has never been given. The column has existed since the first migration and
/// nothing has ever written it, so every profile starts null here.
/// </param>
public sealed record AdminProfileResponse(
    Guid Id,
    string? FullName,
    string Email,
    string? Phone);

/// <summary>
/// A full replacement of the editable fields, not a patch: both are sent every time.
/// <para>
/// There is no email here, by design — see <see cref="AdminProfileResponse.Email"/>.
/// </para>
/// </summary>
/// <param name="FullName">Required. Blank is rejected with <c>VALIDATION_FAILED</c>.</param>
/// <param name="Phone">
/// Optional. Send null — or an empty string, which is treated identically — to clear it.
/// Stored as null either way, so a cleared number reads back as null rather than as "".
/// </param>
public sealed record UpdateAdminProfileRequest(string FullName, string? Phone);
