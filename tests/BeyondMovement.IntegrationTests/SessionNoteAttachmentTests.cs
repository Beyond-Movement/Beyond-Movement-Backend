using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeyondMovement.Api.Scheduling;
using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Scheduling.Domain;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeyondMovement.IntegrationTests;

/// <summary>Two athletes of the seeded Admin, and one belonging to another coach.</summary>
public sealed class SessionNoteAttachmentApiFactory : ApiFactory
{
    public const string Athlete = "casey@attach.test";
    public const string OtherAthlete = "drew@attach.test";
    public const string ForeignAthlete = "foreign@attach.test";

    protected override async Task InitializeCoreAsync()
    {
        await base.InitializeCoreAsync();

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await AthleteApiFactory.AddAthleteAsync(db, scope.ServiceProvider, Athlete, "Casey Attach",
            "Tennis", new DateOnly(2001, 1, 1));
        await AthleteApiFactory.AddAthleteAsync(db, scope.ServiceProvider, OtherAthlete, "Drew Attach",
            "Rowing", new DateOnly(2002, 2, 2));
        await AthleteApiFactory.AddAthleteAsync(db, scope.ServiceProvider, ForeignAthlete, "Foreign Attach",
            "Golf", new DateOnly(2003, 3, 3), coachId: Guid.NewGuid());
    }
}

