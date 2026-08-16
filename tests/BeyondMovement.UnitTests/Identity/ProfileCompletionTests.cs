using BeyondMovement.Modules.Identity.Domain;

namespace BeyondMovement.UnitTests.Identity;

/// <summary>
/// The contract promises the mobile app that <c>profileCompleted: true</c> implies a non-null
/// <c>fullName</c>. That promise is only worth making if the domain cannot break it, so these
/// test the guard rather than the endpoint that happens to call it correctly today.
/// </summary>
public class ProfileCompletionTests
{
    private static readonly DateTime Now = new(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);

    private static User NewAthlete(string? fullName) =>
        User.CreateAthlete("athlete@example.com", fullName, "hash", null, Guid.NewGuid(), Now);

    [Fact]
    public void A_password_registration_starts_with_no_name()
    {
        var user = NewAthlete(null);

        // Not an empty string: the contract says null, and "" would read as a name that is set.
        Assert.Null(user.FullName);
        Assert.False(user.ProfileCompleted);
    }

    [Fact]
    public void A_google_registration_keeps_the_supplied_name_as_a_prefill()
    {
        var user = NewAthlete("Jordan Blake");

        // Having a name is not the same as having completed the profile.
        Assert.Equal("Jordan Blake", user.FullName);
        Assert.False(user.ProfileCompleted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_profile_cannot_be_marked_complete_without_a_name(string? fullName)
    {
        var user = NewAthlete(fullName);

        Assert.Throws<InvalidOperationException>(() => user.MarkProfileCompleted(Now));
        Assert.False(user.ProfileCompleted);
    }

    [Fact]
    public void Naming_the_athlete_first_is_what_makes_completion_possible()
    {
        var user = NewAthlete(null);

        user.SetFullName("Alex Thompson", Now);
        user.MarkProfileCompleted(Now);

        Assert.True(user.ProfileCompleted);
        Assert.Equal("Alex Thompson", user.FullName);
    }

    [Fact]
    public void The_admin_is_complete_from_the_moment_it_exists()
    {
        var admin = User.CreateAdmin("coach@example.com", "The Coach", "hash", Now);

        // There is no Complete Profile step for the coach, so the app must never route them there.
        Assert.True(admin.ProfileCompleted);
        Assert.Equal("The Coach", admin.FullName);
    }

    [Fact]
    public void Completing_twice_keeps_the_first_timestamp()
    {
        var user = NewAthlete("Alex Thompson");

        user.MarkProfileCompleted(Now);
        user.MarkProfileCompleted(Now.AddDays(30));

        // Editing the profile later must not look like completing it later.
        Assert.Equal(Now, user.ProfileCompletedAtUtc);
    }
}
