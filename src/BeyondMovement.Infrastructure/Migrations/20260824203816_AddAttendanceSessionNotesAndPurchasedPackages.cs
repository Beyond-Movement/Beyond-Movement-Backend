using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeyondMovement.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAttendanceSessionNotesAndPurchasedPackages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "CalendlyInviteeUri",
                table: "Sessions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500);

            migrationBuilder.AlterColumn<string>(
                name: "CalendlyEventUri",
                table: "Sessions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500);

            migrationBuilder.AlterColumn<string>(
                name: "CalendlyEventTypeUri",
                table: "Sessions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500);

            migrationBuilder.AddColumn<DateTime>(
                name: "AttendedAtUtc",
                table: "Sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AttendedByUserId",
                table: "Sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConsumedSessionCount",
                table: "Sessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "PurchasedPackages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CoachId = table.Column<Guid>(type: "uuid", nullable: false),
                    AthleteProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    PackageOptionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TotalSessions = table.Column<int>(type: "integer", nullable: false),
                    UsedSessions = table.Column<int>(type: "integer", nullable: false),
                    PricePaidMinor = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchasedPackages", x => x.Id);
                    table.CheckConstraint("CK_PurchasedPackages_Dates", "\"EndDate\" IS NULL OR \"EndDate\" >= \"StartDate\"");
                    table.CheckConstraint("CK_PurchasedPackages_Price", "\"PricePaidMinor\" >= 0");
                    table.CheckConstraint("CK_PurchasedPackages_TotalSessions", "\"TotalSessions\" > 0");
                    table.CheckConstraint("CK_PurchasedPackages_UsedSessions", "\"UsedSessions\" >= 0 AND \"UsedSessions\" <= \"TotalSessions\"");
                    table.ForeignKey(
                        name: "FK_PurchasedPackages_AthleteProfiles_AthleteProfileId",
                        column: x => x.AthleteProfileId,
                        principalTable: "AthleteProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchasedPackages_PackageOptions_PackageOptionId",
                        column: x => x.PackageOptionId,
                        principalTable: "PackageOptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "SessionNotes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Content = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SessionNotes_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_PackageId",
                table: "Sessions",
                column: "PackageId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Sessions_Consumed",
                table: "Sessions",
                sql: "\"ConsumedSessionCount\" IN (0, 1)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Sessions_ConsumedOnlyWhenResolved",
                table: "Sessions",
                sql: "\"ConsumedSessionCount\" = 0 OR \"Status\" IN ('Attended', 'NoShow')");

            migrationBuilder.CreateIndex(
                name: "IX_PurchasedPackages_AthleteProfileId_CreatedAtUtc",
                table: "PurchasedPackages",
                columns: new[] { "AthleteProfileId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchasedPackages_OneActivePerAthlete",
                table: "PurchasedPackages",
                column: "AthleteProfileId",
                unique: true,
                filter: "\"Status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_PurchasedPackages_PackageOptionId",
                table: "PurchasedPackages",
                column: "PackageOptionId");

            migrationBuilder.CreateIndex(
                name: "IX_SessionNotes_SessionId_CreatedAtUtc",
                table: "SessionNotes",
                columns: new[] { "SessionId", "CreatedAtUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_Sessions_PurchasedPackages_PackageId",
                table: "Sessions",
                column: "PackageId",
                principalTable: "PurchasedPackages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Sessions_PurchasedPackages_PackageId",
                table: "Sessions");

            migrationBuilder.DropTable(
                name: "PurchasedPackages");

            migrationBuilder.DropTable(
                name: "SessionNotes");

            migrationBuilder.DropIndex(
                name: "IX_Sessions_PackageId",
                table: "Sessions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Sessions_Consumed",
                table: "Sessions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Sessions_ConsumedOnlyWhenResolved",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "AttendedAtUtc",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "AttendedByUserId",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "ConsumedSessionCount",
                table: "Sessions");

            migrationBuilder.AlterColumn<string>(
                name: "CalendlyInviteeUri",
                table: "Sessions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CalendlyEventUri",
                table: "Sessions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CalendlyEventTypeUri",
                table: "Sessions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);
        }
    }
}
