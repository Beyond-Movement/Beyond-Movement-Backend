using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// Its own factory with a deliberately tiny limit. The shared ApiFactory raises the limit so
/// the suite does not throttle itself, which would otherwise leave this protection untested.
/// </summary>
public sealed class ThrottledApiFactory : ApiFactory
{
    public const int Limit = 3;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimits:InvitationValidationPerMinute"] = Limit.ToString()
            }));
    }
}

public sealed class RateLimitTests(ThrottledApiFactory factory) : IClassFixture<ThrottledApiFactory>
{
    [Fact]
    public async Task Invitation_code_guessing_is_throttled()
    {
        var client = factory.CreateClient();

        // An invitation code is short enough to type, so an unthrottled validate endpoint
        // would be a guessing oracle (architecture section 7.1).
        for (var attempt = 0; attempt < ThrottledApiFactory.Limit; attempt++)
        {
            var allowed = await client.GetAsync("/api/v1/invitations/validate?code=WRONG-CODES");
            Assert.Equal(HttpStatusCode.BadRequest, allowed.StatusCode);
        }

        var blocked = await client.GetAsync("/api/v1/invitations/validate?code=WRONG-CODES");

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.NotNull(blocked.Headers.RetryAfter);

        var body = await blocked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("TOO_MANY_REQUESTS", body.GetProperty("errorCode").GetString());
        Assert.True(body.GetProperty("retryAfterSeconds").GetInt32() > 0);
    }
}
