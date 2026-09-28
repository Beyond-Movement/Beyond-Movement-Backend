using System;
using System.Linq;
using BeyondMovement.Modules.Athletes.Domain;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace BeyondMovement.Infrastructure.Migrations
{
    /// <summary>
    /// Athlete sport moves from free text to the sports catalogue: a <c>Sports</c> table seeded
    /// from <c>SportCatalogue</c>, and <c>AthleteProfiles.SportId</c> in place of
    /// <c>AthleteProfiles.Sport</c>. Pre-production, so the old column is converted and dropped
    /// here rather than kept alongside.
    /// <para>
    /// The order matters — the scaffolded version dropped <c>Sport</c> first and lost every value:
    /// </para>
    /// <list type="number">
    /// <item>Create and seed <c>Sports</c>, with names unique ignoring case.</item>
    /// <item>Add <c>SportId</c>, nullable: a profile exists before Complete Profile picks a sport.</item>
    /// <item>Backfill it from the old text, by <b>explicit rules only</b>: the reviewed dev-data
    /// spellings in <see cref="LegacySportMappings"/>, then a value that already <em>is</em> a
    /// catalogue name, ignoring case and surrounding spaces. Nothing is fuzzy-matched.</item>
    /// <item>Values in <see cref="LegacySportJunk"/> were never a sport. The sport is cleared and
    /// the athlete's profile is marked not completed, so the app sends them back through Complete
    /// Profile — "completed" promises a sport, and would otherwise be a lie.</item>
    /// <item>Anything else is refused: the migration raises, rolls back whole and names the
    /// values, rather than guessing or discarding them. Decide what each should be, then run
    /// it again.</item>
    /// <item>Only then drop <c>Sport</c>, and add the foreign key — Restrict, so a sport somebody
    /// has picked cannot be deleted.</item>
    /// </list>
    /// </summary>
    public partial class AddSportsCatalogue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Sports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sports", x => x.Id);
                });

            // Unique ignoring case, so "tennis" can never be added beside "Tennis". EF cannot
            // express an index on an expression, so the model does not know about this one.
            migrationBuilder.Sql(
                """CREATE UNIQUE INDEX "IX_Sports_Name_CaseInsensitive" ON "Sports" (lower("Name"));""");

            migrationBuilder.InsertData(
                table: "Sports",
                columns: new[] { "Id", "Name", "SortOrder" },
                values: new object[,]
                {
                    { new Guid("0b6f3c1e-58a2-4d7e-9c41-2f8a6e1d3b70"), "Artistic Swimming", 0 },
                    { new Guid("0c7e2a9d-5f3b-4d86-a1c4-9e2b5f8d3a16"), "Karate", 0 },
                    { new Guid("13f8a6c2-4d9e-4a5b-b7f3-9d1c6e4a8f75"), "Weightlifting", 0 },
                    { new Guid("1f4e7b9a-8c2d-4a56-9f3e-7b1d4a6c9e82"), "Fencing", 0 },
                    { new Guid("24a7d9f3-8e1c-4b6a-a3f9-5c8e2d6b9f10"), "Rowing", 0 },
                    { new Guid("2a6e9c3f-8b1d-4c74-8e2a-5f9c3b6d1e42"), "Triathlon", 0 },
                    { new Guid("2e8d6a41-9b3f-4c7e-a5d2-6f1b8c4e3a90"), "Boxing", 0 },
                    { new Guid("3b8f6d2a-9c4e-4e71-8f3b-7a2d5e9c6b84"), "Sailing", 0 },
                    { new Guid("3d9a5f12-7c4e-4a8b-b1f6-5e2d8c9a0f47"), "Athletics", 0 },
                    { new Guid("46c2f8b9-1a5d-4f3e-a7c2-8d5b3e9f1a64"), "Table Tennis", 0 },
                    { new Guid("47b2e9d5-3a6c-4f1e-8d4b-9e5a2c7f0b61"), "Diving", 0 },
                    { new Guid("58b3f1d7-6c9a-4e2f-8a7d-2e5c9b1f4d83"), "Modern Pentathlon", 0 },
                    { new Guid("5d9c1f4b-2e8a-4b67-9c3d-4e8a1f6b9d25"), "Squash", 0 },
                    { new Guid("5e2c7a94-1f3b-4c86-a0d5-7b9e2c4f1a38"), "Football", 0 },
                    { new Guid("61f7b3d8-4a2c-4e9d-8b5a-3c6e0f9d2a17"), "Basketball", 0 },
                    { new Guid("6a9e3d1c-7f5b-4e8a-b2c6-8d4f1a3e7b95"), "Golf", 0 },
                    { new Guid("73d1b6e9-2a4f-4c8b-9e5d-1f6a3c8b4d27"), "Handball", 0 },
                    { new Guid("7f1d4a8c-3e6b-4e92-9a5f-1b4d7c2e8f56"), "Trampoline", 0 },
                    { new Guid("8a1c4e7b-2d9f-4e35-a6b8-0f3c7d1e9a52"), "Badminton", 0 },
                    { new Guid("8e2a5c9f-4d7b-4c38-a6e1-9f3b7d2a5c49"), "Shooting", 0 },
                    { new Guid("9b4c7e1a-5f2d-4b39-a8c6-3e7a1d5f9b28"), "Volleyball", 0 },
                    { new Guid("9c3f1a6e-5d8b-4b2a-9e7c-4a0d6f2b8e13"), "Cycling", 0 },
                    { new Guid("a4f8c3e6-9d2b-4a71-b5e8-3c1f7d9a6e48"), "Judo", 0 },
                    { new Guid("b1e7a3d6-9f2c-4d85-8b4e-6c2f9a3d7e18"), "Swimming", 0 },
                    { new Guid("b8d1f5a3-4e7c-4c29-a8f1-5d3b9e6a2c74"), "Fin Swimming", 0 },
                    { new Guid("c4a81e27-9d5f-4b3a-8e6c-1a7d9f2b5e04"), "Tennis", 0 },
                    { new Guid("c6a3e9d1-2b7f-4d48-8a5c-7e3f1b9d4a26"), "Wrestling", 0 },
                    { new Guid("c9e6a2d4-1b8f-4f53-9d2a-6b4e8c3f1a57"), "Padel", 0 },
                    { new Guid("d5a8c2f7-6e1b-4d93-b7a4-2c9f5e8d1a36"), "Equestrian", 0 },
                    { new Guid("da3f9b5e-6c1a-4a28-b3d7-2f9e5c1a8b37"), "Taekwondo", 0 },
                    { new Guid("e2c5a8f1-3b9d-4f7a-8c1e-4b7d2f9a5c63"), "Gymnastics", 0 },
                    { new Guid("e5d2b8f4-7a3c-4f16-9b1e-8c4a2f7d3b69"), "Water Polo", 0 },
                    { new Guid("f0e9d8c7-2b4a-4f61-9d3e-8c5b7a1e6d29"), "Other", 1 },
                    { new Guid("f7b4e1c8-3d6a-4a2e-b9c5-1d7f4a8e2c36"), "Rugby", 0 }
                });

            migrationBuilder.AddColumn<Guid>(
                name: "SportId",
                table: "AthleteProfiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql($$"""
                UPDATE "AthleteProfiles" SET "Sport" = NULL WHERE btrim("Sport") = '';

                UPDATE "AthleteProfiles" p
                SET "SportId" = m.sport_id
                FROM (VALUES {{LegacySportMappingValues}}) AS m(legacy, sport_id)
                WHERE p."Sport" = m.legacy;

                UPDATE "AthleteProfiles" p
                SET "SportId" = s."Id"
                FROM "Sports" s
                WHERE p."SportId" IS NULL AND lower(btrim(p."Sport")) = lower(s."Name");

                UPDATE "Users" u
                SET "ProfileCompletedAtUtc" = NULL
                FROM "AthleteProfiles" p
                WHERE p."UserId" = u."Id" AND p."SportId" IS NULL
                  AND p."Sport" IN ({{LegacySportJunkValues}});

                UPDATE "AthleteProfiles" SET "Sport" = NULL
                WHERE "SportId" IS NULL AND "Sport" IN ({{LegacySportJunkValues}});

                DO $$
                DECLARE unmapped text;
                BEGIN
                    SELECT string_agg(DISTINCT quote_literal("Sport"), ', ') INTO unmapped
                    FROM "AthleteProfiles" WHERE "SportId" IS NULL AND "Sport" IS NOT NULL;
                    IF unmapped IS NOT NULL THEN
                        RAISE EXCEPTION
                            'AthleteProfiles.Sport has values that match no sport in the catalogue: %. Add each to LegacySportMappings or LegacySportJunk in AddSportsCatalogue, or correct the rows, then apply this migration again.',
                            unmapped;
                    END IF;
                END $$;
                """);

            migrationBuilder.DropColumn(
                name: "Sport",
                table: "AthleteProfiles");

            migrationBuilder.CreateIndex(
                name: "IX_AthleteProfiles_SportId",
                table: "AthleteProfiles",
                column: "SportId");

            migrationBuilder.AddForeignKey(
                name: "FK_AthleteProfiles_Sports_SportId",
                table: "AthleteProfiles",
                column: "SportId",
                principalTable: "Sports",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <summary>
        /// Dev-data spellings reviewed and approved as a catalogue sport, matched exactly as stored.
        /// A value that is already a catalogue name ("Tennis", "Football") needs no entry.
        /// </summary>
        internal static readonly (string Legacy, Guid SportId)[] LegacySportMappings =
        [
            ("Artistic Swimmi.g", SportCatalogue.ArtisticSwimming),
            ("artistic swimmimg", SportCatalogue.ArtisticSwimming),
            ("AS", SportCatalogue.ArtisticSwimming)
        ];

        /// <summary>Dev-data values reviewed and confirmed not to be a sport at all.</summary>
        internal static readonly string[] LegacySportJunk = ["Hi"];

        private static string LegacySportMappingValues => string.Join(", ",
            LegacySportMappings.Select(m => $"({Literal(m.Legacy)}, '{m.SportId}'::uuid)"));

        private static string LegacySportJunkValues => string.Join(", ", LegacySportJunk.Select(Literal));

        private static string Literal(string value) => $"'{value.Replace("'", "''")}'";

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AthleteProfiles_Sports_SportId",
                table: "AthleteProfiles");

            migrationBuilder.DropIndex(
                name: "IX_AthleteProfiles_SportId",
                table: "AthleteProfiles");

            migrationBuilder.AddColumn<string>(
                name: "Sport",
                table: "AthleteProfiles",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            // Back to text as the catalogue names it. The original spellings are not recoverable,
            // and profiles marked not completed stay that way.
            migrationBuilder.Sql("""
                UPDATE "AthleteProfiles" p SET "Sport" = s."Name"
                FROM "Sports" s WHERE p."SportId" = s."Id";
                """);

            migrationBuilder.DropColumn(
                name: "SportId",
                table: "AthleteProfiles");

            migrationBuilder.DropTable(
                name: "Sports");
        }
    }
}
