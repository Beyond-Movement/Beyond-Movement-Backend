using BeyondMovement.Infrastructure;
using BeyondMovement.Infrastructure.Migrations;
using BeyondMovement.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// The Admin's time zone, which Phase 9 made load-bearing: the dashboard computes week, month and
/// year boundaries in it, so an Admin left on the column default reports UTC periods and files
/// late-evening sessions under the wrong day.
/// <para>
/// Athletes must be unaffected throughout. Nothing reads an athlete's time zone today, but these
/// tests pin that they keep the default so a future reader cannot be surprised.
/// </para>
/// </summary>
public sealed class AdminTimeZoneTests(AthleteApiFactory factory) : IClassFixture<AthleteApiFactory>
{
    private async Task<T> QueryAsync<T>(Func<AppDbContext, Task<T>> query)
    {
        using var scope = factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    [Fact]
    public async Task The_seeded_admin_is_created_in_the_configured_zone()
    {
        // Seed:AdminTimeZone is Africa/Cairo in appsettings.json, which the test host loads.
        var timeZone = await QueryAsync(db => db.Users
            .Where(x => x.Role == UserRole.Admin)
            .Select(x => x.TimeZone)
            .SingleAsync());

        Assert.Equal("Africa/Cairo", timeZone);
    }

    [Fact]
    public async Task Athletes_still_default_to_utc()
    {
        // The column default is unchanged and CreateAthlete takes no zone, so every athlete -
        // including the ones seeded before this change - stays on UTC.
        var zones = await QueryAsync(db => db.Users
            .Where(x => x.Role == UserRole.Athlete)
            .Select(x => x.TimeZone)
            .Distinct()
            .ToListAsync());

        Assert.NotEmpty(zones);
        Assert.All(zones, zone => Assert.Equal("UTC", zone));
    }

    [Fact]
    public void An_admin_is_created_in_utc_when_no_zone_is_configured()
    {
        // The parameter is optional and preserves the property's own default, so provisioning a
        // zone stays a deliberate act rather than something that leaks in everywhere.
        var withoutZone = User.CreateAdmin("nozone@nowhere.test", "No Zone", "hash", DateTime.UtcNow);
        Assert.Equal("UTC", withoutZone.TimeZone);

        foreach (var blank in new string?[] { null, "", "   " })
            Assert.Equal("UTC",
                User.CreateAdmin($"blank{blank?.Length ?? -1}@nowhere.test", "Blank", "hash", DateTime.UtcNow, blank)
                    .TimeZone);

        var withZone = User.CreateAdmin("zone@nowhere.test", "Zone", "hash", DateTime.UtcNow, "Africa/Cairo");
        Assert.Equal("Africa/Cairo", withZone.TimeZone);
    }

    /// <summary>
    /// Runs the migration's own statements — the constants the migration executes, not a copy —
    /// against rows arranged to cover every case the guards exist for.
    /// </summary>
    [Fact]
    public async Task The_migration_corrects_only_admins_still_on_the_default()
    {
        var stillDefault = Guid.NewGuid();
        var alreadyMoved = Guid.NewGuid();
        var athleteOnUtc = Guid.NewGuid();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Three rows: an Admin on the default, an Admin deliberately elsewhere, and an athlete.
        await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO "Users" ("Id","Role","Email","Status","TimeZone","CoachId",
                                  "FailedLoginAttempts","CreatedAtUtc","UpdatedAtUtc")
             VALUES ({stillDefault},'Admin','tz-default@nowhere.test','Active','UTC',{stillDefault},
                     0, NOW() AT TIME ZONE 'utc', NOW() AT TIME ZONE 'utc'),
                    ({alreadyMoved},'Admin','tz-moved@nowhere.test','Active','Europe/London',{alreadyMoved},
                     0, NOW() AT TIME ZONE 'utc', NOW() AT TIME ZONE 'utc'),
                    ({athleteOnUtc},'Athlete','tz-athlete@nowhere.test','Active','UTC',{stillDefault},
                     0, NOW() AT TIME ZONE 'utc', NOW() AT TIME ZONE 'utc');
             """);

        try
        {
            await db.Database.ExecuteSqlRawAsync(SetAdminTimeZoneToCairo.UpSql);

            Assert.Equal("Africa/Cairo", await ZoneAsync(db, stillDefault));

            // The guard that matters: an Admin who already had a zone is not overwritten.
            Assert.Equal("Europe/London", await ZoneAsync(db, alreadyMoved));

            // And athletes are untouched, which is what "Role = 'Admin'" is there for.
            Assert.Equal("UTC", await ZoneAsync(db, athleteOnUtc));

            // Re-running changes nothing: the UTC guard makes it a no-op the second time.
            await db.Database.ExecuteSqlRawAsync(SetAdminTimeZoneToCairo.UpSql);
            Assert.Equal("Africa/Cairo", await ZoneAsync(db, stillDefault));
            Assert.Equal("Europe/London", await ZoneAsync(db, alreadyMoved));
        }
        finally
        {
            await db.Database.ExecuteSqlAsync(
                $"""delete from "Users" where "Id" in ({stillDefault},{alreadyMoved},{athleteOnUtc})""");
        }
    }

    /// <summary>
    /// The rollback question: a Down that blindly reverted Cairo to UTC would erase a zone
    /// somebody set on purpose after the migration ran.
    /// </summary>
    [Fact]
    public async Task The_rollback_leaves_an_admin_who_has_since_moved_alone()
    {
        var revertible = Guid.NewGuid();
        var movedAfterwards = Guid.NewGuid();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO "Users" ("Id","Role","Email","Status","TimeZone","CoachId",
                                  "FailedLoginAttempts","CreatedAtUtc","UpdatedAtUtc")
             VALUES ({revertible},'Admin','tz-revert@nowhere.test','Active','Africa/Cairo',{revertible},
                     0, NOW() AT TIME ZONE 'utc', NOW() AT TIME ZONE 'utc'),
                    ({movedAfterwards},'Admin','tz-moved-after@nowhere.test','Active','Asia/Tokyo',{movedAfterwards},
                     0, NOW() AT TIME ZONE 'utc', NOW() AT TIME ZONE 'utc');
             """);

        try
        {
            await db.Database.ExecuteSqlRawAsync(SetAdminTimeZoneToCairo.DownSql);

            // Still holding what Up wrote, so it goes back to the documented prior state.
            Assert.Equal("UTC", await ZoneAsync(db, revertible));

            // Moved on since, so the rollback must not touch it. This is the case the
            // TimeZone = 'Africa/Cairo' guard exists for.
            Assert.Equal("Asia/Tokyo", await ZoneAsync(db, movedAfterwards));
        }
        finally
        {
            await db.Database.ExecuteSqlAsync(
                $"""delete from "Users" where "Id" in ({revertible},{movedAfterwards})""");
        }
    }

    private static Task<string> ZoneAsync(AppDbContext db, Guid userId) =>
        db.Users.AsNoTracking().Where(x => x.Id == userId).Select(x => x.TimeZone).SingleAsync();
}
