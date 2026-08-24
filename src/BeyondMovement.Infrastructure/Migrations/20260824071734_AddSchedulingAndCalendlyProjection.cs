using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeyondMovement.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSchedulingAndCalendlyProjection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CalendlyWebhookEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendlyWebhookEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CoachId = table.Column<Guid>(type: "uuid", nullable: false),
                    AthleteProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    PackageId = table.Column<Guid>(type: "uuid", nullable: true),
                    CalendlyEventUri = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CalendlyInviteeUri = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CalendlyEventTypeUri = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ScheduledStartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ScheduledEndUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    DeliveryType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LocationOrPlatform = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    MeetingUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CancelUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RescheduleUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CancelledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancellationReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                    table.CheckConstraint("CK_Sessions_Duration", "\"DurationMinutes\" > 0");
                    table.CheckConstraint("CK_Sessions_TimeRange", "\"ScheduledEndUtc\" > \"ScheduledStartUtc\"");
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalendlyWebhookEvents_IdempotencyKey",
                table: "CalendlyWebhookEvents",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendlyWebhookEvents_Status_ReceivedAtUtc",
                table: "CalendlyWebhookEvents",
                columns: new[] { "Status", "ReceivedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_AthleteProfileId_ScheduledStartUtc",
                table: "Sessions",
                columns: new[] { "AthleteProfileId", "ScheduledStartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_CalendlyEventUri",
                table: "Sessions",
                column: "CalendlyEventUri",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_CalendlyInviteeUri",
                table: "Sessions",
                column: "CalendlyInviteeUri",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_CoachId_ScheduledStartUtc_Status",
                table: "Sessions",
                columns: new[] { "CoachId", "ScheduledStartUtc", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CalendlyWebhookEvents");

            migrationBuilder.DropTable(
                name: "Sessions");
        }
    }
}
