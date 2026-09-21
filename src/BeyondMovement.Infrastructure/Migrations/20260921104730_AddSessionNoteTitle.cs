using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeyondMovement.Infrastructure.Migrations
{
    /// <summary>
    /// A session note gains a required <c>Title</c>.
    /// <para>
    /// Three steps rather than one, because the column is required and the table already has rows.
    /// The scaffolded version of this migration added it <c>NOT NULL DEFAULT ''</c> in a single
    /// operation, which would have given every existing note an <b>empty</b> title — a value the
    /// validator rejects, so the coach could not have edited such a note without first inventing a
    /// title for it. Added nullable, backfilled, then made required.
    /// </para>
    /// <para>
    /// The backfill reads each note's <b>own first line</b>. That is a shortened restatement of
    /// what the coach actually wrote, not a fact copied from somewhere else — unlike the Phase 8
    /// feature backfill, which deliberately wrote empty arrays rather than copy a catalogue that
    /// may have been edited since. A title produced here is editable like any other, and
    /// <c>'Untitled note'</c> is only a fallback for content that has no first line at all, which
    /// the validator already prevents but the migration should not assume.
    /// </para>
    /// </summary>
    public partial class AddSessionNoteTitle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Nullable first, so adding it cannot fail on the rows that are already there.
            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "SessionNotes",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            // 2. Backfill from the note's own text: normalise CRLF, take the first line, trim it,
            //    and cut it to the column width. NULLIF turns a line that trimmed away to nothing
            //    into NULL so COALESCE can catch it.
            migrationBuilder.Sql(
                """
                UPDATE "SessionNotes"
                SET "Title" = COALESCE(
                    NULLIF(btrim(left(
                        split_part(replace("Content", E'\r\n', E'\n'), E'\n', 1), 200)), ''),
                    'Untitled note')
                WHERE "Title" IS NULL;
                """);

            // 3. Only now is every row able to satisfy it.
            migrationBuilder.AlterColumn<string>(
                name: "Title",
                table: "SessionNotes",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Titles the coach wrote after this shipped are lost, which is unavoidable: the shape
            // being restored has nowhere to put them. The backfilled ones are recoverable by
            // re-running Up, since they are derived from Content, which is untouched.
            migrationBuilder.DropColumn(
                name: "Title",
                table: "SessionNotes");
        }
    }
}
