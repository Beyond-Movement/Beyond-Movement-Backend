using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Scheduling.Contracts;
using BeyondMovement.Modules.Scheduling.Domain;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Api.Scheduling;

/// <summary>
/// Reads observation requests with the athlete labelled on every row.
/// <para>
/// It lives in the composition root for the reason <c>AthleteDirectory</c> and
/// <c>PurchaseReader</c> do: the request belongs to Scheduling, the profile to Athletes and the
/// name to Identity, and a module may not reference another module. The join is written once
/// here rather than in each of the six endpoints that need it — getting it subtly wrong in one
/// of six call sites is the failure worth designing out.
/// </para>
/// <para>
/// The name falls back to the email address, exactly as <c>SchedulingEndpoints.AthleteName</c>
/// does: <c>FullName</c> is null until an athlete completes their profile, and an email
/// identifies them on a queue card where a blank would not.
/// </para>
/// </summary>
public sealed class ObservationRequestReader(AppDbContext db)
{
    /// <summary>
    /// One page of a coach's requests, soonest-requested first. <paramref name="athleteProfileId"/>
    /// narrows to one athlete, which is how the athlete's own list is served — there is no id in
    /// those routes, so it comes from the token and cannot be pointed at anyone else.
    /// </summary>
    public async Task<PagedResult<ObservationRequestResponse>> ListAsync(
        Guid coachId, ObservationRequestStatus? status, Guid? athleteProfileId,
        int page, int pageSize, CancellationToken ct)
    {
        var query = db.ObservationRequests.AsNoTracking().Where(x => x.CoachId == coachId);

        if (status is not null) query = query.Where(x => x.Status == status);
        if (athleteProfileId is not null) query = query.Where(x => x.AthleteProfileId == athleteProfileId);

        var total = await query.CountAsync(ct);

        // Soonest first: a review queue is a list of things about to happen, and the one the
        // coach has least time to answer belongs at the top. The id breaks ties so the order is
        // total and a request cannot appear on two pages.
        //
        // Ordered and paged BEFORE the join, then ordered again after it. Ordering a query that
        // has already been projected into a labelled row is not translatable — the join is what
        // has to be reapplied to, not the projection. Same shape as SchedulingEndpoints.List.
        var pageQuery = query
            .OrderBy(x => x.RequestedStartUtc).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize);

        var rows = await (from request in pageQuery
                          join profile in db.AthleteProfiles on request.AthleteProfileId equals profile.Id
                          join user in db.Users on profile.UserId equals user.Id
                          orderby request.RequestedStartUtc, request.Id
                          select new LabelledRequest(request, user.Id, user.FullName ?? user.Email))
                         .ToListAsync(ct);

        return new PagedResult<ObservationRequestResponse>(
            [.. rows.Select(x => x.Request.ToResponse(x.AthleteUserId, x.AthleteName))],
            page, pageSize, total);
    }

    /// <summary>
    /// One request, already labelled. <paramref name="athleteProfileId"/> is supplied for an
    /// athlete's own read and left null for an Admin's, so a request belonging to someone else
    /// comes back null and the endpoint answers 404 rather than 403 — an id must not be probed
    /// for existence.
    /// </summary>
    public async Task<ObservationRequestResponse?> GetAsync(
        Guid coachId, Guid id, Guid? athleteProfileId, CancellationToken ct)
    {
        var query = db.ObservationRequests.AsNoTracking()
            .Where(x => x.Id == id && x.CoachId == coachId);

        if (athleteProfileId is not null) query = query.Where(x => x.AthleteProfileId == athleteProfileId);

        var row = await Label(query).SingleOrDefaultAsync(ct);

        return row is null ? null : row.Request.ToResponse(row.AthleteUserId, row.AthleteName);
    }

    /// <summary>
    /// Labels a request that has just been written, for the response of a create, edit or
    /// decision. Reads the name after the write rather than inside it: the athlete's name is not
    /// part of what the transaction protects, and the response should show it as it stands now.
    /// </summary>
    public async Task<ObservationRequestResponse> LabelAsync(ObservationRequest request, CancellationToken ct)
    {
        var label = await (from profile in db.AthleteProfiles.AsNoTracking()
                           join user in db.Users on profile.UserId equals user.Id
                           where profile.Id == request.AthleteProfileId
                           select new { UserId = user.Id, Name = user.FullName ?? user.Email })
                          .SingleAsync(ct);

        return request.ToResponse(label.UserId, label.Name);
    }

    private IQueryable<LabelledRequest> Label(IQueryable<ObservationRequest> query) =>
        from request in query
        join profile in db.AthleteProfiles on request.AthleteProfileId equals profile.Id
        join user in db.Users on profile.UserId equals user.Id
        select new LabelledRequest(request, user.Id, user.FullName ?? user.Email);

    private sealed record LabelledRequest(ObservationRequest Request, Guid AthleteUserId, string AthleteName);
}
