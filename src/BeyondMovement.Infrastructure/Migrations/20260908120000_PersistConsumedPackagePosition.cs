using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeyondMovement.Infrastructure.Migrations;

/// <summary>
/// Persists the package position assigned at deduction time. Existing consuming sessions are
/// backfilled in their recorded resolution order; AttendedAtUtc is preferred, while UpdatedAtUtc
/// supplies the corresponding timestamp for historical no-shows.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260908120000_PersistConsumedPackagePosition")]
public partial class PersistConsumedPackagePosition : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "ConsumedPackagePosition",
            table: "Sessions",
            type: "integer",
            nullable: true);

        migrationBuilder.Sql("""
            WITH positions AS (
                SELECT "Id",
                       ROW_NUMBER() OVER (
                           PARTITION BY "PackageId"
                           ORDER BY COALESCE("AttendedAtUtc", "UpdatedAtUtc"), "Id") AS position
                FROM "Sessions"
                WHERE "ConsumedSessionCount" = 1 AND "PackageId" IS NOT NULL
            )
            UPDATE "Sessions" AS session
            SET "ConsumedPackagePosition" = positions.position
            FROM positions
            WHERE session."Id" = positions."Id";
            """);

        migrationBuilder.AddCheckConstraint(
            name: "CK_Sessions_PackagePositionMatchesConsumption",
            table: "Sessions",
            sql: "(\"ConsumedSessionCount\" = 0 AND \"ConsumedPackagePosition\" IS NULL) OR " +
                 "(\"ConsumedSessionCount\" = 1 AND \"ConsumedPackagePosition\" > 0)");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_Sessions_PackagePositionMatchesConsumption",
            table: "Sessions");

        migrationBuilder.DropColumn(
            name: "ConsumedPackagePosition",
            table: "Sessions");
    }
}
