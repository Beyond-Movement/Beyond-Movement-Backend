using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeyondMovement.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CompletePhase5Scheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CalendlyReconciliationRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RemoteCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedCount = table.Column<int>(type: "integer", nullable: false),
                    UpdatedCount = table.Column<int>(type: "integer", nullable: false),
                    CancelledCount = table.Column<int>(type: "integer", nullable: false),
                    FlaggedCount = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendlyReconciliationRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CalendlyUnmatchedBookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CalendlyEventUri = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CalendlyInviteeUri = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CalendlyEventTypeUri = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    InviteeEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    DiscoveredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AssignedAthleteProfileId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendlyUnmatchedBookings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SchedulingChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    DedupKey = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PublishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchedulingChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SchedulingChanges_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SchedulingBookingOperations_SessionId",
                table: "SchedulingBookingOperations",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendlyReconciliationRuns_StartedAtUtc",
                table: "CalendlyReconciliationRuns",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CalendlyUnmatchedBookings_CalendlyInviteeUri",
                table: "CalendlyUnmatchedBookings",
                column: "CalendlyInviteeUri",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendlyUnmatchedBookings_ResolvedAtUtc_DiscoveredAtUtc",
                table: "CalendlyUnmatchedBookings",
                columns: new[] { "ResolvedAtUtc", "DiscoveredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SchedulingChanges_DedupKey",
                table: "SchedulingChanges",
                column: "DedupKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SchedulingChanges_PublishedAtUtc_OccurredAtUtc",
                table: "SchedulingChanges",
                columns: new[] { "PublishedAtUtc", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SchedulingChanges_SessionId",
                table: "SchedulingChanges",
                column: "SessionId");

            migrationBuilder.AddForeignKey(
                name: "FK_SchedulingBookingOperations_AthleteProfiles_AthleteProfileId",
                table: "SchedulingBookingOperations",
                column: "AthleteProfileId",
                principalTable: "AthleteProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SchedulingBookingOperations_Sessions_SessionId",
                table: "SchedulingBookingOperations",
                column: "SessionId",
                principalTable: "Sessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Sessions_AthleteProfiles_AthleteProfileId",
                table: "Sessions",
                column: "AthleteProfileId",
                principalTable: "AthleteProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SchedulingBookingOperations_AthleteProfiles_AthleteProfileId",
                table: "SchedulingBookingOperations");

            migrationBuilder.DropForeignKey(
                name: "FK_SchedulingBookingOperations_Sessions_SessionId",
                table: "SchedulingBookingOperations");

            migrationBuilder.DropForeignKey(
                name: "FK_Sessions_AthleteProfiles_AthleteProfileId",
                table: "Sessions");

            migrationBuilder.DropTable(
                name: "CalendlyReconciliationRuns");

            migrationBuilder.DropTable(
                name: "CalendlyUnmatchedBookings");

            migrationBuilder.DropTable(
                name: "SchedulingChanges");

            migrationBuilder.DropIndex(
                name: "IX_SchedulingBookingOperations_SessionId",
                table: "SchedulingBookingOperations");
        }
    }
}
