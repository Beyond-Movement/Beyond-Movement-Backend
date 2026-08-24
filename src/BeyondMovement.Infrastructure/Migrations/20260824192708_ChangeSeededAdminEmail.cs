using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeyondMovement.Infrastructure.Migrations
{
    /// <summary>
    /// Data-only migration. AdminSeeder is a no-op once any Admin row exists, so changing
    /// Seed:AdminEmail in appsettings.json only affects a brand new database — every developer
    /// who already ran the app keeps the old address. This carries the change to their local
    /// database instead of asking each of them to delete and re-seed the Admin by hand.
    /// </summary>
    public partial class ChangeSeededAdminEmail : Migration
    {
        private const string OldEmail = "admin@beyondmovement.com";
        private const string NewEmail = "lillysynchro@gmail.com";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(Rename(from: OldEmail, to: NewEmail));

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(Rename(from: NewEmail, to: OldEmail));

        /// <remarks>
        /// Three guards, all of them load-bearing:
        /// <list type="bullet">
        /// <item>Role = 'Admin' and the exact old address — a production database whose admin was
        /// created deliberately never matches, so this runs as a no-op there.</item>
        /// <item>NOT EXISTS on the target address — "Users"."Email" is uniquely indexed, and a
        /// developer who already invited an athlete at that address would otherwise get a
        /// constraint violation that fails the whole migration.</item>
        /// <item>GoogleSubjectId is cleared. Google sign-in branch 1 matches on the subject id and
        /// never looks at the email, so leaving it would let the Google account linked under the
        /// old address keep signing in as Admin. Cleared, the next Google sign-in re-links through
        /// branch 2 on the new verified email.</item>
        /// </list>
        /// Emails are stored lower-cased (User.CreateAdmin), and lookups compare exactly, so both
        /// literals must stay lower-case.
        /// </remarks>
        private static string Rename(string from, string to) =>
            $"""
             UPDATE "Users"
             SET    "Email" = '{to}',
                    "GoogleSubjectId" = NULL,
                    "UpdatedAtUtc" = NOW() AT TIME ZONE 'utc'
             WHERE  "Role" = 'Admin'
               AND  "Email" = '{from}'
               AND  NOT EXISTS (SELECT 1 FROM "Users" WHERE "Email" = '{to}');
             """;
    }
}
