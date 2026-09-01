namespace BeyondMovement.Api.Dashboard;

/// <summary>
/// The UTC half-open interval a <see cref="DashboardPeriod"/> covers: <c>[FromUtc, ToUtc)</c>.
/// Both null means unbounded — All Time.
/// </summary>
public readonly record struct DashboardWindow(DateTime? FromUtc, DateTime? ToUtc)
{
    public static readonly DashboardWindow Unbounded = new(null, null);
}

/// <summary>
/// Turns a dashboard period into the UTC range to query.
/// <para>
/// Pure and static, with the clock and the zone passed in, so the week-start and daylight-saving
/// edges can be tested directly rather than only through an endpoint on whatever date the suite
/// happens to run.
/// </para>
/// <para>
/// <b>Boundaries are the Admin's local calendar, not UTC.</b> The coach is in Egypt (UTC+2/+3):
/// a session at 22:00 local on the last day of a month is 20:00 UTC the same day, but one at
/// 01:00 local on the 1st is 23:00 UTC on the last day of the previous month. Bounding in UTC
/// would file that session under the wrong month, and the coach would count their own week
/// differently from the dashboard. <c>User.TimeZone</c> has been stored since the first
/// migration and never read; this is the first thing to honour it.
/// </para>
/// </summary>
public static class DashboardPeriods
{
    /// <summary>
    /// The week starts <b>Monday</b>. Chosen deliberately and pinned here rather than taken from
    /// the server's culture, which would make the same request answer differently depending on
    /// where it ran.
    /// </summary>
    public const DayOfWeek FirstDayOfWeek = DayOfWeek.Monday;

    /// <summary>
    /// The Admin's zone, or UTC when the stored value is not one this server recognises.
    /// <para>
    /// Falling back rather than throwing is the point: a dashboard that returns 500 because
    /// somebody typed a zone name badly is worse than one that reports UTC and says so — the
    /// response carries the zone actually used, so the fallback is visible rather than silent.
    /// .NET resolves both IANA and Windows ids on either platform through ICU, so a value from
    /// a mobile client ("Africa/Cairo") and one from Windows ("Egypt Standard Time") both work.
    /// </para>
    /// </summary>
    public static TimeZoneInfo Resolve(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return TimeZoneInfo.Utc;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception exception) when (
            exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>
    /// The UTC range for <paramref name="period"/>, as it stands at <paramref name="nowUtc"/> in
    /// <paramref name="zone"/>.
    /// </summary>
    public static DashboardWindow Window(DashboardPeriod period, DateTime nowUtc, TimeZoneInfo zone)
    {
        if (period == DashboardPeriod.AllTime)
            return DashboardWindow.Unbounded;

        var local = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone);

        var (startLocal, endLocal) = period switch
        {
            DashboardPeriod.Weekly => WeekOf(local),
            DashboardPeriod.Monthly => MonthOf(local),
            DashboardPeriod.Yearly => YearOf(local),
            _ => throw new ArgumentOutOfRangeException(nameof(period), period, "Unknown period.")
        };

        return new DashboardWindow(ToUtc(startLocal, zone), ToUtc(endLocal, zone));
    }

    private static (DateTime Start, DateTime End) WeekOf(DateTime local)
    {
        // Sunday is day 0, so without the +7 a Sunday would walk back to a negative offset and
        // land in the wrong week. Sunday belongs to the week that began the previous Monday.
        var daysSinceStart = ((int)local.DayOfWeek - (int)FirstDayOfWeek + 7) % 7;
        var start = local.Date.AddDays(-daysSinceStart);
        return (start, start.AddDays(7));
    }

    private static (DateTime Start, DateTime End) MonthOf(DateTime local)
    {
        var start = new DateTime(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        return (start, start.AddMonths(1));
    }

    private static (DateTime Start, DateTime End) YearOf(DateTime local)
    {
        var start = new DateTime(local.Year, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        return (start, start.AddYears(1));
    }

    /// <summary>
    /// Converts a local boundary to UTC, surviving both daylight-saving edges.
    /// <para>
    /// On a spring-forward night the clock jumps and <b>local midnight may not exist</b> — Egypt
    /// has moved its DST start around, and Cairo has had 00:00 skipped. Asking
    /// <see cref="TimeZoneInfo.ConvertTimeToUtc(DateTime, TimeZoneInfo)"/> to convert a time that
    /// never happened throws, which would take the whole dashboard down on one night a year. The
    /// first instant that does exist is used instead.
    /// </para>
    /// <para>
    /// An ambiguous autumn boundary — an hour that happens twice — needs no special handling:
    /// .NET resolves it to standard time, which is the later of the two and keeps the window from
    /// overlapping the one before it.
    /// </para>
    /// </summary>
    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        // A DST gap is at most a couple of hours; the loop is bounded by that, not by the data.
        while (zone.IsInvalidTime(unspecified))
            unspecified = unspecified.AddMinutes(1);

        return TimeZoneInfo.ConvertTimeToUtc(unspecified, zone);
    }
}
