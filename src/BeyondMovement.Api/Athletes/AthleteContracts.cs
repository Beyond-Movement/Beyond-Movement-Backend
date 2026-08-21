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
/// Profile. They are still the coach's athlete and still listed. Non-null whenever the athlete
/// has completed their profile.
/// </param>
/// <param name="Email">
/// Always present. It is what the list shows in place of a name while
/// <paramref name="FullName"/> is null, so the row is never blank and the coach can tell which
/// invitee has not finished. Also what search matches for those athletes.
/// </param>
/// <param name="IsLoyal">
/// Whether the coach has marked this athlete loyal, which earns 15% off every package's default
/// price. The discount itself is never shown here — the athlete's catalogue carries final prices.
/// </param>
/// <param name="Status">Account status only. Sessions remaining and "no active package" arrive with purchasing.</param>
public sealed record AthleteListItem(
    Guid Id,
    string? FullName,
    string Email,
    string? Sport,
    bool IsLoyal,
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
    bool IsLoyal,
    UserStatus Status,
    bool ProfileCompleted,
    DateTime CreatedAtUtc);

/// <summary>The outcome of pausing or reactivating, so a list row can update without a refetch.</summary>
public sealed record AthleteStatusResponse(Guid Id, UserStatus Status);

/// <summary>The coach's own UI preferences.</summary>
public sealed record UpdatePreferencesRequest(AthleteListSort AthleteListSort);

public sealed record PreferencesResponse(AthleteListSort? AthleteListSort);
