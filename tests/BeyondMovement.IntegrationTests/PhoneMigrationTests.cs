using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Identity;
using BeyondMovement.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// The E.164 migration run against rows the previous build could have written — stored as typed,
/// up to 40 characters, digits and <c>+ ( ) - .</c> — rather than against an empty table, where
/// any migration passes. Each test gets its own database, stopped at the migration before this
/// one, so it can be seeded in the old shape and then brought forward.
/// </summary>
public sealed class PhoneMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration = "20260922101618_AddExpenses";

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

    /// <summary>Writes a user, then overwrites its phone with raw SQL — the way the old code stored it.</summary>
    private static async Task<Guid> LegacyUserAsync(AppDbContext db, string email, string? phone)
    {
        var now = DateTime.UtcNow;
        var user = User.CreateAdmin(email, "Legacy User", "hash", now);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlAsync($"""UPDATE "Users" SET "Phone" = {phone} WHERE "Id" = {user.Id}""");
        return user.Id;
    }

    private static Task<string?> PhoneOfAsync(AppDbContext db, Guid id) =>
        db.Database.SqlQuery<string?>($"""SELECT "Phone" AS "Value" FROM "Users" WHERE "Id" = {id}""")
            .SingleAsync();

    [Fact]
    public async Task Existing_numbers_are_normalized_and_accounts_without_one_are_untouched()
    {
        await using var db = NewContext("phone_upgrade");
        await MigrateToAsync(db, PreviousMigration);

        var none = await LegacyUserAsync(db, "none@legacy.test", null);          // the seeded Admin's case
        var blank = await LegacyUserAsync(db, "blank@legacy.test", "   ");
        var spaced = await LegacyUserAsync(db, "spaced@legacy.test", "+20 100 123 4567");
        var dialled = await LegacyUserAsync(db, "dialled@legacy.test", "0020-100-123-4567");
        var bracketed = await LegacyUserAsync(db, "uk@legacy.test", "(+44) 20.7031.3000");
        var national = await LegacyUserAsync(db, "national@legacy.test", "010 1234 5678");

        await MigrateToAsync(db);

        // No number is invented for an account that never had one.
        Assert.Null(await PhoneOfAsync(db, none));
        Assert.Null(await PhoneOfAsync(db, blank));

        Assert.Equal("+201001234567", await PhoneOfAsync(db, spaced));
        Assert.Equal("+201001234567", await PhoneOfAsync(db, dialled));
        Assert.Equal("+442070313000", await PhoneOfAsync(db, bracketed));

        // SQL cannot know which country a national number belongs to, so its digits are kept as
        // they are — and the app sends it straight back on the next profile save, where it passes
        // validation and becomes E.164. Proven here rather than assumed.
        var legacyNational = await PhoneOfAsync(db, national);
        Assert.Equal("01012345678", legacyNational);
        Assert.True(PhonePolicy.TryNormalize(legacyNational, out var resaved));
        Assert.Equal("+201012345678", resaved);
    }

    /// <summary>
    /// More than 15 digits cannot be a phone number, and cannot fit the new column. The migration
    /// refuses to run rather than truncate or discard it, and leaves the row exactly as it was.
    /// </summary>
    [Fact]
    public async Task A_number_too_long_to_be_real_stops_the_migration_rather_than_losing_it()
    {
        await using var db = NewContext("phone_overflow");
        await MigrateToAsync(db, PreviousMigration);

        const string tooLong = "1234 5678 9012 3456 7890";
        var id = await LegacyUserAsync(db, "overflow@legacy.test", tooLong);

        var failure = await Assert.ThrowsAsync<PostgresException>(() => MigrateToAsync(db));
        Assert.Contains("more than 15 digits", failure.MessageText);

        // Rolled back whole: the value is untouched and the migration is still pending.
        Assert.Equal(tooLong, await PhoneOfAsync(db, id));
        Assert.Contains((await db.Database.GetPendingMigrationsAsync()),
            m => m.EndsWith("_NormalizeUserPhoneToE164"));

        // Once someone decides what the number should be, the migration goes through.
        await db.Database.ExecuteSqlAsync($"""UPDATE "Users" SET "Phone" = NULL WHERE "Id" = {id}""");
        await MigrateToAsync(db);

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }
}
