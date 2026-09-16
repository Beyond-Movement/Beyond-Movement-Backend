using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeyondMovement.Infrastructure.Migrations
{
    /// <summary>
    /// Recognised package features — a package feature may now carry a stable code as well as its
    /// display text, and a purchased package remembers which codes it was sold with.
    /// <para>
    /// Three changes, and the order below matters:
    /// </para>
    /// <list type="number">
    /// <item><c>PackageOptionFeatures.Code</c>, nullable. Null is the ordinary feature — arbitrary
    /// text the coach wrote — so every existing row is correct as it stands and nothing is
    /// backfilled. A filtered unique index stops one option claiming the same code twice.</item>
    /// <item><c>PurchasedPackages.IncludedFeatures</c>, an array of codes frozen at purchase. Empty
    /// for every existing package, which is the truthful answer rather than a lossy one: no
    /// catalogue option carried a code until this migration, so nothing was ever sold with
    /// one.</item>
    /// <item><c>PackagePurchases.Features</c> moves from an array column to the
    /// <c>PackagePurchaseFeatures</c> child table, because a feature is now two values and two
    /// parallel arrays that must be kept the same length is the trap the rest of this schema
    /// avoids. <b>The rows are copied before the column is dropped</b> — the scaffolded version of
    /// this migration dropped it first and lost every snapshot.</item>
    /// </list>
    /// </summary>
    public partial class AddRecognisedPackageFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Code",
                table: "PackageOptionFeatures",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            // One option cannot claim the same recognised feature twice. Filtered, because every
            // ordinary feature has a null code and nulls would otherwise collide constantly.
            migrationBuilder.CreateIndex(
                name: "IX_PackageOptionFeatures_OneOfEachCodePerOption",
                table: "PackageOptionFeatures",
                columns: new[] { "PackageOptionId", "Code" },
                unique: true,
                filter: "\"Code\" IS NOT NULL");

            migrationBuilder.AddColumn<string[]>(
                name: "IncludedFeatures",
                table: "PurchasedPackages",
                type: "character varying(40)[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.CreateTable(
                name: "PackagePurchaseFeatures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PackagePurchaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackagePurchaseFeatures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PackagePurchaseFeatures_PackagePurchases_PackagePurchaseId",
                        column: x => x.PackagePurchaseId,
                        principalTable: "PackagePurchases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PackagePurchaseFeatures_PackagePurchaseId_Position",
                table: "PackagePurchaseFeatures",
                columns: new[] { "PackagePurchaseId", "Position" },
                unique: true);

            // Every existing snapshot, moved across before the column it lives in is dropped.
            //
            // WITH ORDINALITY is what preserves the order, which is meaning here - it is what the
            // athlete read down the card. It counts from 1 and Position is zero-based, hence the
            // subtraction. Code is NULL for every migrated row, and that is not a guess: no
            // catalogue option could carry a code before the column added above existed.
            //
            // A purchase whose Features array is empty - every one backfilled onto a package that
            // predates Phase 8 - contributes no rows, which is exactly what it said before.
            migrationBuilder.Sql(
                """
                INSERT INTO "PackagePurchaseFeatures" ("Id", "PackagePurchaseId", "Position", "Text", "Code")
                SELECT gen_random_uuid(), p."Id", feature.ordinality - 1, feature.text, NULL
                FROM "PackagePurchases" p
                CROSS JOIN LATERAL unnest(p."Features") WITH ORDINALITY AS feature(text, ordinality);
                """);

            migrationBuilder.DropColumn(
                name: "Features",
                table: "PackagePurchases");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The array column comes back, and the rows are copied into it before the table they
            // live in is dropped - the mirror of Up, for the same reason. The codes are lost, which
            // is unavoidable: the shape being restored has nowhere to put them.
            migrationBuilder.AddColumn<List<string>>(
                name: "Features",
                table: "PackagePurchases",
                type: "character varying(100)[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.Sql(
                """
                UPDATE "PackagePurchases" p
                SET "Features" = COALESCE((
                    SELECT array_agg(f."Text" ORDER BY f."Position")
                    FROM "PackagePurchaseFeatures" f
                    WHERE f."PackagePurchaseId" = p."Id"), ARRAY[]::character varying(100)[]);
                """);

            migrationBuilder.DropTable(
                name: "PackagePurchaseFeatures");

            migrationBuilder.DropIndex(
                name: "IX_PackageOptionFeatures_OneOfEachCodePerOption",
                table: "PackageOptionFeatures");

            migrationBuilder.DropColumn(
                name: "IncludedFeatures",
                table: "PurchasedPackages");

            migrationBuilder.DropColumn(
                name: "Code",
                table: "PackageOptionFeatures");
        }
    }
}
