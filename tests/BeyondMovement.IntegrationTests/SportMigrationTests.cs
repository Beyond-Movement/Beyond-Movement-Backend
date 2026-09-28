using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// The free-text-to-catalogue migration, run against the sport values development databases
/// actually hold rather than against an empty table, where any migration passes. Each test gets
/// its own database, stopped at the migration before this one, so it can be seeded in the old
/// shape and then brought forward.
/// </summary>
public sealed class SportMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration = "20260927202019_AddSessionNoteAttachments";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private AppDbContext NewContext(string database)
    {
        var connection = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = database };
        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection.ToString()).Options);
    }

    private static Task MigrateToAsync(AppDbContext db, string? target = null) =>
        db.GetService<IMigrator>().MigrateAsync(target);

    /// <summary>
    /// An athlete as the previous build stored them: a user, and a profile row whose sport is the
    /// text they typed. Written in SQL because the entity no longer has a Sport property.
    /// Completed whenever there is a sport, as Complete Profile made them.
    /// </summary>
    private static async Task<Guid> LegacyAthleteAsync(AppDbContext db, string email, string? sport)
    {
        var now = DateTime.UtcNow;
        var user = User.CreateAthlete(email, "Legacy Athlete", "hash", null, Guid.NewGuid(), now);

        if (sport is not null)
            user.MarkProfileCompleted(now);

        db.Users.Add(user);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "AthleteProfiles" ("Id", "UserId", "CoachId", "Sport", "IsLoyal", "CreatedAtUtc", "UpdatedAtUtc")
            VALUES ({Guid.NewGuid()}, {user.Id}, {user.CoachId}, {sport}, false, {now}, {now})
            """);

        return user.Id;
    }

    private static Task<Guid?> SportIdOfAsync(AppDbContext db, Guid userId) =>
        db.Database.SqlQuery<Guid?>($"""SELECT "SportId" AS "Value" FROM "AthleteProfiles" WHERE "UserId" = {userId}""")
            .SingleAsync();

    private static Task<bool> CompletedAsync(AppDbContext db, Guid userId) =>
        db.Database.SqlQuery<bool>($"""SELECT "ProfileCompletedAtUtc" IS NOT NULL AS "Value" FROM "Users" WHERE "Id" = {userId}""")
            .SingleAsync();

    [Fact]
    public async Task The_reviewed_dev_values_map_to_their_catalogue_sport()
    {
        await using var db = NewContext("sport_mapped");
        await MigrateToAsync(db, PreviousMigration);

        var tennis = await LegacyAthleteAsync(db, "tennis@legacy.test", "Tennis");
        var football = await LegacyAthleteAsync(db, "football@legacy.test", "Football");
        var typo = await LegacyAthleteAsync(db, "typo@legacy.test", "Artistic Swimmi.g");
        var lower = await LegacyAthleteAsync(db, "lower@legacy.test", "artistic swimmimg");
        var initials = await LegacyAthleteAsync(db, "initials@legacy.test", "AS");
        var none = await LegacyAthleteAsync(db, "none@legacy.test", null);

        await MigrateToAsync(db);

        Assert.Equal(Sports.Id("Tennis"), await SportIdOfAsync(db, tennis));
        Assert.Equal(Sports.Id("Football"), await SportIdOfAsync(db, football));
        Assert.Equal(Sports.Id("Artistic Swimming"), await SportIdOfAsync(db, typo));
        Assert.Equal(Sports.Id("Artistic Swimming"), await SportIdOfAsync(db, lower));
        Assert.Equal(Sports.Id("Artistic Swimming"), await SportIdOfAsync(db, initials));

        // Every mapped athlete is still completed; the one who never finished still has no sport.
        foreach (var athlete in new[] { tennis, football, typo, lower, initials })
            Assert.True(await CompletedAsync(db, athlete));

        Assert.Null(await SportIdOfAsync(db, none));
        Assert.False(await CompletedAsync(db, none));
    }

    /// <summary>
    /// A value that already is a catalogue name is the same sport whatever its case or stray
    /// spaces — that is not a guess. Anything short of that is not matched: see the refusal test.
    /// </summary>
    [Fact]
    public async Task A_value_that_is_already_a_catalogue_name_maps_ignoring_case_and_spaces()
    {
        await using var db = NewContext("sport_exact");
        await MigrateToAsync(db, PreviousMigration);

        var swimming = await LegacyAthleteAsync(db, "swimming@legacy.test", "  swimming ");
        var table = await LegacyAthleteAsync(db, "table@legacy.test", "TABLE TENNIS");

        await MigrateToAsync(db);

        Assert.Equal(Sports.Id("Swimming"), await SportIdOfAsync(db, swimming));
        Assert.Equal(Sports.Id("Table Tennis"), await SportIdOfAsync(db, table));
    }

    /// <summary>
    /// "Hi" was never a sport, so it becomes no sport — not "Other", and not a new catalogue
    /// entry. Completed promises a sport, so that athlete is sent back through Complete Profile;
    /// nobody else is touched.
    /// </summary>
    [Fact]
    public async Task Junk_is_cleared_and_that_athlete_must_complete_their_profile_again()
    {
        await using var db = NewContext("sport_junk");
        await MigrateToAsync(db, PreviousMigration);

        var junk = await LegacyAthleteAsync(db, "hi@legacy.test", "Hi");
        var fine = await LegacyAthleteAsync(db, "fine@legacy.test", "Tennis");

        await MigrateToAsync(db);

        Assert.Null(await SportIdOfAsync(db, junk));
        Assert.False(await CompletedAsync(db, junk));

        Assert.Equal(Sports.Id("Tennis"), await SportIdOfAsync(db, fine));
        Assert.True(await CompletedAsync(db, fine));

        Assert.DoesNotContain("Hi",
            await db.Database.SqlQuery<string>($"""SELECT "Name" AS "Value" FROM "Sports" """).ToListAsync());
    }

    /// <summary>
    /// A value nobody has reviewed is not guessed at — not "Tenis" as Tennis, not anything as
    /// Other. The migration stops, names it, and leaves every row as it was.
    /// </summary>
    [Fact]
    public async Task An_unreviewed_value_stops_the_migration_rather_than_being_guessed()
    {
        await using var db = NewContext("sport_unmapped");
        await MigrateToAsync(db, PreviousMigration);

        var typo = await LegacyAthleteAsync(db, "tenis@legacy.test", "Tenis");
        var tennis = await LegacyAthleteAsync(db, "tennis@legacy.test", "Tennis");

        var failure = await Assert.ThrowsAsync<PostgresException>(() => MigrateToAsync(db));
        Assert.Contains("'Tenis'", failure.MessageText);

        // Rolled back whole: the text is still there, and the migration is still pending.
        Assert.Equal("Tenis", await db.Database.SqlQuery<string>(
            $"""SELECT "Sport" AS "Value" FROM "AthleteProfiles" WHERE "UserId" = {typo}""").SingleAsync());
        Assert.Contains(await db.Database.GetPendingMigrationsAsync(), m => m.EndsWith("_AddSportsCatalogue"));

        // Once somebody decides what it should be, the migration goes through.
        await db.Database.ExecuteSqlAsync(
            $"""UPDATE "AthleteProfiles" SET "Sport" = 'Tennis' WHERE "UserId" = {typo}""");
        await MigrateToAsync(db);

        Assert.Equal(Sports.Id("Tennis"), await SportIdOfAsync(db, typo));
        Assert.Equal(Sports.Id("Tennis"), await SportIdOfAsync(db, tennis));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }
}
