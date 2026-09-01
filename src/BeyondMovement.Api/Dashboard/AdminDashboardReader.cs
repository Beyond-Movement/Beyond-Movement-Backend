using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Scheduling.Domain;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Api.Dashboard;

/// <summary>
/// The Admin Home read model. Spans Scheduling (sessions), Athletes (profiles) and Identity
/// (names, time zone), so it lives in the composition root and only ever reads — the same
/// arrangement as <c>AthleteDirectory</c> and <c>CatalogueReader</c> (CLAUDE.md section 4).
/// <para>
/// <b>No new domain rules are defined here.</b> "Delivered" is
/// <see cref="SessionStatus.Attended"/>, which is the status <c>Session.Resolve</c> writes and
/// the one <c>AttendedAtUtc</c> is stamped for; "upcoming" is the definition
/// <c>GET /sessions/upcoming</c> already publishes — <see cref="SessionStatus.Scheduled"/> from
/// now forward, so a cancelled session can never appear. Duration is the session's own stored
/// <c>DurationMinutes</c>.
/// </para>
/// <para>
/// <b>Two queries, whatever the data.</b> The statistics are one grouped aggregate evaluated in
/// the database, and the upcoming cards are one join — neither loops, so nothing here grows a
/// query per row.
/// </para>
/// </summary>
public sealed class AdminDashboardReader(AppDbContext db, IClock clock)
{
    /// <summary>
    /// What the Admin Home screen shows by default. Three fits the card list without scrolling.
    /// </summary>
    public const int DefaultUpcoming = 3;

    public const int MaxUpcoming = 20;

    public async Task<AdminDashboardResponse> ReadAsync(
        Guid coachId,
        DashboardPeriod period,
        int upcomingLimit,
        CancellationToken ct)
    {
        var nowUtc = clock.UtcNow;

        // The Admin's own zone decides where a week or a month begins. Read from the coach's
        // user row rather than a setting, because that is where it has always been stored.
        var zoneId = await db.Users.AsNoTracking()
            .Where(x => x.Id == coachId)
            .Select(x => x.TimeZone)
            .SingleOrDefaultAsync(ct);

        var zone = DashboardPeriods.Resolve(zoneId);
        var window = DashboardPeriods.Window(period, nowUtc, zone);

        var statistics = await ReadStatisticsAsync(coachId, period, zone, window, ct);
        var upcoming = await ReadUpcomingAsync(coachId, nowUtc, upcomingLimit, ct);

        return new AdminDashboardResponse(statistics, upcoming);
    }

    /// <summary>
    /// One grouped aggregate: counts and summed minutes per delivery type, computed in Postgres
    /// rather than by pulling sessions back and counting them here. The totals are folded from
    /// that single result, so the breakdown and the total cannot disagree — they are the same
    /// rows added up two ways.
    /// </summary>
    private async Task<DashboardStatistics> ReadStatisticsAsync(
        Guid coachId,
        DashboardPeriod period,
        TimeZoneInfo zone,
        DashboardWindow window,
        CancellationToken ct)
    {
        var query = db.Sessions.AsNoTracking()
            .Where(x => x.CoachId == coachId && x.Status == SessionStatus.Attended);

        // ScheduledStartUtc, not AttendedAtUtc: a session belongs to the period it happened in,
        // not to whenever the coach got around to marking it. Half-open, so a session exactly on
        // a boundary lands in one period and never in both.
        if (window.FromUtc is { } from) query = query.Where(x => x.ScheduledStartUtc >= from);
        if (window.ToUtc is { } to) query = query.Where(x => x.ScheduledStartUtc < to);

        var byType = await query
            .GroupBy(x => x.DeliveryType)
            .Select(group => new
            {
                DeliveryType = group.Key,
                Sessions = group.Count(),
                Minutes = group.Sum(x => x.DurationMinutes)
            })
            .ToListAsync(ct);

        int CountOf(DeliveryType type) =>
            byType.SingleOrDefault(x => x.DeliveryType == type)?.Sessions ?? 0;

        return new DashboardStatistics(
            period,
            zone.Id,
            window.FromUtc,
            window.ToUtc,
            byType.Sum(x => x.Sessions),
            byType.Sum(x => x.Minutes),
            CountOf(DeliveryType.Online),
            CountOf(DeliveryType.FaceToFace),
            CountOf(DeliveryType.Observation));
    }

    /// <summary>
    /// The next few scheduled sessions, exactly as <c>GET /sessions/upcoming</c> defines them.
    /// <para>
    /// Deliberately takes no window: this is what is coming next, and it must not move when the
    /// coach switches the statistics filter from Weekly to Yearly.
    /// </para>
    /// <para>
    /// The athlete's name and user id come from one join rather than a lookup per card. The name
    /// falls back to the email while <c>FullName</c> is null, matching every other place a
    /// session is rendered.
    /// </para>
    /// </summary>
    private Task<List<UpcomingSessionCard>> ReadUpcomingAsync(
        Guid coachId, DateTime nowUtc, int limit, CancellationToken ct) =>
        (from session in db.Sessions.AsNoTracking()
         join profile in db.AthleteProfiles on session.AthleteProfileId equals profile.Id
         join user in db.Users on profile.UserId equals user.Id
         where session.CoachId == coachId
               && session.Status == SessionStatus.Scheduled
               && session.ScheduledStartUtc >= nowUtc
         orderby session.ScheduledStartUtc, session.Id
         select new UpcomingSessionCard(
             session.Id,
             user.Id,
             user.FullName ?? user.Email,
             session.ScheduledStartUtc,
             session.ScheduledEndUtc,
             session.DurationMinutes,
             session.DeliveryType))
        .Take(limit)
        .ToListAsync(ct);
}
