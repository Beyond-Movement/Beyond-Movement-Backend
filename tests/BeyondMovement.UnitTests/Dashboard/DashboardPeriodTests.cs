using BeyondMovement.Api.Dashboard;

namespace BeyondMovement.UnitTests.Dashboard;

/// <summary>
/// The period arithmetic, tested directly rather than through the endpoint.
/// <para>
/// These are the cases an integration test cannot reach on demand: a Sunday, a daylight-saving
/// boundary, a month end that falls on a different day in the coach's zone than in UTC. Driving
/// the maths with a fixed "now" is the only way to assert them without waiting for the calendar.
/// </para>
/// </summary>
public sealed class DashboardPeriodTests
{
    /// <summary>Cairo — the coach's actual zone, UTC+2 and UTC+3 across a DST change.</summary>
    private static TimeZoneInfo Cairo => DashboardPeriods.Resolve("Africa/Cairo");

    [Fact]
    public void All_time_has_no_bounds()
    {
        var window = DashboardPeriods.Window(
            DashboardPeriod.AllTime, new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc), Cairo);

        Assert.Null(window.FromUtc);
        Assert.Null(window.ToUtc);
    }

    [Theory]
    // Monday itself starts the week...
    [InlineData("2026-03-09T09:00:00Z", "2026-03-09", "2026-03-16")]
    // ...midweek resolves back to it...
    [InlineData("2026-03-12T09:00:00Z", "2026-03-09", "2026-03-16")]
    // ...and Sunday belongs to the week that began the PREVIOUS Monday, which is the case a
    // naive (DayOfWeek - Monday) would push into the wrong week.
    [InlineData("2026-03-15T09:00:00Z", "2026-03-09", "2026-03-16")]
    public void The_week_starts_on_monday(string nowUtc, string expectedStart, string expectedEnd)
    {
        var window = DashboardPeriods.Window(
            DashboardPeriod.Weekly, DateTime.Parse(nowUtc).ToUniversalTime(), Cairo);

        // Cairo is UTC+2 in March, so local midnight is 22:00 UTC the day before.
        Assert.Equal(DateTime.Parse(expectedStart + "T00:00:00").AddHours(-2), window.FromUtc);
        Assert.Equal(DateTime.Parse(expectedEnd + "T00:00:00").AddHours(-2), window.ToUtc);
    }

    [Fact]
    public void The_month_runs_from_the_first_to_the_first()
    {
        var window = DashboardPeriods.Window(
            DashboardPeriod.Monthly, new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc), Cairo);

        Assert.Equal(new DateTime(2026, 2, 28, 22, 0, 0, DateTimeKind.Utc), window.FromUtc);
        Assert.Equal(new DateTime(2026, 3, 31, 22, 0, 0, DateTimeKind.Utc), window.ToUtc);
    }

    [Fact]
    public void The_year_runs_from_january_to_january()
    {
        var window = DashboardPeriods.Window(
            DashboardPeriod.Yearly, new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc), Cairo);

        Assert.Equal(new DateTime(2025, 12, 31, 22, 0, 0, DateTimeKind.Utc), window.FromUtc);
        Assert.Equal(new DateTime(2026, 12, 31, 22, 0, 0, DateTimeKind.Utc), window.ToUtc);
    }

    /// <summary>
    /// The reason boundaries are computed in the coach's zone at all. A session at 00:30 Cairo on
    /// 1 March is 22:30 UTC on 28 February: bounding in UTC files the coach's March session under
    /// February, and their own count disagrees with the dashboard.
    /// </summary>
    [Fact]
    public void A_local_month_boundary_is_not_the_utc_one()
    {
        var window = DashboardPeriods.Window(
            DashboardPeriod.Monthly, new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc), Cairo);

        var justAfterLocalMidnight = new DateTime(2026, 2, 28, 22, 30, 0, DateTimeKind.Utc);

        Assert.True(justAfterLocalMidnight >= window.FromUtc,
            "a session at 00:30 Cairo on 1 March belongs to March, not February");
    }

    /// <summary>
    /// UTC is a real configuration, not just a fallback, and must give plain midnight bounds.
    /// </summary>
    [Fact]
    public void Utc_boundaries_are_plain_midnight()
    {
        var window = DashboardPeriods.Window(
            DashboardPeriod.Monthly,
            new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc),
            TimeZoneInfo.Utc);

        Assert.Equal(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), window.FromUtc);
        Assert.Equal(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), window.ToUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Not/A/Zone")]
    [InlineData("Mars Standard Time")]
    public void An_unrecognised_zone_falls_back_to_utc_rather_than_throwing(string? zoneId)
    {
        // A dashboard that 500s because somebody typed a zone badly is worse than one that
        // reports UTC and says so in the response.
        Assert.Equal(TimeZoneInfo.Utc, DashboardPeriods.Resolve(zoneId));
    }

    [Fact]
    public void Windows_and_iana_zone_ids_both_resolve()
    {
        Assert.NotEqual(TimeZoneInfo.Utc, DashboardPeriods.Resolve("Africa/Cairo"));
        Assert.NotEqual(TimeZoneInfo.Utc, DashboardPeriods.Resolve("Egypt Standard Time"));
    }

    /// <summary>
    /// The one night a year local midnight may not exist. Converting a time that never happened
    /// throws, which would take the whole dashboard down; the window must still resolve.
    /// </summary>
    [Fact]
    public void A_daylight_saving_gap_does_not_break_the_window()
    {
        var cairo = Cairo;

        // Find a real DST transition rather than hard-coding a date the tz database may move.
        var gapDay = Enumerable.Range(0, 365 * 3)
            .Select(offset => new DateTime(2025, 1, 1).AddDays(offset))
            .FirstOrDefault(day => cairo.IsInvalidTime(day.Date));

        if (gapDay == default)
            return;   // no midnight gap in this tz database; nothing to assert

        var nowUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(gapDay.Date.AddHours(12), DateTimeKind.Unspecified), cairo);

        var exception = Record.Exception(() =>
            DashboardPeriods.Window(DashboardPeriod.Weekly, nowUtc, cairo));

        Assert.Null(exception);
    }

    /// <summary>Every window is half-open and non-empty, so nothing is double counted.</summary>
    [Theory]
    [InlineData(DashboardPeriod.Weekly)]
    [InlineData(DashboardPeriod.Monthly)]
    [InlineData(DashboardPeriod.Yearly)]
    public void Windows_are_half_open_and_ordered(DashboardPeriod period)
    {
        var window = DashboardPeriods.Window(
            period, new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc), Cairo);

        Assert.NotNull(window.FromUtc);
        Assert.NotNull(window.ToUtc);
        Assert.True(window.ToUtc > window.FromUtc);
    }
}
