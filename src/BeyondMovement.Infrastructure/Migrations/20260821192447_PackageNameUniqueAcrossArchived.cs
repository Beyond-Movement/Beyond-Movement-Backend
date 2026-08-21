using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeyondMovement.Infrastructure.Migrations
{
    /// <summary>
    /// Package names are now unique across the whole catalogue, archived options included, and
    /// case-insensitively — the decision the client confirmed, replacing the narrower rule that
    /// let a name freed by archiving be reused and then collide when the original was restored.
    /// <para>
    /// The replacement is raw SQL because it is an expression index on <c>lower("Name")</c>,
    /// which EF Core cannot express. Without the expression the constraint would be
    /// case-sensitive, so "8 Sessions" and "8 sessions" could both exist and only the handler's
    /// check would stand between them — and two Admin devices can race straight past that.
    /// </para>
    /// </summary>
    public partial class PackageNameUniqueAcrossArchived : Migration
    {
        private const string IndexName = "IX_PackageOptions_CoachId_LowerName";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PackageOptions_CoachId_Name_Active",
                table: "PackageOptions");

            migrationBuilder.Sql($"""
                CREATE UNIQUE INDEX "{IndexName}"
                ON "PackageOptions" ("CoachId", lower("Name"));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""DROP INDEX IF EXISTS "{IndexName}";""");

            migrationBuilder.CreateIndex(
                name: "IX_PackageOptions_CoachId_Name_Active",
                table: "PackageOptions",
                columns: new[] { "CoachId", "Name" },
                unique: true,
                filter: "\"IsArchived\" = false");
        }
    }
}
