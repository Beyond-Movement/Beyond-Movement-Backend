using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeyondMovement.Infrastructure.Migrations
{
    /// <summary>
    /// Registration no longer collects a name, so Users.FullName becomes nullable, and Gender
    /// becomes an enum stored as its name rather than free text.
    /// <para>
    /// Backward-compatible in the direction that matters: the column widens from NOT NULL to
    /// nullable, so a running instance of the previous build keeps working against this schema
    /// until it is replaced.
    /// </para>
    /// </summary>
    public partial class NullableFullNameAndGenderEnum : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "FullName",
                table: "Users",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            // The previous build wrote "" for an athlete who registered without a name. The
            // contract now says null, and an empty string would read as a completed name.
            migrationBuilder.Sql("""
                UPDATE "Users" SET "FullName" = NULL WHERE btrim("FullName") = '';
                """);

            // Gender was free text. Map what the app actually sent onto the enum's names, and
            // null anything else rather than let a value the API can no longer emit survive
            // and fail to deserialise on the next read.
            migrationBuilder.Sql("""
                UPDATE "AthleteProfiles"
                SET "Gender" = CASE lower(btrim("Gender"))
                    WHEN 'female' THEN 'Female'
                    WHEN 'f'      THEN 'Female'
                    WHEN 'male'   THEN 'Male'
                    WHEN 'm'      THEN 'Male'
                    ELSE NULL
                END
                WHERE "Gender" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "FullName",
                table: "Users",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);
        }
    }
}
