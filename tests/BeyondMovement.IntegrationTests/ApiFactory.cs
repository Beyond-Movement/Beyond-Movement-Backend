using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// Starts the real application against a throwaway PostgreSQL container, so these tests
/// exercise the same EF Core provider, the same migrations, and the same SQL as production.
/// Secrets are supplied in memory — user secrets do not exist in CI.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminEmail = "admin@beyondmovement.test";
    public const string AdminPassword = "Integration#Test2026";

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
                ["App:PasswordResetUrlTemplate"] = "https://example.test/reset?token={token}"
            });
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
