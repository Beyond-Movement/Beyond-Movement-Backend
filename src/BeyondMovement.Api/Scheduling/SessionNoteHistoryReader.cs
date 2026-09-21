using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Scheduling.Domain;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Api.Scheduling;

/// <summary>
/// One entry in an athlete's Session Notes History: the note, and just enough of the session it
/// belongs to for the screen to label it and link back.
/// <para>
/// A purpose-built flat shape rather than a nested <c>SessionResponse</c>, for the reason
/// <c>AthletePricingItem</c> is a different shape from <c>CatalogueItemResponse</c>: the full
/// session model carries <c>athleteName</c>, which is redundant on a screen already scoped to one
/// athlete, and <c>meetingUrl</c> and <c>rescheduleUrl</c>, which are join links with no business
/// in a history view. Carrying them would invite a client to offer "join" against a session that
/// finished months ago.
/// </para>
/// <para>
/// Everything the row needs is here, so the screen renders a page of history <b>without a second
/// request per session</b>.
/// </para>
/// </summary>
/// <param name="SessionId">
/// The session this note belongs to, kept on every row so the app can navigate
/// <c>Session Note → Session Details</c> with <c>GET /api/v1/sessions/{sessionId}</c>.
/// </param>
/// <param name="SessionDeliveryType">
/// <c>Online</c>, <c>FaceToFace</c> or <c>Observation</c>. <b>An observation is simply one
/// delivery type</b> — its notes are ordinary session notes and appear in this history beside the
/// others. There is deliberately no <c>isObservation</c> flag: it would be this field compared to
/// one value, and a second copy of one fact is how two fields end up disagreeing.
/// </param>
/// <param name="CreatedAtUtc">
/// When the note was first written, and what the history is ordered by. It stays put when a note
/// is edited, so correcting last week's note today does not move it to the top.
/// </param>
public sealed record AthleteSessionNoteResponse(
    Guid Id,
    Guid SessionId,
    string Title,
    string Content,
    Guid AuthorUserId,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime SessionStartUtc,
    DateTime SessionEndUtc,
    DeliveryType SessionDeliveryType,
    SessionStatus SessionStatus,
    string? SessionLocationOrPlatform);

/// <summary>
/// Every session note belonging to one athlete, newest first — the Admin's Session Notes History.
/// <para>
/// This lives in the composition root because it spans modules: the athlete is resolved from
/// Athletes and the notes and sessions come from Scheduling, and a module may not reference
/// another (CLAUDE.md section 4). It is the same reason <c>CatalogueReader</c> and
/// <c>PurchaseReader</c> live here, and it is read-only.
/// </para>
/// <para>
/// <b>One query, not one per session.</b> The notes are joined to their sessions and paged in the
/// database; nothing here fetches the athlete's sessions and then walks them.
/// </para>
/// <para>
/// <b>Add and Edit Session Note remain the only source of these rows.</b> This is a read model
/// over the notes those endpoints already write — there is no second notes system, and nothing
/// here can create, change or delete a note.
/// </para>
/// </summary>
public sealed class SessionNoteHistoryReader(AppDbContext db)
{
    /// <summary>
    /// A page of this athlete's notes, or <b>null when the athlete is unknown, belongs to another
    /// coach, or has been deleted</b> — one answer for all three, so the caller returns
    /// ATHLETE_NOT_FOUND and an id cannot be probed for existence.
    /// <para>
    /// An athlete who exists but has no notes is an <b>empty page</b>, not null: that is a real
    /// answer and the screen shows its empty view.
    /// </para>
    /// </summary>
    public async Task<PagedResult<AthleteSessionNoteResponse>?> ReadAsync(
        Guid coachId, Guid athleteUserId, int page, int pageSize, CancellationToken ct)
    {
        // The route takes the athlete's USER id, as every /athletes/{athleteId} route does, while
        // sessions are keyed by profile id. Resolving it here is also the authorisation check:
        // the coach id comes from the token and is part of this query, so another coach's athlete
        // simply does not resolve.
        var athleteProfileId = await db.AthleteProfiles.AsNoTracking()
            .Where(x => x.UserId == athleteUserId
                        && x.CoachId == coachId
                        && x.DeletedAtUtc == null)
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(ct);

        if (athleteProfileId is null) return null;

        return await ReadForProfileAsync(coachId, athleteProfileId.Value, page, pageSize, ct);
    }

    /// <summary>
    /// The same history for an athlete whose profile id the caller already has — which is the
    /// athlete reading their own through <c>GET /me/notes</c>, where the profile comes from the
    /// token rather than from a route.
    /// <para>
    /// Both entry points end here on purpose. One query means the ordering, the paging and the
    /// shape cannot drift apart between the coach's view of a history and the athlete's view of
    /// the same history.
    /// </para>
    /// </summary>
    public async Task<PagedResult<AthleteSessionNoteResponse>> ReadForProfileAsync(
        Guid coachId, Guid athleteProfileId, int page, int pageSize, CancellationToken ct)
    {
        // Notes reach their athlete only through their session, so the filter is on the session.
        // CoachId is repeated here even though both callers resolved the athlete under it: it
        // costs nothing, it is served by the same rows, and it means a session that somehow
        // belonged to another coach could never surface a note through this path.
        var query =
            from note in db.SessionNotes.AsNoTracking()
            join session in db.Sessions.AsNoTracking() on note.SessionId equals session.Id
            where session.AthleteProfileId == athleteProfileId && session.CoachId == coachId
            select new { Note = note, Session = session };

        var totalCount = await query.CountAsync(ct);

        var items = await query
            // Newest first, and the id breaks the tie so the order is total. Without it two notes
            // written in the same millisecond could swap places between pages, and offset paging
            // would show one of them twice and the other never.
            .OrderByDescending(x => x.Note.CreatedAtUtc)
            .ThenByDescending(x => x.Note.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new AthleteSessionNoteResponse(
                x.Note.Id,
                x.Note.SessionId,
                x.Note.Title,
                x.Note.Content,
                x.Note.AuthorUserId,
                x.Note.CreatedAtUtc,
                x.Note.UpdatedAtUtc,
                x.Session.ScheduledStartUtc,
                x.Session.ScheduledEndUtc,
                x.Session.DeliveryType,
                x.Session.Status,
                x.Session.LocationOrPlatform))
            .ToListAsync(ct);

        return new PagedResult<AthleteSessionNoteResponse>(items, page, pageSize, totalCount);
    }
}
