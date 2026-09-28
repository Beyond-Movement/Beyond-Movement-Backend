using BeyondMovement.Api.Scheduling;
using BeyondMovement.Modules.Scheduling.Domain;

namespace BeyondMovement.UnitTests.Scheduling;

public sealed class SessionNoteAttachmentTests
{
    private static readonly DateTime Now = new(2026, 9, 27, 8, 0, 0, DateTimeKind.Utc);

    private static SessionNoteAttachment Pending(string contentType = "image/jpeg", Guid? noteId = null) =>
        SessionNoteAttachment.Request(noteId ?? Guid.NewGuid(), contentType, 1000, Guid.NewGuid(), 0,
            Now, Now.AddMinutes(5));

    [Theory]
    [InlineData("image/jpeg", "jpg")]
    [InlineData("image/png", "png")]
    [InlineData("image/webp", "webp")]
    public void The_storage_key_is_two_server_ids_and_an_extension_from_the_type(string contentType, string ext)
    {
        var noteId = Guid.NewGuid();
        var attachment = Pending(contentType, noteId);

        Assert.Equal($"session-notes/{noteId}/{attachment.Id}.{ext}", attachment.StorageKey);
        Assert.Matches(@"^session-notes/[0-9a-f-]{36}/[0-9a-f-]{36}\.(jpg|png|webp)$", attachment.StorageKey);
        Assert.True(attachment.StorageKey.Length <= SessionNoteAttachment.MaxStorageKeyLength);
    }

    [Theory]
    [InlineData("image/jpeg", true)]
    [InlineData("image/png", true)]
    [InlineData("image/webp", true)]
    [InlineData("image/heic", false)]
    [InlineData("image/heif", false)]
    [InlineData("image/gif", false)]
    [InlineData("IMAGE/JPEG", false)]   // normalised by the service before this check
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_jpeg_png_and_webp_are_supported(string? contentType, bool supported) =>
        Assert.Equal(supported, SessionNoteAttachment.IsSupportedContentType(contentType));

    public static TheoryData<string, byte[], bool> Signatures => new()
    {
        { "image/jpeg", [0xFF, 0xD8, 0xFF, 0xE0], true },
        { "image/jpeg", [0xFF, 0xD8, 0xFF, 0xDB], true },
        { "image/jpeg", [0xFF, 0xD8], false },
        { "image/jpeg", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], false },
        { "image/png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], true },
        { "image/png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A], false },
        { "image/png", [0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0], false },
        { "image/webp", [.. "RIFF"u8, 1, 2, 3, 4, .. "WEBP"u8], true },
        { "image/webp", [.. "RIFF"u8, 1, 2, 3, 4, .. "WAVE"u8], false },
        { "image/webp", [.. "RIFF"u8, 1, 2, 3, 4, .. "WEB"u8], false },
        { "image/webp", [], false },
        { "image/gif", [.. "GIF89a"u8], false },
    };

    [Theory]
    [MemberData(nameof(Signatures))]
    public void The_file_signature_must_match_the_declared_type(string contentType, byte[] header, bool matches) =>
        Assert.Equal(matches, SessionNoteAttachment.SignatureMatches(contentType, header));

    [Fact]
    public void A_new_attachment_is_pending_with_nothing_verified()
    {
        var attachment = Pending();

        Assert.Equal(SessionNoteAttachmentStatus.Pending, attachment.Status);
        Assert.Null(attachment.SizeBytes);
        Assert.Null(attachment.CommittedAtUtc);
        Assert.Null(attachment.DeletionRequestedAtUtc);
        Assert.Equal(Now.AddMinutes(5), attachment.UploadExpiresAtUtc);
    }

    [Fact]
    public void Commit_records_the_verified_size()
    {
        var attachment = Pending();
        attachment.Commit(999, Now.AddMinutes(1));

        Assert.Equal(SessionNoteAttachmentStatus.Committed, attachment.Status);
        Assert.Equal(999, attachment.SizeBytes);
        Assert.Equal(Now.AddMinutes(1), attachment.CommittedAtUtc);
        Assert.Throws<InvalidOperationException>(() => attachment.Commit(999, Now));
    }

    [Fact]
    public void Marking_for_deletion_keeps_the_storage_key_and_is_idempotent()
    {
        var attachment = Pending();
        var key = attachment.StorageKey;

        attachment.MarkForDeletion(Now);
        attachment.MarkForDeletion(Now.AddHours(1));

        Assert.Equal(SessionNoteAttachmentStatus.Deleting, attachment.Status);
        Assert.Equal(Now, attachment.DeletionRequestedAtUtc);
        Assert.Equal(key, attachment.StorageKey);
        Assert.Throws<InvalidOperationException>(() => attachment.Commit(1, Now));
    }

    [Fact]
    public void A_pending_upload_is_abandoned_only_after_its_completion_grace()
    {
        var attachment = Pending();
        var closes = attachment.UploadExpiresAtUtc + SessionNoteAttachmentService.CompletionGrace;

        Assert.False(SessionNoteAttachmentService.IsAbandoned(attachment, attachment.UploadExpiresAtUtc.AddMinutes(1)));
        Assert.False(SessionNoteAttachmentService.IsAbandoned(attachment, closes));
        Assert.True(SessionNoteAttachmentService.IsAbandoned(attachment, closes.AddSeconds(1)));

        attachment.Commit(1000, Now);
        Assert.False(SessionNoteAttachmentService.IsAbandoned(attachment, closes.AddDays(1)));
    }
}
