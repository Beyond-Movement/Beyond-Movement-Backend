using BeyondMovement.Modules.Identity.Domain;

namespace BeyondMovement.UnitTests.Identity;

/// <summary>
/// The profile and time-zone mutators. Tested on the domain rather than through the endpoints,
/// because the guarantees the mobile contract makes — a completed profile always has a name, a
/// missing phone is null and never "" — have to hold however they are reached, not only through
/// the one caller that validates first today.
/// </summary>
public class UserProfileMutationTests
{
    private static readonly DateTime Now = new(2026, 9, 4, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = Now.AddHours(3);

    private static User NewAdmin() => User.CreateAdmin("coach@example.com", "The Coach", "hash", Now);

    // --------------------------------------------------------------- name

    [Fact]
    public void A_name_is_stored_trimmed_and_stamps_the_row()
    {
        var admin = NewAdmin();

        admin.SetFullName("  Nadia Hassan  ", Later);

        Assert.Equal("Nadia Hassan", admin.FullName);
        Assert.Equal(Later, admin.UpdatedAtUtc);
    }

    /// <summary>
    /// The contract promises that <c>profileCompleted: true</c> implies a non-null name, and the
    /// Admin is complete from creation — so an edit down to blank would break the invariant
    /// after the point <c>MarkProfileCompleted</c> guards. This is the only mutator that could.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_name_cannot_be_edited_away_to_nothing(string blank)
    {
        var admin = NewAdmin();

        Assert.Throws<ArgumentException>(() => admin.SetFullName(blank, Later));

        Assert.Equal("The Coach", admin.FullName);
        Assert.True(admin.ProfileCompleted);
    }

    // -------------------------------------------------------------- phone

    [Fact]
    public void A_phone_number_starts_unset()
    {
        // The column has existed since the first migration and nothing has ever written it.
        Assert.Null(NewAdmin().Phone);
    }

    [Fact]
    public void A_phone_number_is_stored_trimmed()
    {
        var admin = NewAdmin();

        admin.SetPhone("  +20 100 123 4567 ", Later);

        Assert.Equal("+20 100 123 4567", admin.Phone);
        Assert.Equal(Later, admin.UpdatedAtUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Clearing_a_phone_number_stores_null_not_an_empty_string(string? blank)
    {
        var admin = NewAdmin();
        admin.SetPhone("+20 100 123 4567", Now);

        admin.SetPhone(blank, Later);

        // "" would render in the app as a phone number that is set but empty.
        Assert.Null(admin.Phone);
    }

    // ----------------------------------------------------------- time zone

    [Fact]
    public void A_time_zone_is_stored_exactly_as_given_and_stamps_the_row()
    {
        var admin = NewAdmin();

        admin.SetTimeZone("Europe/London", Later);

        Assert.Equal("Europe/London", admin.TimeZone);
        Assert.Equal(Later, admin.UpdatedAtUtc);
    }

    [Fact]
    public void A_time_zone_is_trimmed()
    {
        var admin = NewAdmin();

        admin.SetTimeZone("  Asia/Tokyo  ", Later);

        Assert.Equal("Asia/Tokyo", admin.TimeZone);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_time_zone_is_a_caller_bug_rather_than_bad_input(string blank)
    {
        var admin = User.CreateAdmin("z@example.com", "Zoned", "hash", Now, "Africa/Cairo");

        Assert.Throws<ArgumentException>(() => admin.SetTimeZone(blank, Later));
        Assert.Equal("Africa/Cairo", admin.TimeZone);
    }

    [Fact]
    public void An_athlete_still_starts_on_utc()
    {
        // Nothing reads an athlete's zone today. Pinned so the new mutator cannot be mistaken
        // for a change in who gets a zone by default.
        var athlete = User.CreateAthlete("a@example.com", "Ath Lete", "hash", null, Guid.NewGuid(), Now);

        Assert.Equal("UTC", athlete.TimeZone);
    }
}
