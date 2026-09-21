using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Scheduling.Domain;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// The Admin's Session Notes History: every note belonging to one athlete, gathered across all
/// their sessions, newest first.
/// <para>
/// The two things these hold on to are that <b>an observation is simply one delivery type</b> —
/// its notes sit in the same history beside Online and FaceToFace, with no separate system — and
/// that the notes are the <b>same rows</b> the per-session endpoints write, read through a second
/// view rather than copied into one.
/// </para>
/// <para>
/// Online and FaceToFace sessions are seeded through the database rather than an endpoint,
/// because every session but an observation comes from Calendly and that is not reachable from a
/// test. The notes on them are still written through the real API.
/// </para>
/// </summary>
public sealed class SessionNoteHistoryTests(AthleteApiFactory factory) : IClassFixture<AthleteApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string Athlete = "alex@nowhere.test";
    private const string OtherAthlete = "jordan@nowhere.test";

    /// <summary>Kept to itself, so the tie-break test pages over its own rows and nobody else's.</summary>
    private const string TieBreakAthlete = "sam@nowhere.test";

    /// <summary>Never given a note, so the empty-page test stays true however often it runs.</summary>
    private const string AthleteWithNoNotes = "robin@nowhere.test";

    private sealed record AuthPayload(string AccessToken, string RefreshToken);

    // ------------------------------------------------- every delivery type

    [Theory]
    [InlineData(DeliveryType.Online)]
    [InlineData(DeliveryType.FaceToFace)]
    [InlineData(DeliveryType.Observation)]
    public async Task The_history_includes_notes_from_any_delivery_type(DeliveryType deliveryType)
    {
        var admin = await AdminClientAsync();
        var athleteUserId = await AthleteUserIdAsync(admin, Athlete);
        var profileId = await ProfileIdAsync(Athlete);

        var content = $"A note on a {deliveryType} session {Guid.NewGuid()}";
        var sessionId = await SeedSessionAsync(profileId, deliveryType, Future(days: 1));
        await WriteNoteAsync(admin, sessionId, content);

        var row = await FindAsync(admin, athleteUserId, content);

        Assert.Equal(sessionId, row.GetProperty("sessionId").GetGuid());
        Assert.Equal(deliveryType.ToString(), row.GetProperty("sessionDeliveryType").GetString());
    }

    /// <summary>
    /// The point of the screen: one history, not three. An Observation is not filtered out, not
    /// separated, and not marked by anything other than its delivery type.
    /// </summary>
    [Fact]
    public async Task Notes_from_several_delivery_types_appear_together()
    {
        var admin = await AdminClientAsync();
        var athleteUserId = await AthleteUserIdAsync(admin, OtherAthlete);
        var profileId = await ProfileIdAsync(OtherAthlete);
        var tag = Guid.NewGuid().ToString();

        foreach (var deliveryType in new[]
                 { DeliveryType.Online, DeliveryType.FaceToFace, DeliveryType.Observation })
        {
            var sessionId = await SeedSessionAsync(profileId, deliveryType, Future(days: 2));
            await WriteNoteAsync(admin, sessionId, $"{tag} {deliveryType}");
        }

        var mine = (await AllAsync(admin, athleteUserId))
            .Where(x => x.GetProperty("content").GetString()!.StartsWith(tag, StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(3, mine.Length);

        Assert.Equal(
            ["FaceToFace", "Observation", "Online"],
            mine.Select(x => x.GetProperty("sessionDeliveryType").GetString()!).OrderBy(x => x));
    }

    // ------------------------------------------------------------ scoping

    [Fact]
    public async Task Only_the_requested_athletes_notes_are_returned()
    {
        var admin = await AdminClientAsync();
        var tag = Guid.NewGuid().ToString();

        var mineId = await AthleteUserIdAsync(admin, Athlete);
        var theirsId = await AthleteUserIdAsync(admin, OtherAthlete);

        var mineSession = await SeedSessionAsync(
            await ProfileIdAsync(Athlete), DeliveryType.Online, Future(days: 3));
        var theirsSession = await SeedSessionAsync(
            await ProfileIdAsync(OtherAthlete), DeliveryType.Online, Future(days: 3));

        await WriteNoteAsync(admin, mineSession, $"{tag} mine");
        await WriteNoteAsync(admin, theirsSession, $"{tag} theirs");

        var mine = await AllAsync(admin, mineId);

        Assert.Contains(mine, x => x.GetProperty("content").GetString() == $"{tag} mine");
        Assert.DoesNotContain(mine, x => x.GetProperty("content").GetString() == $"{tag} theirs");

        // And the other athlete's history is the mirror image, so this is scoping rather than
        // one list happening to be empty.
        var theirs = await AllAsync(admin, theirsId);

        Assert.Contains(theirs, x => x.GetProperty("content").GetString() == $"{tag} theirs");
        Assert.DoesNotContain(theirs, x => x.GetProperty("content").GetString() == $"{tag} mine");
    }

    /// <summary>
    /// An athlete belonging to somebody else is 404, exactly as an id that does not exist is -
    /// the coach comes from the token, and the API never confirms that an id it will not serve
    /// exists.
    /// </summary>
    [Fact]
    public async Task Another_coachs_athlete_is_not_reachable()
    {
        var admin = await AdminClientAsync();

        var response = await admin.GetAsync(
            $"/api/v1/athletes/{AthleteApiFactory.ForeignAthleteId}/notes");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertErrorAsync(response, "ATHLETE_NOT_FOUND");
    }

    [Fact]
    public async Task An_unknown_athlete_is_the_established_404()
    {
        var admin = await AdminClientAsync();

        var response = await admin.GetAsync($"/api/v1/athletes/{Guid.NewGuid()}/notes");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertErrorAsync(response, "ATHLETE_NOT_FOUND");
    }

    /// <summary>
    /// Session notes are the coach's, and this work must not be the thing that shows them to the
    /// athlete they are about.
    /// </summary>
    [Fact]
    public async Task An_athlete_cannot_read_a_session_notes_history()
    {
        var admin = await AdminClientAsync();
        var athlete = await AthleteClientAsync(Athlete);
        var athleteUserId = await AthleteUserIdAsync(admin, Athlete);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await athlete.GetAsync($"/api/v1/athletes/{athleteUserId}/notes")).StatusCode);
    }

    [Fact]
    public async Task An_athlete_with_no_notes_gets_an_empty_page_not_a_404()
    {
        var admin = await AdminClientAsync();
        var athleteUserId = await AthleteUserIdAsync(admin, AthleteWithNoNotes);

        var response = await admin.GetAsync($"/api/v1/athletes/{athleteUserId}/notes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Empty(body.GetProperty("items").EnumerateArray());
        Assert.Equal(0, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, body.GetProperty("totalPages").GetInt32());
        Assert.False(body.GetProperty("hasNextPage").GetBoolean());
    }

    // ----------------------------------------------------------- ordering

    [Fact]
    public async Task The_history_is_newest_first()
    {
        var admin = await AdminClientAsync();
        var athleteUserId = await AthleteUserIdAsync(admin, Athlete);
        var profileId = await ProfileIdAsync(Athlete);
        var tag = Guid.NewGuid().ToString();

        var sessionId = await SeedSessionAsync(profileId, DeliveryType.Online, Future(days: 4));

        // Written oldest to newest, so a history that echoed insertion order would fail this.
        foreach (var n in new[] { "first", "second", "third" })
            await WriteNoteAsync(admin, sessionId, $"{tag} {n}");

        var mine = (await AllAsync(admin, athleteUserId))
            .Where(x => x.GetProperty("content").GetString()!.StartsWith(tag, StringComparison.Ordinal))
            .Select(x => x.GetProperty("content").GetString()!)
            .ToArray();

        Assert.Equal([$"{tag} third", $"{tag} second", $"{tag} first"], mine);
    }

    /// <summary>
    /// Notes written in the same instant must still have a total order, or offset paging shows one
    /// of them twice and the other never. The timestamps are forced equal in the database, which
    /// is the only way to produce the collision reliably.
    /// </summary>
    [Fact]
    public async Task Ordering_is_deterministic_when_timestamps_match()
    {
        var admin = await AdminClientAsync();

        // An athlete of its own, because this test backdates its notes to a fixed instant and then
        // pages one at a time. Sharing an athlete with the other tests here would mean paging past
        // their notes to reach these, and the assertion is about order, not about how many rows
        // some other test happened to leave behind.
        var athleteUserId = await AthleteUserIdAsync(admin, TieBreakAthlete);
        var profileId = await ProfileIdAsync(TieBreakAthlete);
        var tag = Guid.NewGuid().ToString();

        var sessionId = await SeedSessionAsync(profileId, DeliveryType.FaceToFace, Future(days: 5));

        for (var i = 0; i < 4; i++)
            await WriteNoteAsync(admin, sessionId, $"{tag} {i}");

        var sameInstant = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlAsync(
                $"""
                 UPDATE "SessionNotes" SET "CreatedAtUtc" = {sameInstant}
                 WHERE "SessionId" = {sessionId}
                 """);
        }

        // Read the whole thing in one page, then the same rows one page at a time. If the
        // tie-break were missing the two would disagree - a row would repeat on one page and
        // never appear on another - which is exactly the bug this guards.
        var whole = (await AllAsync(admin, athleteUserId)).Where(Tagged(tag)).Select(Id).ToArray();
        var paged = (await AllAsync(admin, athleteUserId, pageSize: 1)).Where(Tagged(tag)).Select(Id).ToArray();

        Assert.Equal(4, whole.Length);
        Assert.Equal(whole, paged);
        Assert.Equal(paged.Length, paged.Distinct().Count());

        // Descending id, since every createdAtUtc is now identical.
        Assert.Equal(whole.OrderByDescending(x => x), whole);
    }

    // ----------------------------------------------------------- paging

    [Fact]
    public async Task The_page_envelope_is_the_usual_one()
    {
        var admin = await AdminClientAsync();
        var athleteUserId = await AthleteUserIdAsync(admin, Athlete);
        var profileId = await ProfileIdAsync(Athlete);

        var sessionId = await SeedSessionAsync(profileId, DeliveryType.Online, Future(days: 6));
        await WriteNoteAsync(admin, sessionId, $"Paging {Guid.NewGuid()}");

        var first = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/athletes/{athleteUserId}/notes?page=1&pageSize=1");

        var total = first.GetProperty("totalCount").GetInt32();

        Assert.True(total >= 1);
        Assert.Equal(1, first.GetProperty("page").GetInt32());
        Assert.Equal(1, first.GetProperty("pageSize").GetInt32());
        Assert.Equal(total, first.GetProperty("totalPages").GetInt32());
        Assert.False(first.GetProperty("hasPreviousPage").GetBoolean());
        Assert.Equal(total > 1, first.GetProperty("hasNextPage").GetBoolean());
        Assert.Single(first.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Paging_values_outside_the_range_are_clamped_rather_than_rejected()
    {
        var admin = await AdminClientAsync();
        var athleteUserId = await AthleteUserIdAsync(admin, Athlete);

        var body = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/athletes/{athleteUserId}/notes?page=0&pageSize=5000");

        Assert.Equal(1, body.GetProperty("page").GetInt32());
        Assert.Equal(100, body.GetProperty("pageSize").GetInt32());
    }

    // -------------------------------------------------- session metadata

    [Fact]
    public async Task Every_row_carries_its_sessions_details()
    {
        var admin = await AdminClientAsync();
        var athleteUserId = await AthleteUserIdAsync(admin, Athlete);
        var profileId = await ProfileIdAsync(Athlete);

        var start = Future(days: 7);
        var content = $"Metadata {Guid.NewGuid()}";
        var sessionId = await SeedSessionAsync(
            profileId, DeliveryType.FaceToFace, start, "Maadi Club", minutes: 45);

        var created = await WriteNoteAsync(admin, sessionId, content);
        var row = await FindAsync(admin, athleteUserId, content);

        // The note itself.
        Assert.Equal(created.GetProperty("id").GetGuid(), row.GetProperty("id").GetGuid());
        Assert.Equal(content, row.GetProperty("content").GetString());
        Assert.Equal(
            created.GetProperty("authorUserId").GetGuid(),
            row.GetProperty("authorUserId").GetGuid());

        // A tolerance, because these two values take different routes: the POST response carries
        // the in-memory DateTime with its full 100ns ticks, while the history reads the column
        // back from Postgres, which stores microseconds. They are the same instant to every
        // precision anyone cares about, and comparing them exactly fails on the last digit.
        Assert.Equal(
            created.GetProperty("createdAtUtc").GetDateTime(),
            row.GetProperty("createdAtUtc").GetDateTime(),
            TimeSpan.FromMilliseconds(1));

        // Its session, so the screen needs no second call.
        Assert.Equal(sessionId, row.GetProperty("sessionId").GetGuid());
        Assert.Equal(start, row.GetProperty("sessionStartUtc").GetDateTime());
        Assert.Equal(start.AddMinutes(45), row.GetProperty("sessionEndUtc").GetDateTime());
        Assert.Equal("FaceToFace", row.GetProperty("sessionDeliveryType").GetString());
        Assert.Equal("Scheduled", row.GetProperty("sessionStatus").GetString());
        Assert.Equal("Maadi Club", row.GetProperty("sessionLocationOrPlatform").GetString());

        // Deliberately absent: the flag would be sessionDeliveryType compared to one value, and
        // the join links belong to a live session rather than a history row.
        Assert.False(row.TryGetProperty("isObservation", out _));
        Assert.False(row.TryGetProperty("meetingUrl", out _));
        Assert.False(row.TryGetProperty("athleteName", out _));
    }

    /// <summary>
    /// Editing rewrites the text and moves updatedAtUtc while createdAtUtc stays put - so a note
    /// corrected today keeps its place in the history rather than jumping to the top.
    /// </summary>
    [Fact]
    public async Task An_edited_note_keeps_its_place_and_shows_its_new_text()
    {
        var admin = await AdminClientAsync();
        var athleteUserId = await AthleteUserIdAsync(admin, Athlete);
        var profileId = await ProfileIdAsync(Athlete);
        var tag = Guid.NewGuid().ToString();

        var sessionId = await SeedSessionAsync(profileId, DeliveryType.Online, Future(days: 8));

        var older = await WriteNoteAsync(admin, sessionId, $"{tag} older");
        await WriteNoteAsync(admin, sessionId, $"{tag} newer");

        var edit = await admin.PutAsJsonAsync(
            $"/api/v1/sessions/{sessionId}/notes/{older.GetProperty("id").GetGuid()}",
            new { title = "Corrected", content = $"{tag} older, corrected" });

        edit.EnsureSuccessStatusCode();

        var mine = (await AllAsync(admin, athleteUserId)).Where(Tagged(tag)).ToArray();

        // Still second, despite being the most recently touched.
        Assert.Equal($"{tag} newer", mine[0].GetProperty("content").GetString());
        Assert.Equal($"{tag} older, corrected", mine[1].GetProperty("content").GetString());

        var corrected = mine[1];
        Assert.Equal(
            older.GetProperty("createdAtUtc").GetDateTime(),
            corrected.GetProperty("createdAtUtc").GetDateTime(),
            TimeSpan.FromMilliseconds(1));
        Assert.True(
            corrected.GetProperty("updatedAtUtc").GetDateTime() >
            corrected.GetProperty("createdAtUtc").GetDateTime());
    }

    /// <summary>
    /// The history is a read over the notes the per-session endpoints own, so a deletion there
    /// removes it here too. There is no second copy that could survive.
    /// </summary>
    [Fact]
    public async Task A_deleted_note_leaves_the_history()
    {
        var admin = await AdminClientAsync();
        var athleteUserId = await AthleteUserIdAsync(admin, Athlete);
        var profileId = await ProfileIdAsync(Athlete);

        var content = $"Doomed {Guid.NewGuid()}";
        var sessionId = await SeedSessionAsync(profileId, DeliveryType.Online, Future(days: 9));
        var note = await WriteNoteAsync(admin, sessionId, content);

        Assert.Contains(
            await AllAsync(admin, athleteUserId),
            x => x.GetProperty("content").GetString() == content);

        var deleted = await admin.DeleteAsync(
            $"/api/v1/sessions/{sessionId}/notes/{note.GetProperty("id").GetGuid()}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        Assert.DoesNotContain(
            await AllAsync(admin, athleteUserId),
            x => x.GetProperty("content").GetString() == content);
    }

    // ------------------------------------- the per-session read is untouched

    /// <summary>
    /// The existing endpoint keeps its own behaviour: one session's notes, OLDEST first, which is
    /// the opposite order to the history and deliberately so - it reads as a running record of
    /// that session rather than a feed.
    /// </summary>
    [Fact]
    public async Task The_per_session_endpoint_still_returns_its_own_notes_oldest_first()
    {
        var admin = await AdminClientAsync();
        var profileId = await ProfileIdAsync(Athlete);
        var tag = Guid.NewGuid().ToString();

        var sessionId = await SeedSessionAsync(profileId, DeliveryType.Online, Future(days: 10));
        var other = await SeedSessionAsync(profileId, DeliveryType.Online, Future(days: 10));

        foreach (var n in new[] { "one", "two", "three" })
            await WriteNoteAsync(admin, sessionId, $"{tag} {n}");

        await WriteNoteAsync(admin, other, $"{tag} elsewhere");

        var notes = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/sessions/{sessionId}/notes");

        var contents = notes.EnumerateArray()
            .Select(x => x.GetProperty("content").GetString()!)
            .ToArray();

        Assert.Equal([$"{tag} one", $"{tag} two", $"{tag} three"], contents);
        Assert.DoesNotContain($"{tag} elsewhere", contents);
    }

    // -------------------------------------------------------------- title

    [Fact]
    public async Task The_title_round_trips_through_both_reads()
    {
        var admin = await AdminClientAsync();
        var athleteUserId = await AthleteUserIdAsync(admin, Athlete);
        var profileId = await ProfileIdAsync(Athlete);

        var content = $"Worked the start sequence {Guid.NewGuid()}";
        var sessionId = await SeedSessionAsync(profileId, DeliveryType.Online, Future(days: 11));

        var created = await WriteNoteAsync(admin, sessionId, content, title: "Race starts");

        Assert.Equal("Race starts", created.GetProperty("title").GetString());

        // The per-session read...
        var perSession = (await admin.GetFromJsonAsync<JsonElement>(
                $"/api/v1/sessions/{sessionId}/notes"))
            .EnumerateArray().Single();

        Assert.Equal("Race starts", perSession.GetProperty("title").GetString());

        // ...and the history read.
        Assert.Equal("Race starts",
            (await FindAsync(admin, athleteUserId, content)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task The_title_is_trimmed()
    {
        var admin = await AdminClientAsync();
        var profileId = await ProfileIdAsync(Athlete);
        var sessionId = await SeedSessionAsync(profileId, DeliveryType.Online, Future(days: 12));

        var created = await WriteNoteAsync(
            admin, sessionId, $"Trimmed {Guid.NewGuid()}", title: "   Spaced out   ");

        Assert.Equal("Spaced out", created.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task A_note_without_a_title_is_refused(string? title)
    {
        var admin = await AdminClientAsync();
        var profileId = await ProfileIdAsync(Athlete);
        var sessionId = await SeedSessionAsync(profileId, DeliveryType.Online, Future(days: 13));

        var response = await admin.PostAsJsonAsync(
            $"/api/v1/sessions/{sessionId}/notes", new { title, content = "Has content." });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_FAILED", body.GetProperty("errorCode").GetString());

        // The errors dictionary is keyed by the CLR property name, which is how every other
        // endpoint in this API already behaves - AuthEndpointTests asserts "Email" for the same
        // reason. FluentValidation's WithName changes the message text, not the key.
        Assert.Contains("Title", body.GetProperty("errors").EnumerateObject().Select(e => e.Name));
    }

    [Fact]
    public async Task A_title_over_two_hundred_characters_is_refused()
    {
        var admin = await AdminClientAsync();
        var profileId = await ProfileIdAsync(Athlete);
        var sessionId = await SeedSessionAsync(profileId, DeliveryType.Online, Future(days: 14));

        var response = await admin.PostAsJsonAsync(
            $"/api/v1/sessions/{sessionId}/notes",
            new { title = new string('x', 201), content = "Has content." });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // 200 exactly is fine - the bound is inclusive.
        var atTheLimit = await admin.PostAsJsonAsync(
            $"/api/v1/sessions/{sessionId}/notes",
            new { title = new string('x', 200), content = "Has content." });

        Assert.Equal(HttpStatusCode.Created, atTheLimit.StatusCode);
    }

    /// <summary>
    /// The edit replaces both fields, so changing only the content still requires a title and
    /// still rewrites it. There is no patch that leaves one behind.
    /// </summary>
    [Fact]
    public async Task Editing_replaces_the_title_as_well_as_the_content()
    {
        var admin = await AdminClientAsync();
        var athleteUserId = await AthleteUserIdAsync(admin, Athlete);
        var profileId = await ProfileIdAsync(Athlete);

        var sessionId = await SeedSessionAsync(profileId, DeliveryType.Online, Future(days: 15));
        var note = await WriteNoteAsync(
            admin, sessionId, $"Before {Guid.NewGuid()}", title: "First thoughts");

        var newContent = $"After {Guid.NewGuid()}";

        var edited = await admin.PutAsJsonAsync(
            $"/api/v1/sessions/{sessionId}/notes/{note.GetProperty("id").GetGuid()}",
            new { title = "Second thoughts", content = newContent });

        edited.EnsureSuccessStatusCode();

        var row = await FindAsync(admin, athleteUserId, newContent);
        Assert.Equal("Second thoughts", row.GetProperty("title").GetString());

        // And an edit that omits the title is refused rather than silently keeping the old one.
        var partial = await admin.PutAsJsonAsync(
            $"/api/v1/sessions/{sessionId}/notes/{note.GetProperty("id").GetGuid()}",
            new { content = "Only the content this time." });

        Assert.Equal(HttpStatusCode.BadRequest, partial.StatusCode);
    }

    // ------------------------------------------------- the athlete's own view

    /// <summary>
    /// Session notes are shared with the athlete as of 2026-09-21. The athlete reads their own
    /// history, and gets the same rows the Admin sees for them.
    /// </summary>
    [Fact]
    public async Task An_athlete_reads_their_own_notes()
    {
        var admin = await AdminClientAsync();
        var athlete = await AthleteClientAsync(Athlete);
        var athleteUserId = await AthleteUserIdAsync(admin, Athlete);
        var profileId = await ProfileIdAsync(Athlete);

        var content = $"Shared with the athlete {Guid.NewGuid()}";
        var sessionId = await SeedSessionAsync(profileId, DeliveryType.Observation, Future(days: 16));
        await WriteNoteAsync(admin, sessionId, content, title: "What I saw");

        var mine = await MineAsync(athlete);
        var row = Assert.Single(mine, x => x.GetProperty("content").GetString() == content);

        Assert.Equal("What I saw", row.GetProperty("title").GetString());
        Assert.Equal(sessionId, row.GetProperty("sessionId").GetGuid());
        Assert.Equal("Observation", row.GetProperty("sessionDeliveryType").GetString());

        // Field for field the same object the Admin gets for this athlete.
        var admins = await FindAsync(admin, athleteUserId, content);
        Assert.Equal(admins.GetRawText(), row.GetRawText());
    }

    /// <summary>
    /// Notes written before notes were shared are visible too - the decision was deliberately not
    /// given a cutoff. Simulated by backdating a note to well before the change.
    /// </summary>
    [Fact]
    public async Task Notes_written_before_the_sharing_decision_are_visible_too()
    {
        var admin = await AdminClientAsync();
        var athlete = await AthleteClientAsync(Athlete);
        var profileId = await ProfileIdAsync(Athlete);

        var content = $"Written long ago {Guid.NewGuid()}";
        var sessionId = await SeedSessionAsync(profileId, DeliveryType.Online, Future(days: 17));
        var note = await WriteNoteAsync(admin, sessionId, content, title: "An old note");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlAsync(
                $"""
                 UPDATE "SessionNotes" SET "CreatedAtUtc" = {new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc)}
                 WHERE "Id" = {note.GetProperty("id").GetGuid()}
                 """);
        }

        Assert.Contains(
            await MineAsync(athlete),
            x => x.GetProperty("content").GetString() == content);
    }

    [Fact]
    public async Task An_athlete_sees_only_their_own_notes()
    {
        var admin = await AdminClientAsync();
        var athlete = await AthleteClientAsync(Athlete);
        var tag = Guid.NewGuid().ToString();

        var mineSession = await SeedSessionAsync(
            await ProfileIdAsync(Athlete), DeliveryType.Online, Future(days: 18));
        var theirsSession = await SeedSessionAsync(
            await ProfileIdAsync(OtherAthlete), DeliveryType.Online, Future(days: 18));

        await WriteNoteAsync(admin, mineSession, $"{tag} mine");
        await WriteNoteAsync(admin, theirsSession, $"{tag} theirs");

        var mine = await MineAsync(athlete);

        Assert.Contains(mine, x => x.GetProperty("content").GetString() == $"{tag} mine");
        Assert.DoesNotContain(mine, x => x.GetProperty("content").GetString() == $"{tag} theirs");
    }

    /// <summary>
    /// Read-only. The athlete has no way to add, change or remove a note - there is no write
    /// route under /me/notes at all, and the per-session routes stay Admin-only.
    /// </summary>
    [Fact]
    public async Task An_athlete_cannot_write_notes()
    {
        var admin = await AdminClientAsync();
        var athlete = await AthleteClientAsync(Athlete);
        var profileId = await ProfileIdAsync(Athlete);

        var sessionId = await SeedSessionAsync(profileId, DeliveryType.Online, Future(days: 19));
        var note = await WriteNoteAsync(admin, sessionId, $"Not yours to edit {Guid.NewGuid()}");
        var noteId = note.GetProperty("id").GetGuid();

        var body = new { title = "Mine now", content = "I changed this." };

        // There is no write route under /me/notes - 404 or 405, never a write.
        var invented = await athlete.PostAsJsonAsync("/api/v1/me/notes", body);
        Assert.NotEqual(HttpStatusCode.Created, invented.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, invented.StatusCode);

        // And the real write routes refuse them.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await athlete.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/notes", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await athlete.PutAsJsonAsync($"/api/v1/sessions/{sessionId}/notes/{noteId}", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await athlete.DeleteAsync($"/api/v1/sessions/{sessionId}/notes/{noteId}")).StatusCode);

        // The note is untouched.
        var after = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/sessions/{sessionId}/notes");
        Assert.Equal(
            note.GetProperty("content").GetString(),
            after.EnumerateArray().Single().GetProperty("content").GetString());
    }

    [Fact]
    public async Task An_admin_cannot_use_the_athletes_own_route()
    {
        var admin = await AdminClientAsync();

        Assert.Equal(HttpStatusCode.Forbidden,
            (await admin.GetAsync("/api/v1/me/notes")).StatusCode);
    }

    [Fact]
    public async Task The_athletes_own_history_pages_like_the_admins()
    {
        var admin = await AdminClientAsync();
        var athlete = await AthleteClientAsync(Athlete);

        var body = await athlete.GetFromJsonAsync<JsonElement>(
            "/api/v1/me/notes?page=0&pageSize=5000");

        Assert.Equal(1, body.GetProperty("page").GetInt32());
        Assert.Equal(100, body.GetProperty("pageSize").GetInt32());
        Assert.True(body.GetProperty("totalCount").GetInt32() >= 0);

        // The same envelope the Admin's view uses.
        foreach (var field in new[]
                 { "items", "page", "pageSize", "totalCount", "totalPages", "hasNextPage", "hasPreviousPage" })
        {
            Assert.True(body.TryGetProperty(field, out _), $"missing {field}");
        }
    }

    // ----------------------------------------------------------- helpers

    /// <summary>Every page of the calling athlete's own history.</summary>
    private static async Task<JsonElement[]> MineAsync(HttpClient athlete)
    {
        var all = new List<JsonElement>();

        for (var page = 1; ; page++)
        {
            var body = await athlete.GetFromJsonAsync<JsonElement>(
                $"/api/v1/me/notes?page={page}&pageSize=100");

            all.AddRange(body.GetProperty("items").EnumerateArray());

            if (!body.GetProperty("hasNextPage").GetBoolean()) return [.. all];
        }
    }


    private static Func<JsonElement, bool> Tagged(string tag) =>
        x => x.GetProperty("content").GetString()!.StartsWith(tag, StringComparison.Ordinal);

    private static Guid Id(JsonElement note) => note.GetProperty("id").GetGuid();

    private static DateTime Future(int days) =>
        new DateTime(DateTime.UtcNow.Ticks, DateTimeKind.Utc).Date.AddDays(days).AddHours(9);

    /// <summary>
    /// Every page of this athlete's history, flattened, newest first. Pages until hasNextPage is
    /// false rather than until a short page, which is what the contract tells the app to do.
    /// </summary>
    private static async Task<JsonElement[]> AllAsync(
        HttpClient admin, Guid athleteUserId, int pageSize = 100)
    {
        var all = new List<JsonElement>();

        for (var page = 1; ; page++)
        {
            var body = await admin.GetFromJsonAsync<JsonElement>(
                $"/api/v1/athletes/{athleteUserId}/notes?page={page}&pageSize={pageSize}");

            all.AddRange(body.GetProperty("items").EnumerateArray());

            if (!body.GetProperty("hasNextPage").GetBoolean()) return [.. all];
        }
    }

    private static async Task<JsonElement> FindAsync(
        HttpClient admin, Guid athleteUserId, string content)
    {
        var match = (await AllAsync(admin, athleteUserId))
            .Where(x => x.GetProperty("content").GetString() == content)
            .ToArray();

        return Assert.Single(match);
    }

    private static async Task<JsonElement> WriteNoteAsync(
        HttpClient admin, Guid sessionId, string content, string? title = null)
    {
        var response = await admin.PostAsJsonAsync(
            $"/api/v1/sessions/{sessionId}/notes",
            new { title = title ?? $"Title for {content}", content });

        if (response.StatusCode != HttpStatusCode.Created)
            Assert.Fail($"note {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>
    /// Writes a session straight to the database. Only an observation can be created through the
    /// API - everything else is projected from Calendly - so a test that needs an Online or
    /// FaceToFace session builds one the way the projection would.
    /// </summary>
    private async Task<Guid> SeedSessionAsync(
        Guid athleteProfileId, DeliveryType deliveryType, DateTime startUtc,
        string? location = null, int minutes = 60)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var coachId = await db.AthleteProfiles.AsNoTracking()
            .Where(x => x.Id == athleteProfileId).Select(x => x.CoachId).SingleAsync();

        Session session;

        if (deliveryType == DeliveryType.Observation)
        {
            session = Session.CreateObservation(
                coachId, athleteProfileId, startUtc, startUtc.AddMinutes(minutes),
                location, deductsSession: false, clock.UtcNow);
        }
        else
        {
            // A unique Calendly uri per row: the columns are uniquely indexed, and these tests
            // seed many sessions across one run.
            var unique = Guid.NewGuid();

            session = Session.Create(coachId, athleteProfileId, new CalendlySessionData(
                    $"https://api.calendly.com/scheduled_events/{unique}",
                    $"https://api.calendly.com/scheduled_events/{unique}/invitees/{unique}",
                    "https://api.calendly.com/event_types/test",
                    startUtc, startUtc.AddMinutes(minutes), deliveryType, location,
                    MeetingUrl: null, CancelUrl: null, RescheduleUrl: null),
                clock.UtcNow);
        }

        db.Sessions.Add(session);
        await db.SaveChangesAsync();

        return session.Id;
    }

    private async Task<Guid> ProfileIdAsync(string email)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await (from user in db.Users
                      join profile in db.AthleteProfiles on user.Id equals profile.UserId
                      where user.Email == email
                      select profile.Id).SingleAsync();
    }

    private static async Task<Guid> AthleteUserIdAsync(HttpClient admin, string email)
    {
        var page = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/athletes?search={Uri.EscapeDataString(email)}");

        return page.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
    }

    private async Task<HttpClient> AdminClientAsync() =>
        await ClientAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);

    private async Task<HttpClient> AthleteClientAsync(string email) =>
        await ClientAsync(email, AthleteApiFactory.AthletePassword);

    private async Task<HttpClient> ClientAsync(string email, string password)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });

        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthPayload>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, string errorCode) =>
        Assert.Equal(errorCode, (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("errorCode").GetString());
}
