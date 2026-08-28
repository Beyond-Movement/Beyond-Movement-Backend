using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeyondMovement.Infrastructure.Migrations
{
    /// <summary>
    /// BR-07 changes from a duration rule to the Admin's explicit choice, so the choice needs
    /// somewhere to live. See contract/CHANGELOG.md.
    /// </summary>
    public partial class AddObservationDeductsSession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ObservationDeductsSession",
                table: "Sessions",
                type: "boolean",
                nullable: true);

            // Existing observations predate the choice, so it is reconstructed from the rule that
            // was in force when they were recorded. That matters most for the ones already
            // attended: their ConsumedSessionCount was decided by this same expression, and
            // defaulting them to false would leave the session disagreeing with the package
            // balance it actually moved. Scheduled ones inherit the answer the old rule would
            // have given them, which is the least surprising thing for a coach who recorded them
            // under it.
            //
            // Must run before the constraint below, which the null column would otherwise fail.
            migrationBuilder.Sql("""
                UPDATE "Sessions"
                SET "ObservationDeductsSession" = ("DurationMinutes" > 60)
                WHERE "DeliveryType" = 'Observation';
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Sessions_ObservationDeductsSession",
                table: "Sessions",
                sql: "(\"DeliveryType\" = 'Observation') = (\"ObservationDeductsSession\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Sessions_ObservationDeductsSession",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "ObservationDeductsSession",
                table: "Sessions");
        }
    }
}
