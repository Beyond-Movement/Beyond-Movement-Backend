using System.Security.Claims;
using BeyondMovement.Api.Dashboard;
using BeyondMovement.Modules.Identity.Contracts;

namespace BeyondMovement.Api.Endpoints;

/// <summary>
/// Admin Home — Phase 9.
/// <para>
/// One aggregate endpoint rather than one per card. The screen renders as a unit, and three
/// separate calls can interleave with a session being marked attended, painting a header that
/// contradicts the list beneath it.
/// </para>
/// <para>
/// <b>Not here yet:</b> package alerts, paid/unpaid totals and expenses. The roadmap lists them
/// under Phase 9 and they remain in scope for the phase — they are deferred only because the
/// current Admin Home does not display them, and expenses are not built at all. Adding them
/// later is additive.
/// </para>
/// </summary>
public static class DashboardEndpoints
{
    private const string ProblemJson = "application/problem+json";

    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/dashboard/admin", Admin)
            .WithTags("Dashboard")
            .RequireAuthorization("AdminOnly")
            .WithName("GetAdminDashboard")
            .WithSummary("Everything the Admin Home screen shows, in one call.")
            .WithDescription(
                "period selects the statistics window and defaults to Monthly. The periods are " +
                "CALENDAR periods, not rolling windows: Weekly is the current week starting " +
                "MONDAY, Monthly is the current month from the 1st, Yearly is the current year " +
                "from 1 January, and AllTime has no date bound. A rolling \"last 7 days\" would " +
                "change yesterday's number today for no reason the coach did. " +
                "Boundaries are computed in the ADMIN'S OWN TIME ZONE (their User.TimeZone) and " +
                "then converted to UTC, so a late-evening session falls in the week the coach " +
                "was working, not the one UTC says. The zone actually used is echoed back as " +
                "statistics.timeZone, and falls back to UTC if the stored value is not " +
                "recognised. fromUtc and toUtc are the resolved window, both null for AllTime; " +
                "toUtc is in the future for a period still running. " +
                "Every statistic counts ATTENDED sessions only, by scheduledStartUtc - the date " +
                "the session happened, not when it was marked. Scheduled, cancelled and NO-SHOW " +
                "sessions are all excluded, so the figures are delivered work. " +
                "onlineSessions, faceToFaceSessions and observationSessions use the identical " +
                "filter and always sum exactly to attendedSessions; onlineMinutes, " +
                "faceToFaceMinutes and observationMinutes do the same and always sum exactly to " +
                "totalMinutes. " +
                "Every minute field is an integer count of MINUTES, summed from each session's " +
                "stored durationMinutes; format for display only (90 as \"1h 30m\") and never do " +
                "arithmetic on a divided value - the same reason money is an integer count of " +
                "piastres. Per-type minutes are sent rather than derived because they cannot be " +
                "recovered from the counts: three online sessions could be 30, 60 and 90 " +
                "minutes, and any average the client invented would be wrong. " +
                "upcomingSessions is INDEPENDENT of period: it is the next scheduled sessions " +
                "from now, exactly as GET /sessions/upcoming defines them, and switching Weekly " +
                "to Yearly never changes it. Defaults to 3, and upcomingLimit is clamped to " +
                "1-20. athleteUserId on each card is the athlete's USER id - the id " +
                "GET /athletes/{athleteId} takes - not the profile id that sessions and packages " +
                "are keyed by.")
            .Produces<AdminDashboardResponse>()
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson);

        return app;
    }

    private static async Task<IResult> Admin(
        AdminDashboardReader reader, ClaimsPrincipal principal, CancellationToken ct,
        DashboardPeriod period = DashboardPeriod.Monthly,
        int upcomingLimit = AdminDashboardReader.DefaultUpcoming)
    {
        if (!principal.TryGetIdentity(out _, out var coachId)) return Results.Unauthorized();

        // Clamped rather than rejected, matching /sessions and the athlete list: a limit outside
        // the range is a client bug that should still render a screen, not a 400 the coach sees.
        var take = Math.Clamp(
            upcomingLimit <= 0 ? AdminDashboardReader.DefaultUpcoming : upcomingLimit,
            1,
            AdminDashboardReader.MaxUpcoming);

        return Results.Ok(await reader.ReadAsync(coachId, period, take, ct));
    }
}
