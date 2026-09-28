using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeyondMovement.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionNoteAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SessionNoteAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionNoteId = table.Column<Guid>(type: "uuid", nullable: true),
                    StorageKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DeclaredSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    UploadedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UploadExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CommittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletionRequestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionNoteAttachments", x => x.Id);
                    table.CheckConstraint("CK_SessionNoteAttachments_CommittedConsistency", "(\"Status\" = 'Pending' AND \"CommittedAtUtc\" IS NULL AND \"SizeBytes\" IS NULL) OR (\"Status\" = 'Committed' AND \"CommittedAtUtc\" IS NOT NULL AND \"SizeBytes\" IS NOT NULL) OR \"Status\" = 'Deleting'");
                    table.CheckConstraint("CK_SessionNoteAttachments_DeclaredSize", "\"DeclaredSizeBytes\" > 0");
                    table.CheckConstraint("CK_SessionNoteAttachments_DeletionConsistency", "(\"Status\" = 'Deleting') = (\"DeletionRequestedAtUtc\" IS NOT NULL)");
                    table.CheckConstraint("CK_SessionNoteAttachments_SortOrder", "\"SortOrder\" >= 0");
                    table.ForeignKey(
                        name: "FK_SessionNoteAttachments_SessionNotes_SessionNoteId",
                        column: x => x.SessionNoteId,
                        principalTable: "SessionNotes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SessionNoteAttachments_SessionNoteId_Status_SortOrder",
                table: "SessionNoteAttachments",
                columns: new[] { "SessionNoteId", "Status", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_SessionNoteAttachments_Status_UploadExpiresAtUtc",
                table: "SessionNoteAttachments",
                columns: new[] { "Status", "UploadExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SessionNoteAttachments_StorageKey",
                table: "SessionNoteAttachments",
                column: "StorageKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SessionNoteAttachments");
        }
    }
}
