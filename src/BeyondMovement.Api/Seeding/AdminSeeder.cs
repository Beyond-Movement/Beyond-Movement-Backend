using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Identity.Persistence;
using BeyondMovement.Modules.Identity.Services;
using BeyondMovement.SharedKernel;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Api.Seeding;

/// <summary>
/// There is no registration endpoint (BR-01), so without this there is no way to log in at all.
/// Development only — production admins are created deliberately, not by a startup path.
/// </summary>
public static class AdminSeeder
{
    public static async Task SeedAdminAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;

        var db = provider.GetRequiredService<IIdentityDbContext>();
        var configuration = provider.GetRequiredService<IConfiguration>();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(AdminSeeder));

        if (await db.Users.AnyAsync(u => u.Role == UserRole.Admin, ct))
            return;

        var email = configuration["Seed:AdminEmail"];
        var fullName = configuration["Seed:AdminFullName"];
        var password = configuration["Seed:AdminPassword"];   // user secrets, never a file

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning(
                "No admin user exists and Seed:AdminEmail / Seed:AdminPassword are not configured. " +
                "Set them with: dotnet user-secrets set \"Seed:AdminPassword\" \"<value>\" --project src/BeyondMovement.Api");
            return;
        }

        var hasher = provider.GetRequiredService<IPasswordHasher<User>>();
        var clock = provider.GetRequiredService<IClock>();

        var admin = User.CreateAdmin(
            email,
            string.IsNullOrWhiteSpace(fullName) ? "Admin" : fullName,
            passwordHash: "placeholder",
            clock.UtcNow,
            ResolveTimeZone(configuration["Seed:AdminTimeZone"], logger));

        // The hasher salts per user, so the hash can only be produced once the user exists.
        admin.SetPasswordHash(hasher.HashPassword(admin, password), clock.UtcNow);

        db.Users.Add(admin);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Seeded the initial Admin user {UserId} in time zone {TimeZone}",
            admin.Id, admin.TimeZone);
    }

    /// <summary>
    /// Checks the configured zone actually resolves before it is written to the row.
    /// <para>
    /// The Admin dashboard computes week, month and year boundaries in this zone, and an
    /// unrecognised value there falls back to UTC <em>silently</em> on every request — the
    /// figures would simply be wrong for late-evening sessions and nothing would say why. Failing
    /// it here, once, at the moment somebody typed it, is the only place the mistake is cheap.
    /// </para>
    /// <para>
    /// A warning rather than a throw: an unusable time zone must not stop the Admin being created
    /// at all, because without an Admin there is no way to log in and fix anything (BR-01).
    /// </para>
    /// </summary>
    private static string? ResolveTimeZone(string? configured, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return null;   // CreateAdmin keeps its own UTC default

        // Shares TimeZoneId with the sync endpoint, so "a zone this server accepts" has one
        // definition. It also preserves the configured id rather than TimeZoneInfo.Id, which
        // means a Windows development host stores "Africa/Cairo" like a Linux one instead of
        // silently rewriting it to "Egypt Standard Time".
        if (TimeZoneId.TryNormalize(configured, out var timeZone))
            return timeZone;

        logger.LogWarning(
            "Seed:AdminTimeZone is set to {TimeZone}, which this server does not recognise. " +
            "The Admin will be created in UTC and the dashboard will report UTC periods. " +
            "Use an IANA id such as Africa/Cairo.", configured);

        return null;
    }
}
