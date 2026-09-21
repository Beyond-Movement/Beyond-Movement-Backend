using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Packages.Domain;
using BeyondMovement.Modules.Scheduling.Domain;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Api.Packages;

/// <summary>
/// One session that was deducted from a purchased package, and just enough of it for the screen
/// to render a row and expand it in place.
/// <para>
/// A purpose-built flat shape rather than a nested <c>SessionResponse</c>, for the reason
/// <c>AthleteSessionNoteResponse</c> is one: the full session model carries <c>athleteName</c>,
/// which is redundant on a screen already scoped to one athlete's package, and <c>meetingUrl</c>
/// and <c>rescheduleUrl</c>, which are join links with no business in a history view. Carrying
/// them would invite a client to offer "join" against a session that finished months ago.
/// </para>
/// <para>
/// <b>One shape for both callers.</b> The Admin and the athlete receive the same fields, because
/// every one of them is something the athlete can already read about their own session through
/// <c>GET /sessions/{id}</c>. What is deliberately absent is the Admin's side of the record:
/// <c>AttendedByUserId</c>, the Calendly identifiers, the cancellation reason and the booking
/// links are all left out of both.
/// </para>
/// </summary>
/// <param name="Id">
/// The session, so the app can open <c>GET /api/v1/sessions/{id}</c> from a row. The athlete's
/// own route reaches their own sessions there, exactly as the Admin's does.
/// </param>
/// <param name="Status">
/// <c>Attended</c> or <c>NoShow</c>, and never anything else: a session only appears here because
/// it consumed, and <c>CK_Sessions_ConsumedOnlyWhenResolved</c> allows consumption on those two
/// statuses alone. A no-show is in this list because the coach chose to charge it — that is what
/// makes it part of what the package was spent on.
/// </param>
/// <param name="ConsumedPackagePosition">
/// The one-based position this deduction took in its package — the N in "Session N". Always
/// present, because a row without a deduction is not in this list at all.
/// <para>
/// It is the order the sessions were <b>deducted</b> in, which is the order the coach resolved
/// them. That is almost always the order they happened in, but a coach who marks Thursday's
/// session attended before Tuesday's gives Thursday the lower position. The list is ordered by
/// <paramref name="StartUtc"/> regardless, so read this as the recorded position rather than as
/// the row number.
/// </para>
/// </param>
/// <param name="AttendedAtUtc">
/// When the coach marked it attended. <b>Null for a no-show</b>, which did not attend anything —
/// the same distinction <see cref="Session.AttendedAtUtc"/> keeps, and for the same reason.
/// </param>
/// <param name="HasNotes">
/// Whether this session has at least one session note, so a row can show that there is something
/// to expand into without a request per session. The notes themselves are not here: they are
/// their own resource, at <c>GET /sessions/{id}/notes</c> for the Admin and
/// <c>GET /me/notes</c> for the athlete, and a second copy of them here could disagree with those.
/// </param>
public sealed record PackageSessionResponse(
    Guid Id,
    DateTime StartUtc,
    DateTime EndUtc,
    int DurationMinutes,
    DeliveryType DeliveryType,
    SessionStatus Status,
    string? LocationOrPlatform,
    int ConsumedPackagePosition,
    DateTime? AttendedAtUtc,
    bool HasNotes);

