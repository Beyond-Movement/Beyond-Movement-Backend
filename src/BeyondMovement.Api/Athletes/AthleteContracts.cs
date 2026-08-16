using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.SharedKernel;

namespace BeyondMovement.Api.Athletes;

// These live in the Api rather than a module: the athlete list joins Users (Identity) to
// AthleteProfiles (Athletes), and modules must not reference one another (CLAUDE.md section 4).
// The Api is the composition root and the only project that knows the full graph.

/// <summary>
/// Which athletes to include. This is <em>account</em> status — whether the athlete can sign in.
/// <para>
/// It is not the Active/Inactive filter in the product specification, which means "has an
/// active package" and is derived from package data. That arrives with phase 4 as a separate
/// parameter; merging the two would make a paused athlete and an athlete between packages
/// indistinguishable (CLAUDE.md section 6).
/// </para>
/// </summary>
public enum AthleteStatusFilter { All, Active, Paused }

/// <summary>One row of the athlete list.</summary>
/// <param name="FullName">
/// Null for an athlete who has been invited and has registered but has not finished Complete
/// Profile. They are still the coach's athlete and still listed; the app shows the email until
/// a name exists. Non-null whenever <c>profileCompleted</c> is true.
/// </param>
/// <param name="Status">Account status only. Sessions remaining and "no active package" arrive in phase 4.</param>
public sealed record AthleteListItem(
    Guid Id,
    string? FullName,
    string? Sport,
    UserStatus Status,
    DateTime CreatedAtUtc);

/// <summary>The Admin's read-only view of one athlete.</summary>
/// <param name="FullName">Null until the athlete completes their profile. See <see cref="AthleteListItem"/>.</param>
/// <param name="Phone">
/// Null for every athlete today — no screen collects a phone number yet. See the changelog.
/// </param>
public sealed record AthleteDetail(
    Guid Id,
    string? FullName,
    string Email,
    string? Phone,
    DateOnly? DateOfBirth,
    Gender? Gender,
    string? Sport,
    UserStatus Status,
    bool ProfileCompleted,
    DateTime CreatedAtUtc);

/// <summary>The outcome of pausing or reactivating, so a list row can update without a refetch.</summary>
public sealed record AthleteStatusResponse(Guid Id, UserStatus Status);

/// <summary>The coach's own UI preferences.</summary>
public sealed record UpdatePreferencesRequest(AthleteListSort AthleteListSort);

public sealed record PreferencesResponse(AthleteListSort? AthleteListSort);
