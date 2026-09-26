using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeyondMovement.Infrastructure.Migrations
{
    /// <summary>
    /// Users.Phone becomes an E.164 column: varchar(16), a "+" and at most 15 digits. It stays
    /// <b>nullable</b> — the seeded Admin and every account created before phone numbers were
    /// collected have none, and NOT NULL would need an invented number for each.
    /// <para>
    /// Existing values were stored as typed. SQL cannot parse them against numbering plans the
    /// way PhonePolicy does, so this does only the part that is lossless — dropping the
    /// separators and reading a leading 00 as + — which turns every internationally written
    /// number into its E.164 form. A number written nationally (010…) keeps its digits, still
    /// reads back, and is normalized by the next profile save.
    /// </para>
    /// <para>
    /// Anything still longer than 16 characters has more than 15 digits, so it cannot be a phone
    /// number. It is refused rather than truncated or nulled: this is someone's contact detail,
    /// and deciding what it should have been is not a migration's call.
    /// </para>
    /// </summary>
    public partial class NormalizeUserPhoneToE164 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "Users"
                SET "Phone" = regexp_replace(regexp_replace("Phone", '[\s().-]', '', 'g'), '^00', '+')
                WHERE "Phone" IS NOT NULL;

                UPDATE "Users" SET "Phone" = NULL WHERE "Phone" = '';

                DO $$
                DECLARE too_long integer;
                BEGIN
                    SELECT count(*) INTO too_long FROM "Users" WHERE length("Phone") > 16;
                    IF too_long > 0 THEN
                        RAISE EXCEPTION
                            '% Users.Phone value(s) have more than 15 digits and cannot be a phone number. Correct or clear them, then apply this migration again.',
                            too_long;
                    END IF;
                END $$;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "Phone",
                table: "Users",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(40)",
                oldMaxLength: 40,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Widening only. The normalized values are valid under the old, looser rules, so
            // there is nothing to restore — and the original formatting is not recoverable.
            migrationBuilder.AlterColumn<string>(
                name: "Phone",
                table: "Users",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16,
                oldNullable: true);
        }
    }
}
