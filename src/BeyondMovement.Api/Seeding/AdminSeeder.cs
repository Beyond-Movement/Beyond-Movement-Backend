using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Identity.Persistence;
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
            clock.UtcNow);

        // The hasher salts per user, so the hash can only be produced once the user exists.
        admin.SetPasswordHash(hasher.HashPassword(admin, password), clock.UtcNow);

        db.Users.Add(admin);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Seeded the initial Admin user {UserId}", admin.Id);
    }
}