/// <summary>
/// Images on session notes: the two-phase upload, the verification that stands between an upload
/// and a visible image, who may see and change them, and the cleanup that keeps storage from
/// accumulating objects nobody can reach.
/// <para>
/// Storage is <see cref="FakeObjectStorage"/>. Everything else — the endpoints, the database, the
/// migrations and the constraints — is real.
/// </para>
/// </summary>
public sealed class SessionNoteAttachmentTests(SessionNoteAttachmentApiFactory factory)
    : IClassFixture<SessionNoteAttachmentApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record AuthPayload(string AccessToken, string RefreshToken);

    private const string Jpeg = "image/jpeg";
    private const string Png = "image/png";
    private const string Webp = "image/webp";
    private const long TenMb = 10 * 1024 * 1024;

    private static readonly byte[] JpegHeader = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];
    private static readonly byte[] WebpHeader = [.. "RIFF"u8, 0x24, 0x00, 0x00, 0x00, .. "WEBP"u8];

    private static byte[] Image(string contentType, int size = 64)
    {
        var header = contentType switch { Jpeg => JpegHeader, Png => PngHeader, _ => WebpHeader };
        var bytes = new byte[size];
        header.CopyTo(bytes, 0);
        return bytes;
    }

    // ================================================================ happy path

    [Theory]
    [InlineData(Jpeg, "jpg")]
    [InlineData(Png, "png")]
    [InlineData(Webp, "webp")]
    public async Task An_uploaded_and_completed_image_is_committed(string contentType, string extension)
    {
        var admin = await AdminClientAsync();
        var (sessionId, noteId) = await NoteAsync(admin, SessionNoteAttachmentApiFactory.Athlete);
        var bytes = Image(contentType, 500);

        var upload = await RequestUploadAsync(admin, sessionId, noteId, contentType, bytes.Length);
        var attachmentId = upload.GetProperty("attachmentId").GetGuid();

        Assert.Equal("PUT", upload.GetProperty("uploadMethod").GetString());
        Assert.Equal(contentType, upload.GetProperty("requiredHeaders").GetProperty("Content-Type").GetString());
        Assert.Equal(TenMb, upload.GetProperty("maxSizeBytes").GetInt64());

        var key = factory.Storage.SimulatePut(upload.GetProperty("uploadUrl").GetString()!, bytes, contentType);

        // Server-generated key: prefix, note id, attachment id, extension from the type. Nothing else.
        Assert.Equal($"session-notes/{noteId}/{attachmentId}.{extension}", key);

        var response = await CompleteAsync(admin, sessionId, noteId, attachmentId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var committed = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(attachmentId, committed.GetProperty("id").GetGuid());
        Assert.Equal(noteId, committed.GetProperty("sessionNoteId").GetGuid());
        Assert.Equal(contentType, committed.GetProperty("contentType").GetString());
        Assert.Equal(bytes.Length, committed.GetProperty("sizeBytes").GetInt64());
        Assert.StartsWith(FakeObjectStorage.Host, committed.GetProperty("downloadUrl").GetString());
        Assert.Contains("verb=GET", committed.GetProperty("downloadUrl").GetString());
        Assert.True(committed.TryGetProperty("downloadUrlExpiresAtUtc", out _));
        Assert.False(committed.TryGetProperty("storageKey", out _));

        await using var scope = await DbAsync();
        var row = await scope.Db.SessionNoteAttachments.AsNoTracking().SingleAsync(x => x.Id == attachmentId);
        Assert.Equal(SessionNoteAttachmentStatus.Committed, row.Status);
        Assert.Equal(bytes.Length, row.SizeBytes);
        Assert.NotNull(row.CommittedAtUtc);
    }

    [Fact]
    public async Task Url_lifetimes_are_five_minutes_to_upload_and_fifteen_to_download()
    {
        var admin = await AdminClientAsync();
        var (sessionId, noteId) = await NoteAsync(admin, SessionNoteAttachmentApiFactory.Athlete);

        var before = DateTime.UtcNow;
        var upload = await RequestUploadAsync(admin, sessionId, noteId, Jpeg, 64);
        Assert.Equal(TimeSpan.FromMinutes(5), factory.Storage.LastUploadLifetime);

        var expires = upload.GetProperty("uploadUrlExpiresAtUtc").GetDateTime().ToUniversalTime();
        Assert.InRange(expires, before.AddMinutes(4.9), DateTime.UtcNow.AddMinutes(5.1));

        await UploadAndCompleteAsync(admin, sessionId, noteId, upload, Jpeg, Image(Jpeg));
        Assert.Equal(TimeSpan.FromMinutes(15), factory.Storage.LastDownloadLifetime);
    }

    [Fact]
    public async Task Completion_is_idempotent()
    {
        var admin = await AdminClientAsync();
        var (sessionId, noteId) = await NoteAsync(admin, SessionNoteAttachmentApiFactory.Athlete);
        var id = await AttachAsync(admin, sessionId, noteId, Png);

        var again = await CompleteAsync(admin, sessionId, noteId, id);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(id, (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());

        await using var scope = await DbAsync();
        Assert.Equal(1, await scope.Db.SessionNoteAttachments.CountAsync(x => x.SessionNoteId == noteId));
    }

    [Fact]
    public async Task Committed_images_appear_on_every_note_read_in_order()
    {
        var admin = await AdminClientAsync();
        var athlete = await AthleteClientAsync(SessionNoteAttachmentApiFactory.Athlete);
        var athleteUserId = await AthleteUserIdAsync(admin, SessionNoteAttachmentApiFactory.Athlete);
        var (sessionId, noteId) = await NoteAsync(admin, SessionNoteAttachmentApiFactory.Athlete);

        var first = await AttachAsync(admin, sessionId, noteId, Jpeg);
        var second = await AttachAsync(admin, sessionId, noteId, Webp);

        // Session Details list.
        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/sessions/{sessionId}/notes");
        AssertImages(list.EnumerateArray().Single(x => Id(x) == noteId), first, second);

        // The Admin's Session Notes History.
        var history = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/athletes/{athleteUserId}/notes?pageSize=100");
        AssertImages(history.GetProperty("items").EnumerateArray().Single(x => Id(x) == noteId), first, second);

        // The athlete's own history.
        var mine = await athlete.GetFromJsonAsync<JsonElement>("/api/v1/me/notes?pageSize=100");
        AssertImages(mine.GetProperty("items").EnumerateArray().Single(x => Id(x) == noteId), first, second);

        static void AssertImages(JsonElement note, Guid first, Guid second)
        {
            var images = note.GetProperty("attachments").EnumerateArray().ToArray();
            Assert.Equal([first, second], images.Select(Id));
            Assert.All(images, x => Assert.False(string.IsNullOrEmpty(x.GetProperty("downloadUrl").GetString())));
            Assert.True(images[0].GetProperty("sortOrder").GetInt32() < images[1].GetProperty("sortOrder").GetInt32());
        }
    }

    [Fact]
    public async Task A_pending_upload_is_not_visible_anywhere()
    {
        var admin = await AdminClientAsync();
        var athlete = await AthleteClientAsync(SessionNoteAttachmentApiFactory.Athlete);
        var (sessionId, noteId) = await NoteAsync(admin, SessionNoteAttachmentApiFactory.Athlete);

        var upload = await RequestUploadAsync(admin, sessionId, noteId, Jpeg, 64);
        var id = upload.GetProperty("attachmentId").GetGuid();
        factory.Storage.SimulatePut(upload.GetProperty("uploadUrl").GetString()!, Image(Jpeg), Jpeg);

        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/sessions/{sessionId}/notes");
        Assert.Empty(list.EnumerateArray().Single(x => Id(x) == noteId).GetProperty("attachments").EnumerateArray());

        var mine = await athlete.GetFromJsonAsync<JsonElement>("/api/v1/me/notes?pageSize=100");
        Assert.Empty(mine.GetProperty("items").EnumerateArray().Single(x => Id(x) == noteId)
            .GetProperty("attachments").EnumerateArray());

        var refresh = await admin.GetAsync(
            $"/api/v1/sessions/{sessionId}/notes/{noteId}/attachments/{id}/download-url");
        Assert.Equal(HttpStatusCode.NotFound, refresh.StatusCode);
        await AssertErrorAsync(refresh, "ATTACHMENT_NOT_FOUND");
    }

    // ============================================================ request checks

    [Theory]
    [InlineData("image/heic")]
    [InlineData("image/gif")]
    [InlineData("application/pdf")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Unsupported_types_are_refused(string? contentType)
    {
        var admin = await AdminClientAsync();
        var (sessionId, noteId) = await NoteAsync(admin, SessionNoteAttachmentApiFactory.Athlete);

        var response = await admin.PostAsJsonAsync(
            $"/api/v1/sessions/{sessionId}/notes/{noteId}/attachments", new { contentType, sizeBytes = 100 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertErrorAsync(response, "UNSUPPORTED_ATTACHMENT_TYPE");
        await AssertNoRowsAsync(noteId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(TenMb + 1)]
    public async Task Sizes_outside_one_byte_to_ten_megabytes_are_refused(long sizeBytes)
    {
        var admin = await AdminClientAsync();
        var (sessionId, noteId) = await NoteAsync(admin, SessionNoteAttachmentApiFactory.Athlete);

        var response = await admin.PostAsJsonAsync(
            $"/api/v1/sessions/{sessionId}/notes/{noteId}/attachments", new { contentType = Jpeg, sizeBytes });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertErrorAsync(response, "ATTACHMENT_TOO_LARGE");
        await AssertNoRowsAsync(noteId);
    }

    [Fact]
    public async Task Exactly_ten_megabytes_is_accepted()
    {
        var admin = await AdminClientAsync();
        var (sessionId, noteId) = await NoteAsync(admin, SessionNoteAttachmentApiFactory.Athlete);

        var upload = await RequestUploadAsync(admin, sessionId, noteId, Jpeg, TenMb);
        var response = await UploadAndCompleteRawAsync(
            admin, sessionId, noteId, upload, Jpeg, Image(Jpeg, (int)TenMb));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_note_holds_at_most_five_images_counting_pending_uploads()
    {
        var admin = await AdminClientAsync();
        var (sessionId, noteId) = await NoteAsync(admin, SessionNoteAttachmentApiFactory.Athlete);

        var committed = await AttachAsync(admin, sessionId, noteId, Jpeg);
        for (var i = 0; i < 4; i++) await RequestUploadAsync(admin, sessionId, noteId, Png, 64);

        var sixth = await admin.PostAsJsonAsync(
            $"/api/v1/sessions/{sessionId}/notes/{noteId}/attachments", new { contentType = Jpeg, sizeBytes = 64 });

        Assert.Equal(HttpStatusCode.Conflict, sixth.StatusCode);
        await AssertErrorAsync(sixth, "ATTACHMENT_LIMIT_REACHED");

        // Deleting one frees its slot.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync(
            $"/api/v1/sessions/{sessionId}/notes/{noteId}/attachments/{committed}")).StatusCode);

        await RequestUploadAsync(admin, sessionId, noteId, Jpeg, 64);
    }

    [Fact]
    public async Task Parallel_requests_cannot_exceed_the_limit()
    {
        var admin = await AdminClientAsync();
        var (sessionId, noteId) = await NoteAsync(admin, SessionNoteAttachmentApiFactory.Athlete);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => admin.PostAsJsonAsync(
            $"/api/v1/sessions/{sessionId}/notes/{noteId}/attachments", new { contentType = Jpeg, sizeBytes = 64 })));

        Assert.Equal(5, responses.Count(x => x.StatusCode == HttpStatusCode.Created));
        Assert.Equal(3, responses.Count(x => x.StatusCode == HttpStatusCode.Conflict));

        await using var scope = await DbAsync();
        Assert.Equal(5, await scope.Db.SessionNoteAttachments.CountAsync(x => x.SessionNoteId == noteId));
    }

    [Fact]
    public async Task An_abandoned_upload_stops_counting_toward_the_limit()
    {
        var admin = await AdminClientAsync();
        var (sessionId, noteId) = await NoteAsync(admin, SessionNoteAttachmentApiFactory.Athlete);

        for (var i = 0; i < 5; i++) await RequestUploadAsync(admin, sessionId, noteId, Jpeg, 64);
        await ExpireUploadsAsync(noteId);

        await RequestUploadAsync(admin, sessionId, noteId, Jpeg, 64);
    }

    [Fact]
    public async Task Storage_being_down_is_503_and_takes_no_slot()
    {
        var admin = await AdminClientAsync();
        var (sessionId, noteId) = await NoteAsync(admin, SessionNoteAttachmentApiFactory.Athlete);

        factory.Storage.Unavailable = true;
        try
        {
            var response = await admin.PostAsJsonAsync(
                $"/api/v1/sessions/{sessionId}/notes/{noteId}/attachments", new { contentType = Jpeg, sizeBytes = 64 });

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            await AssertErrorAsync(response, "STORAGE_UNAVAILABLE");
        }
        finally
        {
            factory.Storage.Unavailable = false;
        }

        await AssertNoRowsAsync(noteId);
    }

    // ======================================================= upload verification

    [Fact]
    public async Task Completing_before_anything_was_uploaded_leaves_the_upload_pending()
    {
        var admin = await AdminClientAsync();
        var (sessionId, noteId) = await NoteAsync(admin, SessionNoteAttachmentApiFactory.Athlete);
        var upload = await RequestUploadAsync(admin, sessionId, noteId, Jpeg, 64);
        var id = upload.GetProperty("attachmentId").GetGuid();

        var early = await CompleteAsync(admin, sessionId, noteId, id);
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        await AssertErrorAsync(early, "ATTACHMENT_UPLOAD_INVALID");

        // The client retries the PUT and completes.
        var late = await UploadAndCompleteRawAsync(admin, sessionId, noteId, upload, Jpeg, Image(Jpeg));
        Assert.Equal(HttpStatusCode.OK, late.StatusCode);
    }

    private static byte[] Padded(ReadOnlySpan<byte> start)
    {
        var bytes = new byte[64];
        start.CopyTo(bytes);
        return bytes;
    }

    /// <summary>Each case is exactly the declared 64 bytes unless size is the point.</summary>
    public static TheoryData<string, string, byte[], long> BadUploads => new()
    {
        // Declared JPEG, but the bytes are a PNG: the declared type alone is not trusted.
        { "wrong signature", Jpeg, Image(Png), 64 },
        // Declared PNG, bytes are plain text.
        { "not an image", Png, Padded("hello, this is not an image"u8), 64 },
        // Declared WebP, but a RIFF container of another kind (a WAV file).
        { "other RIFF", Webp, Padded([.. "RIFF"u8, 0, 0, 0, 0, .. "WAVE"u8]), 64 },
        // Declared JPEG, all zeros.
        { "zeros", Jpeg, new byte[64], 64 },
        // The right format, but not the declared size.
        { "size mismatch", Jpeg, Image(Jpeg, 65), 64 },
    };

    [Theory]
    [MemberData(nameof(BadUploads))]
    public async Task An_invalid_upload_is_discarded_with_its_object(
        string why, string contentType, byte[] bytes, long declaredSize)
    {
        _ = why;
        var admin = await AdminClientAsync();
        var (sessionId, noteId) = await NoteAsync(admin, SessionNoteAttachmentApiFactory.Athlete);

        var upload = await RequestUploadAsync(admin, sessionId, noteId, contentType, declaredSize);
        var id = upload.GetProperty("attachmentId").GetGuid();
        var key = factory.Storage.SimulatePut(upload.GetProperty("uploadUrl").GetString()!, bytes, contentType);

        var response = await CompleteAsync(admin, sessionId, noteId, id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertErrorAsync(response, "ATTACHMENT_UPLOAD_INVALID");
        Assert.False(factory.Storage.Exists(key));
        await AssertNoRowsAsync(noteId);

        // Gone for good: a retry is not a way to get it accepted.
        Assert.Equal(HttpStatusCode.NotFound, (await CompleteAsync(admin, sessionId, noteId, id)).StatusCode);
    }

    [Fact]
    public async Task A_stored_content_type_that_differs_from_the_declared_one_is_refused()
    {
        var admin = await AdminClientAsync();
        var (sessionId, noteId) = await NoteAsync(admin, SessionNoteAttachmentApiFactory.Athlete);

        var upload = await RequestUploadAsync(admin, sessionId, noteId, Jpeg, 64);
        var key = factory.Storage.SimulatePut(upload.GetProperty("uploadUrl").GetString()!, Image(Jpeg), "image/png");

        var response = await CompleteAsync(admin, sessionId, noteId, upload.GetProperty("attachmentId").GetGuid());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertErrorAsync(response, "ATTACHMENT_UPLOAD_INVALID");
        Assert.False(factory.Storage.Exists(key));
    }

    [Fact]
    public async Task An_upload_completed_after_its_window_closed_is_refused_and_discarded()
    {
        var admin = await AdminClientAsync();
        var (sessionId, noteId) = await NoteAsync(admin, SessionNoteAttachmentApiFactory.Athlete);

        var upload = await RequestUploadAsync(admin, sessionId, noteId, Jpeg, 64);
        var key = factory.Storage.SimulatePut(upload.GetProperty("uploadUrl").GetString()!, Image(Jpeg), Jpeg);
        await ExpireUploadsAsync(noteId);

        var response = await CompleteAsync(admin, sessionId, noteId, upload.GetProperty("attachmentId").GetGuid());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertErrorAsync(response, "ATTACHMENT_UPLOAD_INVALID");
        Assert.False(factory.Storage.Exists(key));
        await AssertNoRowsAsync(noteId);
    }

    // ================================================================= delete

    [Fact]
    public async Task Deleting_an_image_removes_it_and_its_object()
    {
        var admin = await AdminClientAsync();
        var (sessionId, noteId) = await NoteAsync(admin, SessionNoteAttachmentApiFactory.Athlete);
        var id = await AttachAsync(admin, sessionId, noteId, Jpeg);
        var key = await KeyAsync(id);

        var response = await admin.DeleteAsync($"/api/v1/sessions/{sessionId}/notes/{noteId}/attachments/{id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(factory.Storage.Exists(key));
        await AssertNoRowsAsync(noteId);

        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/sessions/{sessionId}/notes");
        Assert.Empty(list.EnumerateArray().Single(x => Id(x) == noteId).GetProperty("attachments").EnumerateArray());

        var again = await admin.DeleteAsync($"/api/v1/sessions/{sessionId}/notes/{noteId}/attachments/{id}");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        await AssertErrorAsync(again, "ATTACHMENT_NOT_FOUND");
    }

    /// <summary>
    /// The storage key is the only record of where the object is. If the object cannot be deleted
    /// now, the row must survive — hidden — until the cleanup job manages it.
    /// </summary>
    [Fact]
    public async Task A_failed_storage_delete_keeps_the_row_until_cleanup_succeeds()
    {
        var admin = await AdminClientAsync();
        var (sessionId, noteId) = await NoteAsync(admin, SessionNoteAttachmentApiFactory.Athlete);
        var id = await AttachAsync(admin, sessionId, noteId, Jpeg);
        var key = await KeyAsync(id);

        factory.Storage.FailDeletes = true;
        try
        {
            var response = await admin.DeleteAsync($"/api/v1/sessions/{sessionId}/notes/{noteId}/attachments/{id}");
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

            Assert.True(factory.Storage.Exists(key));
            Assert.Equal(SessionNoteAttachmentStatus.Deleting, (await RowAsync(id))!.Status);

            // Hidden from every read even though it still exists.
            var list = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/sessions/{sessionId}/notes");
            Assert.Empty(list.EnumerateArray().Single(x => Id(x) == noteId).GetProperty("attachments").EnumerateArray());

            // A cleanup run while storage is still failing changes nothing.
            await RunCleanupAsync();
            Assert.NotNull(await RowAsync(id));
        }
        finally
        {
            factory.Storage.FailDeletes = false;
        }

        await RunCleanupAsync();

        Assert.False(factory.Storage.Exists(key));
        Assert.Null(await RowAsync(id));
    }

    [Fact]
    public async Task Deleting_a_note_deletes_its_images()
    {
        var admin = await AdminClientAsync();
        var (sessionId, noteId) = await NoteAsync(admin, SessionNoteAttachmentApiFactory.Athlete);
        var committed = await AttachAsync(admin, sessionId, noteId, Jpeg);
        var pending = await RequestUploadAsync(admin, sessionId, noteId, Png, 64);
        var pendingKey = factory.Storage.SimulatePut(pending.GetProperty("uploadUrl").GetString()!, Image(Png), Png);
        var committedKey = await KeyAsync(committed);

        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.DeleteAsync($"/api/v1/sessions/{sessionId}/notes/{noteId}")).StatusCode);

        Assert.False(factory.Storage.Exists(committedKey));
        Assert.False(factory.Storage.Exists(pendingKey));

        await using var scope = await DbAsync();
        Assert.False(await scope.Db.SessionNoteAttachments.AnyAsync(
            x => x.StorageKey == committedKey || x.StorageKey == pendingKey));
    }

    [Fact]
    public async Task Deleting_a_note_while_storage_is_down_leaves_orphans_for_cleanup()
    {
        var admin = await AdminClientAsync();
        var (sessionId, noteId) = await NoteAsync(admin, SessionNoteAttachmentApiFactory.Athlete);
        var id = await AttachAsync(admin, sessionId, noteId, Jpeg);
        var key = await KeyAsync(id);

        factory.Storage.FailDeletes = true;
        try
        {
            Assert.Equal(HttpStatusCode.NoContent,
                (await admin.DeleteAsync($"/api/v1/sessions/{sessionId}/notes/{noteId}")).StatusCode);

            var row = await RowAsync(id);
            Assert.NotNull(row);
            Assert.Null(row.SessionNoteId);
            Assert.Equal(key, row.StorageKey);
        }
        finally
        {
            factory.Storage.FailDeletes = false;
        }

        await RunCleanupAsync();

        Assert.False(factory.Storage.Exists(key));
        Assert.Null(await RowAsync(id));
    }

    /// <summary>
    /// A note removed below the application — a session delete cascading to its notes, or a
    /// manual fix in the database — must not take the storage key with it. The foreign key is
    /// SET NULL for exactly this.
    /// </summary>
    [Fact]
    public async Task A_note_removed_at_the_database_level_orphans_its_images_for_cleanup()
    {
        var admin = await AdminClientAsync();
        var (sessionId, noteId) = await NoteAsync(admin, SessionNoteAttachmentApiFactory.Athlete);
        var id = await AttachAsync(admin, sessionId, noteId, Jpeg);
        var key = await KeyAsync(id);

        await using (var scope = await DbAsync())
            await scope.Db.Database.ExecuteSqlAsync($"""delete from "SessionNotes" where "Id" = {noteId}""");

        var orphan = await RowAsync(id);
        Assert.NotNull(orphan);
        Assert.Null(orphan.SessionNoteId);
        Assert.True(factory.Storage.Exists(key));

        await RunCleanupAsync();

        Assert.False(factory.Storage.Exists(key));
        Assert.Null(await RowAsync(id));
    }

    [Fact]
    public async Task Cleanup_removes_abandoned_uploads_and_leaves_live_ones()
    {
        var admin = await AdminClientAsync();
        var (sessionId, noteId) = await NoteAsync(admin, SessionNoteAttachmentApiFactory.Athlete);

        var abandoned = await RequestUploadAsync(admin, sessionId, noteId, Jpeg, 64);
        var abandonedKey = factory.Storage.SimulatePut(abandoned.GetProperty("uploadUrl").GetString()!, Image(Jpeg), Jpeg);
        await ExpireUploadsAsync(noteId);

        var fresh = await RequestUploadAsync(admin, sessionId, noteId, Jpeg, 64);
        var committed = await AttachAsync(admin, sessionId, noteId, Png);

        await RunCleanupAsync();

        Assert.False(factory.Storage.Exists(abandonedKey));
        Assert.Null(await RowAsync(abandoned.GetProperty("attachmentId").GetGuid()));
        Assert.Equal(SessionNoteAttachmentStatus.Pending, (await RowAsync(fresh.GetProperty("attachmentId").GetGuid()))!.Status);
        Assert.Equal(SessionNoteAttachmentStatus.Committed, (await RowAsync(committed))!.Status);
        Assert.True(factory.Storage.Exists(await KeyAsync(committed)));
    }

    // ========================================================== authorisation

    [Fact]
    public async Task An_athlete_cannot_upload_complete_or_delete()
    {
        var admin = await AdminClientAsync();
        var athlete = await AthleteClientAsync(SessionNoteAttachmentApiFactory.Athlete);
        var (sessionId, noteId) = await NoteAsync(admin, SessionNoteAttachmentApiFactory.Athlete);
        var id = await AttachAsync(admin, sessionId, noteId, Jpeg);
        var baseUrl = $"/api/v1/sessions/{sessionId}/notes/{noteId}/attachments";

        Assert.Equal(HttpStatusCode.Forbidden,
            (await athlete.PostAsJsonAsync(baseUrl, new { contentType = Jpeg, sizeBytes = 64 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await athlete.PostAsync($"{baseUrl}/{id}/complete", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await athlete.DeleteAsync($"{baseUrl}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await athlete.GetAsync($"{baseUrl}/{id}/download-url")).StatusCode);

        // And there is no write route under /me.
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await athlete.PostAsJsonAsync(
            $"/api/v1/me/notes/{noteId}/attachments/{id}/download-url", new { })).StatusCode);

        Assert.Equal(SessionNoteAttachmentStatus.Committed, (await RowAsync(id))!.Status);
    }

    [Fact]
    public async Task Anonymous_callers_are_refused()
    {
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(
            $"/api/v1/sessions/{Guid.NewGuid()}/notes/{Guid.NewGuid()}/attachments",
            new { contentType = Jpeg, sizeBytes = 64 })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(
            $"/api/v1/me/notes/{Guid.NewGuid()}/attachments/{Guid.NewGuid()}/download-url")).StatusCode);
    }

    [Fact]
    public async Task An_athlete_can_refresh_the_download_url_of_an_image_on_their_own_note()
    {
        var admin = await AdminClientAsync();
        var athlete = await AthleteClientAsync(SessionNoteAttachmentApiFactory.Athlete);
        var (sessionId, noteId) = await NoteAsync(admin, SessionNoteAttachmentApiFactory.Athlete);
        var id = await AttachAsync(admin, sessionId, noteId, Jpeg);

        var response = await athlete.GetAsync($"/api/v1/me/notes/{noteId}/attachments/{id}/download-url");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, body.GetProperty("id").GetGuid());
        Assert.Equal(await KeyAsync(id), FakeObjectStorage.KeyOf(body.GetProperty("downloadUrl").GetString()!));
    }

    [Fact]
    public async Task Another_athlete_cannot_reach_an_image_by_knowing_its_ids()
    {
        var admin = await AdminClientAsync();
        var other = await AthleteClientAsync(SessionNoteAttachmentApiFactory.OtherAthlete);
        var (sessionId, noteId) = await NoteAsync(admin, SessionNoteAttachmentApiFactory.Athlete);
        var id = await AttachAsync(admin, sessionId, noteId, Jpeg);

        var response = await other.GetAsync($"/api/v1/me/notes/{noteId}/attachments/{id}/download-url");

        // The same answer as a note that does not exist at all.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertErrorAsync(response, "SESSION_NOTE_NOT_FOUND");

        var unknown = await other.GetAsync($"/api/v1/me/notes/{Guid.NewGuid()}/attachments/{id}/download-url");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        await AssertErrorAsync(unknown, "SESSION_NOTE_NOT_FOUND");

        // Nor does it show up in their history.
        var theirs = await other.GetFromJsonAsync<JsonElement>("/api/v1/me/notes?pageSize=100");
        Assert.DoesNotContain(theirs.GetProperty("items").EnumerateArray(), x => Id(x) == noteId);
    }

    [Fact]
    public async Task An_attachment_id_is_only_valid_under_its_own_note()
    {
        var admin = await AdminClientAsync();
        var athlete = await AthleteClientAsync(SessionNoteAttachmentApiFactory.Athlete);
        var (sessionId, noteA) = await NoteAsync(admin, SessionNoteAttachmentApiFactory.Athlete);
        var noteB = Id(await WriteNoteAsync(admin, sessionId));
        var onA = await AttachAsync(admin, sessionId, noteA, Jpeg);

        foreach (var response in new[]
                 {
                     await admin.GetAsync($"/api/v1/sessions/{sessionId}/notes/{noteB}/attachments/{onA}/download-url"),
                     await admin.PostAsync($"/api/v1/sessions/{sessionId}/notes/{noteB}/attachments/{onA}/complete", null),
                     await admin.DeleteAsync($"/api/v1/sessions/{sessionId}/notes/{noteB}/attachments/{onA}"),
                     await athlete.GetAsync($"/api/v1/me/notes/{noteB}/attachments/{onA}/download-url")
                 })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            await AssertErrorAsync(response, "ATTACHMENT_NOT_FOUND");
        }

        Assert.Equal(SessionNoteAttachmentStatus.Committed, (await RowAsync(onA))!.Status);
    }

    [Fact]
    public async Task A_note_under_the_wrong_session_or_another_coachs_session_is_not_found()
    {
        var admin = await AdminClientAsync();
        var (sessionId, noteId) = await NoteAsync(admin, SessionNoteAttachmentApiFactory.Athlete);
        var (otherSession, _) = await NoteAsync(admin, SessionNoteAttachmentApiFactory.OtherAthlete);
        var id = await AttachAsync(admin, sessionId, noteId, Jpeg);

        // The right note under a different session of the same coach.
        var wrongSession = await admin.PostAsJsonAsync(
            $"/api/v1/sessions/{otherSession}/notes/{noteId}/attachments", new { contentType = Jpeg, sizeBytes = 64 });
        Assert.Equal(HttpStatusCode.NotFound, wrongSession.StatusCode);
        await AssertErrorAsync(wrongSession, "SESSION_NOTE_NOT_FOUND");

        var wrongSessionRead = await admin.GetAsync(
            $"/api/v1/sessions/{otherSession}/notes/{noteId}/attachments/{id}/download-url");
        Assert.Equal(HttpStatusCode.NotFound, wrongSessionRead.StatusCode);
        await AssertErrorAsync(wrongSessionRead, "SESSION_NOTE_NOT_FOUND");

        // A note on another coach's session, written straight to the database.
        var (foreignSession, foreignNote) = await ForeignNoteAsync();

        foreach (var response in new[]
                 {
                     await admin.PostAsJsonAsync($"/api/v1/sessions/{foreignSession}/notes/{foreignNote}/attachments",
                         new { contentType = Jpeg, sizeBytes = 64 }),
                     await admin.GetAsync($"/api/v1/sessions/{foreignSession}/notes/{foreignNote}/attachments/{id}/download-url"),
                     await admin.DeleteAsync($"/api/v1/sessions/{foreignSession}/notes/{foreignNote}/attachments/{id}")
                 })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            await AssertErrorAsync(response, "SESSION_NOTE_NOT_FOUND");
        }

        await AssertNoRowsAsync(foreignNote);
    }

    [Fact]
    public async Task The_admin_can_refresh_a_download_url()
    {
        var admin = await AdminClientAsync();
        var (sessionId, noteId) = await NoteAsync(admin, SessionNoteAttachmentApiFactory.Athlete);
        var id = await AttachAsync(admin, sessionId, noteId, Webp);

        var first = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/sessions/{sessionId}/notes/{noteId}/attachments/{id}/download-url");
        var second = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/sessions/{sessionId}/notes/{noteId}/attachments/{id}/download-url");

        Assert.Equal(id, first.GetProperty("id").GetGuid());
        Assert.NotEqual(first.GetProperty("downloadUrl").GetString(), second.GetProperty("downloadUrl").GetString());
    }

    // ================================================ existing note behaviour

    [Fact]
    public async Task Notes_still_create_edit_and_list_as_before_with_an_attachments_list()
    {
        var admin = await AdminClientAsync();
        var (sessionId, noteId) = await NoteAsync(admin, SessionNoteAttachmentApiFactory.Athlete);

        var created = await WriteNoteAsync(admin, sessionId, "A title", "Some content");
        Assert.Equal("A title", created.GetProperty("title").GetString());
        Assert.Equal("Some content", created.GetProperty("content").GetString());
        Assert.Empty(created.GetProperty("attachments").EnumerateArray());

        var id = await AttachAsync(admin, sessionId, noteId, Jpeg);

        // Editing the text leaves the images in place.
        var edited = await admin.PutAsJsonAsync(
            $"/api/v1/sessions/{sessionId}/notes/{noteId}", new { title = "New title", content = "New content" });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var body = await edited.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("New title", body.GetProperty("title").GetString());
        Assert.Equal([id], body.GetProperty("attachments").EnumerateArray().Select(Id));

        // Validation is unchanged: a title is still required.
        var invalid = await admin.PutAsJsonAsync(
            $"/api/v1/sessions/{sessionId}/notes/{noteId}", new { content = "No title" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    // ================================================================ helpers

    private static Guid Id(JsonElement x) => x.GetProperty("id").GetGuid();

    private async Task<Guid> AttachAsync(HttpClient admin, Guid sessionId, Guid noteId, string contentType)
    {
        var bytes = Image(contentType);
        var upload = await RequestUploadAsync(admin, sessionId, noteId, contentType, bytes.Length);
        await UploadAndCompleteAsync(admin, sessionId, noteId, upload, contentType, bytes);
        return upload.GetProperty("attachmentId").GetGuid();
    }

    private static async Task<JsonElement> RequestUploadAsync(
        HttpClient admin, Guid sessionId, Guid noteId, string contentType, long sizeBytes)
    {
        var response = await admin.PostAsJsonAsync(
            $"/api/v1/sessions/{sessionId}/notes/{noteId}/attachments", new { contentType, sizeBytes });

        if (response.StatusCode != HttpStatusCode.Created)
            Assert.Fail($"upload request {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task UploadAndCompleteAsync(
        HttpClient admin, Guid sessionId, Guid noteId, JsonElement upload, string contentType, byte[] bytes)
    {
        var response = await UploadAndCompleteRawAsync(admin, sessionId, noteId, upload, contentType, bytes);

        if (response.StatusCode != HttpStatusCode.OK)
            Assert.Fail($"complete {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private async Task<HttpResponseMessage> UploadAndCompleteRawAsync(
        HttpClient admin, Guid sessionId, Guid noteId, JsonElement upload, string contentType, byte[] bytes)
    {
        factory.Storage.SimulatePut(upload.GetProperty("uploadUrl").GetString()!, bytes, contentType);
        return await CompleteAsync(admin, sessionId, noteId, upload.GetProperty("attachmentId").GetGuid());
    }

    private static Task<HttpResponseMessage> CompleteAsync(HttpClient admin, Guid sessionId, Guid noteId, Guid id) =>
        admin.PostAsync($"/api/v1/sessions/{sessionId}/notes/{noteId}/attachments/{id}/complete", null);

    private async Task<(Guid SessionId, Guid NoteId)> NoteAsync(HttpClient admin, string athleteEmail)
    {
        var sessionId = await SeedObservationAsync(await ProfileIdAsync(athleteEmail));
        return (sessionId, Id(await WriteNoteAsync(admin, sessionId)));
    }

    private static async Task<JsonElement> WriteNoteAsync(
        HttpClient admin, Guid sessionId, string title = "Note", string content = "Content")
    {
        var response = await admin.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/notes", new { title, content });

        if (response.StatusCode != HttpStatusCode.Created)
            Assert.Fail($"note {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<Guid> SeedObservationAsync(Guid athleteProfileId)
    {
        await using var scope = await DbAsync();
        var clock = scope.Services.GetRequiredService<IClock>();
        var coachId = await scope.Db.AthleteProfiles.AsNoTracking()
            .Where(x => x.Id == athleteProfileId).Select(x => x.CoachId).SingleAsync();

        var start = DateTime.UtcNow.AddDays(-1);
        var session = Session.CreateObservation(
            coachId, athleteProfileId, start, start.AddMinutes(60), "Pool", deductsSession: false, clock.UtcNow);

        scope.Db.Sessions.Add(session);
        await scope.Db.SaveChangesAsync();
        return session.Id;
    }

    private async Task<(Guid SessionId, Guid NoteId)> ForeignNoteAsync()
    {
        var sessionId = await SeedObservationAsync(await ProfileIdAsync(SessionNoteAttachmentApiFactory.ForeignAthlete));

        await using var scope = await DbAsync();
        var note = SessionNote.Write(sessionId, Guid.NewGuid(), "Foreign", "Foreign note", DateTime.UtcNow);
        scope.Db.SessionNotes.Add(note);
        await scope.Db.SaveChangesAsync();
        return (sessionId, note.Id);
    }

    private async Task ExpireUploadsAsync(Guid noteId)
    {
        await using var scope = await DbAsync();
        var past = DateTime.UtcNow.AddHours(-2);
        await scope.Db.Database.ExecuteSqlAsync(
            $"""update "SessionNoteAttachments" set "UploadExpiresAtUtc" = {past} where "SessionNoteId" = {noteId} and "Status" = 'Pending'""");
    }

    private async Task RunCleanupAsync()
    {
        await using var scope = await DbAsync();
        await scope.Services.GetRequiredService<SessionNoteAttachmentCleanupJob>().RunAsync(CancellationToken.None);
    }

    private async Task<SessionNoteAttachment?> RowAsync(Guid id)
    {
        await using var scope = await DbAsync();
        return await scope.Db.SessionNoteAttachments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
    }

    private async Task<string> KeyAsync(Guid id) => (await RowAsync(id))!.StorageKey;

    private async Task AssertNoRowsAsync(Guid noteId)
    {
        await using var scope = await DbAsync();
        Assert.False(await scope.Db.SessionNoteAttachments.AnyAsync(x => x.SessionNoteId == noteId));
    }

    private async Task<Guid> ProfileIdAsync(string email)
    {
        await using var scope = await DbAsync();
        return await (from user in scope.Db.Users
                      join profile in scope.Db.AthleteProfiles on user.Id equals profile.UserId
                      where user.Email == email
                      select profile.Id).SingleAsync();
    }

    private sealed class DbScope(AsyncServiceScope scope) : IAsyncDisposable
    {
        public IServiceProvider Services => scope.ServiceProvider;
        public AppDbContext Db { get; } = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        public ValueTask DisposeAsync() => scope.DisposeAsync();
    }

    private Task<DbScope> DbAsync() => Task.FromResult(new DbScope(factory.Services.CreateAsyncScope()));

    private static async Task<Guid> AthleteUserIdAsync(HttpClient admin, string email)
    {
        var page = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/athletes?search={Uri.EscapeDataString(email)}");
        return Id(page.GetProperty("items").EnumerateArray().Single());
    }

    private Task<HttpClient> AdminClientAsync() => ClientAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);

    private Task<HttpClient> AthleteClientAsync(string email) => ClientAsync(email, AthleteApiFactory.AthletePassword);

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
