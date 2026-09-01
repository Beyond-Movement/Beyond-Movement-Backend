using BeyondMovement.Modules.Scheduling.Domain;

namespace BeyondMovement.Api.Dashboard;

/// <summary>
/// The statistics window the Admin Home screen offers.
/// <para>
/// These are <b>calendar</b> periods, not rolling windows: Weekly is the current week, not the
/// last seven days. A rolling window makes yesterday's number change today for no reason the
/// coach did, which reads as a bug rather than a report.
/// </para>
/// </summary>
public enum DashboardPeriod
{
    /// <summary>The current calendar week, starting Monday.</summary>
    Weekly,

    /// <summary>The current calendar month, from the 1st.</summary>
    Monthly,

    /// <summary>The current calendar year, from 1 January.</summary>
    Yearly,

    /// <summary>Every attended session ever, with no date bound at all.</summary>
    AllTime
}

/// <summary>
/// Everything the Admin Home screen needs, in one call.
/// <para>
/// One aggregate endpoint rather than three, because the screen renders as a unit: three
/// requests can interleave with a session being marked attended and paint a header that
/// disagrees with the list below it.
/// </para>
/// </summary>
public sealed record AdminDashboardResponse(
    DashboardStatistics Statistics,
    IReadOnlyList<UpcomingSessionCard> UpcomingSessions);

/// <summary>
/// Delivery statistics for the selected period.
/// <para>
/// Every count here is <b>attended sessions only</b>. Scheduled sessions have not happened,
/// cancelled ones never will (BR-06), and a no-show is deliberately excluded — the coach's time
/// was spent but the session was not delivered, and a figure labelled "attended" that counts
/// absences is a figure nobody can reconcile.
/// </para>
/// </summary>
/// <param name="TimeZone">
/// The IANA or Windows zone the period boundaries were computed in — the Admin's own
/// <c>User.TimeZone</c>. Returned so the app can label the window without guessing, and so a
/// wrong-looking week is diagnosable rather than mysterious. Falls back to <c>UTC</c> when the
/// stored value is not a zone this server recognises.
/// </param>
/// <param name="FromUtc">
/// Inclusive start of the window, in UTC. <b>Null for AllTime</b>, which has no lower bound.
/// </param>
/// <param name="ToUtc">
/// Exclusive end of the window, in UTC. Null for AllTime. For the current period this is in the
/// future — the end of this week, month or year — because the window is the calendar period, not
/// the part of it that has already happened.
/// </param>
/// <param name="TotalMinutes">
/// Coaching time as an integer count of <b>minutes</b>, summed from each session's stored
/// <c>DurationMinutes</c> (open decision A-02, resolved: the duration on the session is the
/// source). Minutes rather than decimal hours for the same reason money is an integer count of
/// piastres — a decimal in JSON becomes a Dart <c>double</c>, and the error that is invisible on
/// one value shows up the first time a column is summed. Divide by 60 to display; never do
/// arithmetic on the divided value.
/// </param>
/// <param name="OnlineSessions">
/// The three delivery counts use the identical attended-only filter as
/// <paramref name="AttendedSessions"/>, so they always sum to it exactly. A breakdown that does
/// not add up to its own total is a bug report waiting to happen.
/// </param>
public sealed record DashboardStatistics(
    DashboardPeriod Period,
    string TimeZone,
    DateTime? FromUtc,
    DateTime? ToUtc,
    int AttendedSessions,
    int TotalMinutes,
    int OnlineSessions,
    int FaceToFaceSessions,
    int ObservationSessions);

/// <summary>
/// One card in the Admin Home "upcoming sessions" list.
/// <para>
/// <b>Independent of the statistics period.</b> Switching Weekly to Yearly changes the numbers
/// above and never this list: what is coming next does not depend on how far back the coach is
/// looking.
/// </para>
/// </summary>
/// <param name="AthleteUserId">
/// The athlete's <b>USER</b> id — what <c>GET /api/v1/athletes/{athleteId}</c> takes, so this is
/// the id to navigate to Athlete Profile with. It is deliberately <em>not</em> the profile id
/// that sessions and packages are keyed by; the two are different rows and are not
/// interchangeable. Named in full rather than a bare <c>athleteId</c> so the distinction cannot
/// be missed at the call site.
/// </param>
/// <param name="AthleteName">
/// Never null. Falls back to the athlete's email while <c>fullName</c> is null, because a
/// session can exist before its athlete has completed a profile and a blank card names nobody.
/// </param>
/// <param name="DeliveryType">
/// Named to match <c>SessionResponse.deliveryType</c> rather than introducing a second word for
/// one concept.
/// </param>
public sealed record UpcomingSessionCard(
    Guid SessionId,
    Guid AthleteUserId,
    string AthleteName,
    DateTime ScheduledStartUtc,
    DateTime ScheduledEndUtc,
    int DurationMinutes,
    DeliveryType DeliveryType);
