using BeyondMovement.Modules.Identity.Services;

namespace BeyondMovement.UnitTests.Identity;

/// <summary>
/// The gate in front of <c>Users.TimeZone</c>. It matters more than a validator usually does,
/// because the only reader of that column falls back to UTC <em>silently</em> — so anything this
/// lets through wrong shows up not as an error but as a dashboard quietly reporting the wrong
/// week, and nothing anywhere says why.
/// </summary>
public class TimeZoneIdTests
{
    [Theory]
    [InlineData("Africa/Cairo")]
    [InlineData("Europe/London")]
    [InlineData("Asia/Tokyo")]
    [InlineData("UTC")]
    public void An_iana_zone_is_accepted(string candidate)
    {
        Assert.True(TimeZoneId.TryNormalize(candidate, out var normalized));
        Assert.Equal(candidate, normalized);
    }

    [Fact]
    public void A_windows_zone_is_accepted_too()
    {
        // .NET resolves both forms on either platform through ICU. Mobile sends IANA, but a
        // value that arrived from a Windows host must not be refused.
        Assert.True(TimeZoneId.IsValid("Egypt Standard Time"));
    }

    /// <summary>
    /// The property the whole sync flow rests on. The app compares the device's zone with the
    /// one <c>/auth/me</c> returns and writes only on a difference — so if a write to
    /// "Africa/Cairo" read back as anything else, the two would never agree and the app would
    /// re-sync on every single launch. On Windows,
    /// <c>FindSystemTimeZoneById("Africa/Cairo").Id</c> is "Egypt Standard Time", which is
    /// exactly the rewrite this must not do.
    /// </summary>
    [Fact]
    public void The_callers_own_id_is_preserved_rather_than_the_platforms()
    {
        Assert.True(TimeZoneId.TryNormalize("Africa/Cairo", out var normalized));

        Assert.Equal("Africa/Cairo", normalized);

        // Stated as the round-trip the client actually performs, on whichever OS this runs.
        Assert.True(TimeZoneId.TryNormalize(normalized, out var again));
        Assert.Equal(normalized, again);
    }

    [Fact]
    public void Surrounding_whitespace_is_trimmed_rather_than_rejected()
    {
        Assert.True(TimeZoneId.TryNormalize("  Africa/Cairo  ", out var normalized));
        Assert.Equal("Africa/Cairo", normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("Africa/Cairo; DROP TABLE Users")]
    [InlineData("GMT+3")]
    public void Anything_unresolvable_is_refused(string? candidate)
    {
        Assert.False(TimeZoneId.TryNormalize(candidate, out var normalized));
        Assert.Equal(string.Empty, normalized);
    }

    [Fact]
    public void An_id_too_long_for_the_column_is_refused_before_the_database_sees_it()
    {
        var overlong = new string('x', TimeZoneId.MaxLength + 1);

        Assert.False(TimeZoneId.IsValid(overlong));
    }
}
