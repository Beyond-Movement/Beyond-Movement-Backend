using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeyondMovement.Infrastructure.Migrations
{
    /// <summary>
    /// Data-only migration. No schema changes.
    /// <para>
    /// Phase 9 made <c>Users.TimeZone</c> load-bearing for the first time: the Admin dashboard
    /// computes week, month and year boundaries in the coach's own zone. Every existing row still
    /// holds the column default, <c>UTC</c>, and the client is Egypt-based — so without this the
    /// production dashboard would file a session at 00:30 Cairo on the 1st under the previous
    /// month, and the coach's own count would disagree with the screen every month.
    /// </para>
    /// <para>
    /// <c>AdminSeeder</c> cannot fix this. It runs in Development only, and returns immediately
    /// once any Admin row exists — so it never reaches production, and never reaches a developer
    /// database that has already been seeded. This carries the correction to all of them, exactly
    /// as <c>ChangeSeededAdminEmail</c> did for the seeded address.
    /// </para>
    /// <para>
    /// <b>Africa/Cairo is hard-coded deliberately.</b> This is a one-off correction for the
    /// current deployment, not an application default — new databases get their zone from
    /// <c>Seed:AdminTimeZone</c>, and the column default stays <c>UTC</c> so athletes are
    /// unaffected.
    /// </para>
    /// </summary>
    public partial class SetAdminTimeZoneToCairo : Migration
    {
        public const string Cairo = "Africa/Cairo";
        public const string Utc = "UTC";

        /// <summary>
        /// Two guards, both load-bearing:
        /// <list type="bullet">
        /// <item><c>Role = 'Admin'</c> — athlete rows are never touched. Nothing in the codebase
        /// reads an athlete's time zone (the only reader is the dashboard, scoped to the coach),
        /// but the guarantee belongs in the statement rather than in reasoning about it.</item>
        /// <item><c>TimeZone = 'UTC'</c> — only rows still holding the column default are
        /// corrected. An Admin whose zone was set deliberately is left alone, and re-running is a
        /// no-op, so this cannot fight <c>Seed:AdminTimeZone</c> on a fresh database.</item>
        /// </list>
        /// Exposed as a constant so the test suite can execute the statement that actually ships
        /// rather than a copy of it that can drift.
        /// </summary>
        public const string UpSql =
            """
            UPDATE "Users"
            SET    "TimeZone" = 'Africa/Cairo',
                   "UpdatedAtUtc" = NOW() AT TIME ZONE 'utc'
            WHERE  "Role" = 'Admin'
              AND  "TimeZone" = 'UTC';
            """;

        /// <summary>
        /// Reverts only rows that still hold exactly the value <see cref="UpSql"/> wrote.
        /// <para>
        /// The <c>TimeZone = 'Africa/Cairo'</c> guard is what makes this safe: an Admin who was
        /// moved to some other zone after the migration ran no longer matches, so a rollback
        /// cannot overwrite that newer value with UTC. That is the case worth protecting, and it
        /// is protected exactly.
        /// </para>
        /// <para>
        /// <b>One ambiguity is irreducible and is accepted knowingly.</b> An Admin who was
        /// independently set to Africa/Cairo after the migration is indistinguishable from one
        /// this migration set — the row records a value, not who wrote it — so a rollback returns
        /// them to UTC. Distinguishing the two would mean recording per-row provenance for a
        /// one-off correction, which is more machinery than the problem deserves. It is also
        /// unreachable today: <c>User.TimeZone</c> has a private setter and no mutator, and there
        /// is no settings endpoint, so nothing can change a time zone except raw SQL. If a way to
        /// change it is ever added, revisit this <c>Down</c> before relying on it.
        /// </para>
        /// </summary>
        public const string DownSql =
            """
            UPDATE "Users"
            SET    "TimeZone" = 'UTC',
                   "UpdatedAtUtc" = NOW() AT TIME ZONE 'utc'
            WHERE  "Role" = 'Admin'
              AND  "TimeZone" = 'Africa/Cairo';
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(UpSql);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(DownSql);
    }
}
