using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeyondMovement.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProfileCompletedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ProfileCompletedAtUtc",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            // Backfill. Every account that already exists predates the Complete Profile step,
            // so leaving them null would send existing users to a profile screen they have
            // no reason to see. New athletes get null and are routed there correctly.
            migrationBuilder.Sql(
                """
                UPDATE "Users"
                SET "ProfileCompletedAtUtc" = "CreatedAtUtc"
                WHERE "ProfileCompletedAtUtc" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProfileCompletedAtUtc",
                table: "Users");
        }
    }
}
