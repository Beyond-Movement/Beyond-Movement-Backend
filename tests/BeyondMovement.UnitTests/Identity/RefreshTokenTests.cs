using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Identity.Services;
using BeyondMovement.SharedKernel;
using Microsoft.Extensions.Options;

namespace BeyondMovement.UnitTests.Identity;

public class RefreshTokenTests
{
    private static readonly DateTime Now = new(2026, 8, 12, 10, 0, 0, DateTimeKind.Utc);

    private static RefreshToken Issue() =>
        RefreshToken.Issue(Guid.NewGuid(), "hash", Guid.NewGuid(), deviceId: null, Now);

    [Fact]
    public void A_fresh_token_is_active()
    {
        Assert.True(Issue().IsActive(Now));
    }

    [Fact]
    public void A_used_token_is_no_longer_active()
    {
        var token = Issue();

        token.MarkUsed(Now.AddMinutes(1));

        Assert.False(token.IsActive(Now.AddMinutes(2)));
    }

    [Fact]
    public void A_revoked_token_is_no_longer_active()
    {
        var token = Issue();

        token.Revoke(Now.AddMinutes(1));

        Assert.False(token.IsActive(Now.AddMinutes(2)));
    }

    [Fact]
    public void A_token_expires_after_thirty_days()
    {
        var token = Issue();

        Assert.True(token.IsActive(Now.AddDays(29)));
        Assert.False(token.IsActive(Now.AddDays(31)));
    }

    [Fact]
    public void Revoking_twice_keeps_the_first_timestamp()
    {
        var token = Issue();

        token.Revoke(Now.AddMinutes(1));
        token.Revoke(Now.AddMinutes(9));

        Assert.Equal(Now.AddMinutes(1), token.RevokedAtUtc);
    }

    [Fact]
    public void A_reset_token_is_single_use_and_expires_in_an_hour()
    {
        var token = PasswordResetToken.Issue(Guid.NewGuid(), "hash", Now);

        Assert.True(token.IsUsable(Now.AddMinutes(30)));
        Assert.False(token.IsUsable(Now.AddHours(2)));

        token.MarkUsed(Now.AddMinutes(30));
        Assert.False(token.IsUsable(Now.AddMinutes(31)));
    }
}

public class TokenServiceTests
{
    private sealed class FixedClock(DateTime now) : IClock
    {
        public DateTime UtcNow { get; } = now;
    }

    private static TokenService CreateService() => new(
        Options.Create(new JwtOptions
        {
            Issuer = "beyond-movement",
            Audience = "beyond-movement-app",
            SigningKey = new string('k', 64),
            AccessTokenMinutes = 15,
            RefreshTokenDays = 30
        }),
        new FixedClock(new DateTime(2026, 8, 12, 10, 0, 0, DateTimeKind.Utc)));

    [Fact]
    public void Hashing_is_deterministic()
    {
        var service = CreateService();

        Assert.Equal(service.Hash("some-token"), service.Hash("some-token"));
        Assert.NotEqual(service.Hash("some-token"), service.Hash("other-token"));
    }

    [Fact]
    public void The_raw_refresh_token_is_never_its_own_hash()
    {
        var service = CreateService();

        var (raw, hash) = service.CreateRefreshToken();

        Assert.NotEqual(raw, hash);
        Assert.Equal(service.Hash(raw), hash);
    }

    [Fact]
    public void Every_refresh_token_is_unique()
    {
        var service = CreateService();

        var tokens = Enumerable.Range(0, 100).Select(_ => service.CreateRefreshToken().raw).ToList();

        Assert.Equal(100, tokens.Distinct().Count());
    }

    [Fact]
    public void An_access_token_carries_the_claims_the_api_reads()
    {
        var service = CreateService();
        var user = User.CreateAdmin("admin@beyondmovement.com", "Admin", "hash",
            new DateTime(2026, 8, 12, 10, 0, 0, DateTimeKind.Utc));

        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
            .ReadJwtToken(service.CreateAccessToken(user));

        Assert.Equal(user.Id.ToString(), jwt.Claims.Single(c => c.Type == "sub").Value);
        Assert.Equal("Admin", jwt.Claims.Single(c => c.Type == TokenService.RoleClaim).Value);
        Assert.Equal(user.CoachId.ToString(), jwt.Claims.Single(c => c.Type == TokenService.CoachIdClaim).Value);
        Assert.Equal("beyond-movement", jwt.Issuer);
    }
}
