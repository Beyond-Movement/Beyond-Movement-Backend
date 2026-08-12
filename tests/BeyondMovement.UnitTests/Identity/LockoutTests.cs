using BeyondMovement.Modules.Identity.Domain;

namespace BeyondMovement.UnitTests.Identity;

public class LockoutTests
{
    private static readonly DateTime Now = new(2026, 8, 12, 10, 0, 0, DateTimeKind.Utc);

    private static User NewAdmin() =>
        User.CreateAdmin("admin@beyondmovement.com", "Admin", "hash", Now);

    [Fact]
    public void Four_failures_do_not_lock_the_account()
    {
        var user = NewAdmin();

        for (var i = 0; i < 4; i++)
            user.RecordFailedLogin(Now);

        Assert.False(user.IsLockedOut(Now));
        Assert.Equal(4, user.FailedLoginAttempts);
    }

    [Fact]
    public void Fifth_failure_locks_the_account()
    {
        var user = NewAdmin();

        for (var i = 0; i < 5; i++)
            user.RecordFailedLogin(Now);

        Assert.True(user.IsLockedOut(Now));
    }

    [Fact]
    public void Lockout_expires_after_fifteen_minutes()
    {
        var user = NewAdmin();

        for (var i = 0; i < 5; i++)
            user.RecordFailedLogin(Now);

        // This assertion is only possible because "now" is injected rather than read
        // from DateTime.UtcNow — the reason IClock exists.
        Assert.True(user.IsLockedOut(Now.AddMinutes(14)));
        Assert.False(user.IsLockedOut(Now.AddMinutes(16)));
    }

    [Fact]
    public void Successful_login_clears_the_counter_and_the_lockout()
    {
        var user = NewAdmin();

        for (var i = 0; i < 5; i++)
            user.RecordFailedLogin(Now);

        user.RecordSuccessfulLogin(Now.AddMinutes(20));

        Assert.Equal(0, user.FailedLoginAttempts);
        Assert.False(user.IsLockedOut(Now.AddMinutes(20)));
        Assert.Equal(Now.AddMinutes(20), user.LastLoginAtUtc);
    }

    [Fact]
    public void Setting_a_new_password_clears_the_lockout()
    {
        var user = NewAdmin();

        for (var i = 0; i < 5; i++)
            user.RecordFailedLogin(Now);

        user.SetPasswordHash("new-hash", Now.AddMinutes(1));

        Assert.False(user.IsLockedOut(Now.AddMinutes(1)));
        Assert.Equal(0, user.FailedLoginAttempts);
    }

    [Fact]
    public void Pausing_and_reactivating_moves_status_without_touching_credentials()
    {
        var user = NewAdmin();

        user.Pause(Now);
        Assert.Equal(UserStatus.Paused, user.Status);

        user.Reactivate(Now.AddMinutes(5));
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.Equal("hash", user.PasswordHash);
    }
}
