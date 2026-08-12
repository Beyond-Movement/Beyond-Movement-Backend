using BeyondMovement.Modules.Identity.Domain;
using Microsoft.AspNetCore.Identity;

namespace BeyondMovement.UnitTests.Identity;

public class PasswordVerificationTests
{
    private static readonly DateTime Now = new(2026, 8, 12, 10, 0, 0, DateTimeKind.Utc);

    private static (User user, IPasswordHasher<User> hasher) CreateAdminWith(string password)
    {
        var hasher = new PasswordHasher<User>();
        var user = User.CreateAdmin("admin@beyondmovement.com", "Admin", "placeholder", Now);
        user.SetPasswordHash(hasher.HashPassword(user, password), Now);
        return (user, hasher);
    }

    [Fact]
    public void Correct_password_verifies()
    {
        var (user, hasher) = CreateAdminWith("Correct#Horse7Battery");

        var result = hasher.VerifyHashedPassword(user, user.PasswordHash!, "Correct#Horse7Battery");

        Assert.Equal(PasswordVerificationResult.Success, result);
    }

    [Fact]
    public void Wrong_password_fails()
    {
        var (user, hasher) = CreateAdminWith("Correct#Horse7Battery");

        var result = hasher.VerifyHashedPassword(user, user.PasswordHash!, "not-the-password");

        Assert.Equal(PasswordVerificationResult.Failed, result);
    }

    [Fact]
    public void Password_verification_is_case_sensitive()
    {
        var (user, hasher) = CreateAdminWith("Correct#Horse7Battery");

        var result = hasher.VerifyHashedPassword(user, user.PasswordHash!, "correct#horse7battery");

        Assert.Equal(PasswordVerificationResult.Failed, result);
    }

    [Fact]
    public void Stored_hash_is_not_the_password()
    {
        var (user, _) = CreateAdminWith("Correct#Horse7Battery");

        Assert.DoesNotContain("Correct#Horse7Battery", user.PasswordHash);
    }

    [Fact]
    public void Same_password_hashes_differently_for_each_user()
    {
        var (first, _) = CreateAdminWith("Correct#Horse7Battery");
        var (second, _) = CreateAdminWith("Correct#Horse7Battery");

        // Per-user salt: identical passwords must not produce identical hashes.
        Assert.NotEqual(first.PasswordHash, second.PasswordHash);
    }

    [Fact]
    public void Email_is_stored_lower_cased()
    {
        var user = User.CreateAdmin("Admin@BeyondMovement.COM", "Admin", "hash", Now);

        Assert.Equal("admin@beyondmovement.com", user.Email);
    }
}
