using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeyondMovement.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPackagePurchases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PackagePurchases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CoachId = table.Column<Guid>(type: "uuid", nullable: false),
                    AthleteProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    AthleteUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PackageOptionId = table.Column<Guid>(type: "uuid", nullable: true),
                    PackageName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SessionCount = table.Column<int>(type: "integer", nullable: false),
                    PriceMinor = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Origin = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PaidAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PaidByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    PurchasedPackageId = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    Features = table.Column<List<string>>(type: "character varying(100)[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackagePurchases", x => x.Id);
                    table.CheckConstraint("CK_PackagePurchases_PaidConsistency", "(\"Status\" = 'Paid' AND \"PaidAtUtc\" IS NOT NULL AND \"PurchasedPackageId\" IS NOT NULL) OR (\"Status\" = 'Pending' AND \"PaidAtUtc\" IS NULL AND \"PurchasedPackageId\" IS NULL)");
                    table.CheckConstraint("CK_PackagePurchases_Price", "\"PriceMinor\" >= 0");
                    table.CheckConstraint("CK_PackagePurchases_SessionCount", "\"SessionCount\" > 0");
                    table.ForeignKey(
                        name: "FK_PackagePurchases_AthleteProfiles_AthleteProfileId",
                        column: x => x.AthleteProfileId,
                        principalTable: "AthleteProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PackagePurchases_PackageOptions_PackageOptionId",
                        column: x => x.PackageOptionId,
                        principalTable: "PackageOptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PackagePurchases_PurchasedPackages_PurchasedPackageId",
                        column: x => x.PurchasedPackageId,
                        principalTable: "PurchasedPackages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PackagePurchases_AthleteUserId_CreatedAtUtc",
                table: "PackagePurchases",
                columns: new[] { "AthleteUserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PackagePurchases_CoachId_Status_CreatedAtUtc",
                table: "PackagePurchases",
                columns: new[] { "CoachId", "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PackagePurchases_OnePendingPerAthlete",
                table: "PackagePurchases",
                column: "AthleteProfileId",
                unique: true,
                filter: "\"Status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_PackagePurchases_OnePurchasePerPackage",
                table: "PackagePurchases",
                column: "PurchasedPackageId",
                unique: true,
                filter: "\"PurchasedPackageId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PackagePurchases_PackageOptionId",
                table: "PackagePurchases",
                column: "PackageOptionId");

            // Backfill. Every package that existed before this phase gets a Paid purchase behind
            // it, so payment history and the Athlete Profile's payment status cover all packages
            // rather than only those bought through the app from now on. Without this, a coach
            // opening the payments screen the day after deployment would see an empty list beside
            // athletes who are demonstrably training on packages they paid for.
            //
            // These are recorded as AdminDirect, which is what they were: every package created
            // before Phase 8 came from POST /athletes/{id}/packages, which only an Admin can call.
            //
            // Two fields cannot be recovered and are deliberately left as they are rather than
            // invented:
            //   - PaidByUserId is NULL. Which Admin confirmed the money is not in any table, and
            //     naming the seeded Admin would be a guess written into an audit trail.
            //   - Features is an empty array. The snapshot must be what the athlete was shown at
            //     purchase time; the catalogue option's features today may have been edited since,
            //     so copying them now would fabricate a snapshot rather than restore one.
            // The contract documents both, so the app can tell a legacy row from a real one.
            migrationBuilder.Sql(
                """
                INSERT INTO "PackagePurchases" (
                    "Id", "CoachId", "AthleteProfileId", "AthleteUserId", "PackageOptionId",
                    "PackageName", "SessionCount", "Features", "PriceMinor", "Currency",
                    "Status", "Origin", "CreatedAtUtc", "UpdatedAtUtc",
                    "PaidAtUtc", "PaidByUserId", "PurchasedPackageId")
                SELECT
                    gen_random_uuid(), p."CoachId", p."AthleteProfileId", a."UserId",
                    p."PackageOptionId", p."Name", p."TotalSessions",
                    ARRAY[]::character varying(100)[], p."PricePaidMinor", p."Currency",
                    'Paid', 'AdminDirect', p."CreatedAtUtc", p."CreatedAtUtc",
                    p."CreatedAtUtc", NULL, p."Id"
                FROM "PurchasedPackages" p
                JOIN "AthleteProfiles" a ON a."Id" = p."AthleteProfileId"
                WHERE NOT EXISTS (
                    SELECT 1 FROM "PackagePurchases" x WHERE x."PurchasedPackageId" = p."Id");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PackagePurchases");
        }
    }
}
