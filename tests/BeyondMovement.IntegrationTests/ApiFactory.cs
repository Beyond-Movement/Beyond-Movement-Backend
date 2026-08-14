using BeyondMovement.Modules.Identity.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// A Google token validator the tests drive directly. Everything downstream of verification —
/// the three account-matching branches that enforce BR-01 — is the real code path.
/// </summary>
public sealed class StubGoogleTokenValidator : IGoogleTokenValidator
{
    /// <summary>The identity the next call returns. Null means "the token failed verification".</summary>
    public GoogleIdentity? NextIdentity { get; set; }

    public Task<GoogleIdentity?> ValidateAsync(string idToken, CancellationToken ct = default) =>
        Task.FromResult(NextIdentity);
}

/// <summary>
/// Starts the real application against a throwaway PostgreSQL container, so these tests
/// exercise the same EF Core provider, the same migrations, and the same SQL as production.
/// Secrets are supplied in memory — user secrets do not exist in CI.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminEmail = "admin@beyondmovement.test";
    public const string AdminPassword = "Integration#Test2026";

    public StubGoogleTokenValidator GoogleValidator { get; } = new();

    // Pinned to the same major version as docker-compose, so tests and local development
    // run against the same PostgreSQL.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16").Build();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development so the startup path migrates the database and seeds the admin.
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = _postgres.GetConnectionString(),
                ["Jwt:SigningKey"] = new string('k', 64),
                ["Jwt:Issuer"] = "beyond-movement",
                ["Jwt:Audience"] = "beyond-movement-app",
                ["Jwt:AccessTokenMinutes"] = "15",
                ["Jwt:RefreshTokenDays"] = "30",
                ["Seed:AdminEmail"] = AdminEmail,
                ["Seed:AdminFullName"] = "Integration Admin",
                ["Seed:AdminPassword"] = AdminPassword,
                ["App:PasswordResetUrlTemplate"] = "beyondmovement://reset-password?token={token}",
                ["App:MinimumSupportedAppVersion"] = "1.0.0",
                ["Google:ClientId:Web"] = "test-web-client-id.apps.googleusercontent.com"
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IGoogleTokenValidator>();
            services.AddSingleton<IGoogleTokenValidator>(GoogleValidator);
        });
    }

    // Implemented explicitly: xUnit's IAsyncLifetime returns Task, while the base
    // WebApplicationFactory.DisposeAsync returns ValueTask. Without this the two
    // signatures collide.
    Task IAsyncLifetime.InitializeAsync() => _postgres.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
