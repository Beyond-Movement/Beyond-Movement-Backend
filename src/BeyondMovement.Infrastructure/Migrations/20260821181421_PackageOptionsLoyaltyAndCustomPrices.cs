using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeyondMovement.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PackageOptionsLoyaltyAndCustomPrices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsLoyal",
                table: "AthleteProfiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LoyalSinceUtc",
                table: "AthleteProfiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PackageOptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CoachId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Sessions = table.Column<int>(type: "integer", nullable: false),
                    DefaultPriceMinor = table.Column<long>(type: "bigint", nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    ArchivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageOptions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AthletePackagePrices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AthleteUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PackageOptionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PriceMinor = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AthletePackagePrices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AthletePackagePrices_PackageOptions_PackageOptionId",
                        column: x => x.PackageOptionId,
                        principalTable: "PackageOptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PackageOptionFeatures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PackageOptionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageOptionFeatures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PackageOptionFeatures_PackageOptions_PackageOptionId",
                        column: x => x.PackageOptionId,
                        principalTable: "PackageOptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AthletePackagePrices_AthleteUserId",
                table: "AthletePackagePrices",
                column: "AthleteUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AthletePackagePrices_AthleteUserId_PackageOptionId",
                table: "AthletePackagePrices",
                columns: new[] { "AthleteUserId", "PackageOptionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AthletePackagePrices_PackageOptionId",
                table: "AthletePackagePrices",
                column: "PackageOptionId");

            migrationBuilder.CreateIndex(
                name: "IX_PackageOptionFeatures_PackageOptionId_Position",
                table: "PackageOptionFeatures",
                columns: new[] { "PackageOptionId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PackageOptions_CoachId_IsArchived",
                table: "PackageOptions",
                columns: new[] { "CoachId", "IsArchived" });

            migrationBuilder.CreateIndex(
                name: "IX_PackageOptions_CoachId_Name_Active",
                table: "PackageOptions",
                columns: new[] { "CoachId", "Name" },
                unique: true,
                filter: "\"IsArchived\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AthletePackagePrices");

            migrationBuilder.DropTable(
                name: "PackageOptionFeatures");

            migrationBuilder.DropTable(
                name: "PackageOptions");

            migrationBuilder.DropColumn(
                name: "IsLoyal",
                table: "AthleteProfiles");

            migrationBuilder.DropColumn(
                name: "LoyalSinceUtc",
                table: "AthleteProfiles");
        }
    }
}
