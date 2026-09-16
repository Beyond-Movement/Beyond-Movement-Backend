using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeyondMovement.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddObservationRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ObservationRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CoachId = table.Column<Guid>(type: "uuid", nullable: false),
                    AthleteProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedStartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RequestedDurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    Location = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Details = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ObservationRequests", x => x.Id);
                    table.CheckConstraint("CK_ObservationRequests_AcceptedHasSession", "(\"Status\" = 'Accepted' AND \"SessionId\" IS NOT NULL) OR (\"Status\" <> 'Accepted' AND \"SessionId\" IS NULL)");
                    table.CheckConstraint("CK_ObservationRequests_Duration", "\"RequestedDurationMinutes\" BETWEEN 15 AND 1440");
                    table.CheckConstraint("CK_ObservationRequests_ResolvedConsistency", "(\"Status\" = 'Pending' AND \"ResolvedAtUtc\" IS NULL AND \"ResolvedByUserId\" IS NULL) OR (\"Status\" <> 'Pending' AND \"ResolvedAtUtc\" IS NOT NULL AND \"ResolvedByUserId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_ObservationRequests_AthleteProfiles_AthleteProfileId",
                        column: x => x.AthleteProfileId,
                        principalTable: "AthleteProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ObservationRequests_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ObservationRequests_AthleteProfileId_CreatedAtUtc",
                table: "ObservationRequests",
                columns: new[] { "AthleteProfileId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ObservationRequests_CoachId_Status_RequestedStartUtc",
                table: "ObservationRequests",
                columns: new[] { "CoachId", "Status", "RequestedStartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ObservationRequests_OneRequestPerSession",
                table: "ObservationRequests",
                column: "SessionId",
                unique: true,
                filter: "\"SessionId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ObservationRequests");
        }
    }
}