/// <summary>
/// The sessions one purchased package was spent on, oldest first.
/// <para>
/// This lives in the composition root because it spans modules: the package is resolved from
/// Packages, the athlete from Athletes and the sessions from Scheduling, and a module may not
/// reference another (CLAUDE.md section 4). It is the same reason <c>CatalogueReader</c> and
/// <c>SessionNoteHistoryReader</c> live here, and it is read-only.
/// </para>
/// <para>
/// <b>Membership is consumption.</b> A session belongs to this list exactly when it took a
/// session off this package — <c>Session.PackageId</c> is written only by
/// <c>Session.AttachToPackage</c>, which the attendance path calls only when something was
/// actually deducted. So a scheduled session, a cancelled one, a no-show the coach chose not to
/// charge and a non-deducting observation are all absent, and the row count of the whole list is
/// the package's <c>usedSessions</c>. That equality is the point: this is what the athlete's
/// sessions were spent on, not everything that was ever arranged around them.
/// </para>
/// <para>
/// <b>One query, not one per session.</b> The sessions are filtered and paged in the database,
/// and whether each has notes is a correlated subquery in the same statement; nothing here
/// fetches a package's sessions and then walks them.
/// </para>
/// </summary>
public sealed class PackageSessionHistoryReader(AppDbContext db)
{
    /// <summary>
    /// A page of the sessions this package was spent on, or <b>null when the package is unknown
    /// or belongs to another coach</b> — one answer for both, so the caller returns
    /// PACKAGE_NOT_FOUND and an id cannot be probed for existence.
    /// <para>
    /// A package that exists but has consumed nothing yet is an <b>empty page</b>, not null: that
    /// is a real answer and the screen shows its empty view.
    /// </para>
    /// </summary>
    public async Task<PagedResult<PackageSessionResponse>?> ReadAsync(
        Guid coachId, Guid packageId, int page, int pageSize, CancellationToken ct)
    {
        // The coach id comes from the token and is part of this query, so another coach's package
        // simply does not resolve. Resolving it here is the authorisation check.
        var exists = await db.PurchasedPackages.AsNoTracking()
            .AnyAsync(x => x.Id == packageId && x.CoachId == coachId, ct);

        return exists ? await ReadSessionsAsync(coachId, packageId, page, pageSize, ct) : null;
    }

    /// <summary>
    /// The same history for the athlete who owns the package, whose profile id comes from their
    /// token rather than from a route.
    /// <para>
    /// The package must be <em>theirs</em>, not merely this coach's: without the
    /// <c>AthleteProfileId</c> test an athlete holding a valid package id could read any other
    /// athlete's. A package belonging to somebody else is the same null — and therefore the same
    /// 404 — as one that does not exist.
    /// </para>
    /// <para>
    /// Both entry points end in <see cref="ReadSessionsAsync"/> on purpose. One query means the
    /// ordering, the paging and the shape cannot drift apart between the coach's view of a
    /// package and the athlete's view of the same package.
    /// </para>
    /// </summary>
    public async Task<PagedResult<PackageSessionResponse>?> ReadForAthleteAsync(
        Guid coachId, Guid athleteProfileId, Guid packageId, int page, int pageSize,
        CancellationToken ct)
    {
        var owned = await db.PurchasedPackages.AsNoTracking()
            .AnyAsync(x => x.Id == packageId
                           && x.CoachId == coachId
                           && x.AthleteProfileId == athleteProfileId, ct);

        return owned ? await ReadSessionsAsync(coachId, packageId, page, pageSize, ct) : null;
    }

    private async Task<PagedResult<PackageSessionResponse>> ReadSessionsAsync(
        Guid coachId, Guid packageId, int page, int pageSize, CancellationToken ct)
    {
        // ConsumedSessionCount > 0 is redundant beside PackageId — AttachToPackage refuses to
        // write one without the other, and CK_Sessions_PackagePositionMatchesConsumption holds
        // the pair at the database. It is stated anyway because this list's whole meaning is
        // "what the package was spent on", and a reader should not have to know the write path to
        // see that the query says so.
        //
        // CoachId is repeated even though the package resolved under it: it costs nothing, it is
        // served by the same rows, and it means a session that somehow belonged to another coach
        // could never surface here.
        var query = db.Sessions.AsNoTracking()
            .Where(x => x.PackageId == packageId
                        && x.CoachId == coachId
                        && x.ConsumedSessionCount > 0);

        var totalCount = await query.CountAsync(ct);

        var items = await query
            // Oldest first, so the package reads as the course it was: Session 1, then 2, then 3.
            // The id breaks the tie so the order is total. Without it two sessions starting in
            // the same millisecond could swap places between requests, and offset paging would
            // show one of them twice and the other never.
            .OrderBy(x => x.ScheduledStartUtc)
            .ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new PackageSessionResponse(
                x.Id,
                x.ScheduledStartUtc,
                x.ScheduledEndUtc,
                x.DurationMinutes,
                x.DeliveryType,
                x.Status,
                x.LocationOrPlatform,
                // Non-null on every row this query can return: the check constraint ties a
                // position to a consumption, and nothing without a consumption is selected.
                x.ConsumedPackagePosition!.Value,
                x.AttendedAtUtc,
                db.SessionNotes.Any(note => note.SessionId == x.Id)))
            .ToListAsync(ct);

        return new PagedResult<PackageSessionResponse>(items, page, pageSize, totalCount);
    }
}
