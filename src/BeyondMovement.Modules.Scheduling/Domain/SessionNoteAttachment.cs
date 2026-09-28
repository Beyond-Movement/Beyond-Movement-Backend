namespace BeyondMovement.Modules.Scheduling.Domain;

/// <summary>
/// Where an attachment is in its life. Stored as a string, like every other enum here.
/// </summary>
public enum SessionNoteAttachmentStatus
{
    /// <summary>
    /// An upload URL has been issued, but the API has not yet seen and verified the object. Never
    /// shown to anyone — a pending attachment may be an object that was never uploaded, or one
    /// that is not the image it claims to be.
    /// </summary>
    Pending,

    /// <summary>Verified: the object exists, its size and signature match. The only visible state.</summary>
    Committed,

    /// <summary>
    /// Deleted as far as every reader is concerned, but its object may still be in storage. The
    /// row is kept because <see cref="SessionNoteAttachment.StorageKey"/> is the only record of
    /// where that object is; the cleanup job removes the row only once the object is gone.
    /// </summary>
    Deleting
}

/// <summary>
/// An image attached to a <see cref="SessionNote"/>. The bytes live in private object storage;
/// this row holds only what is needed to find, verify, serve and eventually delete them.
/// <para>
/// <b>Two-phase upload.</b> <see cref="Request"/> creates a <see cref="SessionNoteAttachmentStatus.Pending"/>
/// row and the client PUTs straight to storage with a short-lived pre-signed URL. The API then
/// checks the object that actually arrived — size, stored content type and the file's leading
/// bytes — before <see cref="Commit"/> makes it visible. A client can never claim a file it did
/// not upload, and a declared <c>image/jpeg</c> that is really something else is never served.
/// </para>
/// <para>
/// <b>Deletion never loses the storage key.</b> Deleting an attachment, its note or its session
/// moves the row to <see cref="SessionNoteAttachmentStatus.Deleting"/> (or, for a database-level
/// cascade, leaves it with a null <see cref="SessionNoteId"/>) rather than removing it. The row
/// goes only after its object has been removed, so a storage outage delays cleanup instead of
/// leaving an object nobody can find.
/// </para>
/// </summary>
public sealed class SessionNoteAttachment
{
    public const string JpegContentType = "image/jpeg";
    public const string PngContentType = "image/png";
    public const string WebpContentType = "image/webp";

    /// <summary>
    /// Every key starts here. The ECS task role's S3 policy is scoped to exactly this prefix, so
    /// it is a constant rather than configuration: a different prefix would simply be denied.
    /// </summary>
    public const string KeyPrefix = "session-notes/";

    public const int MaxStorageKeyLength = 200;

    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>
    /// The note it belongs to. Nullable only so a database-level cascade (a session deleted, and
    /// its notes with it) orphans the row instead of deleting the only copy of its storage key;
    /// the cleanup job treats a null here as "delete the object".
    /// </summary>
    public Guid? SessionNoteId { get; private set; }

    /// <summary>
    /// <c>session-notes/{noteId}/{attachmentId}.{ext}</c> — two server-generated ids and an
    /// extension derived from the accepted content type. No filename, name, email or anything
    /// else about a person ever appears in it.
    /// </summary>
    public string StorageKey { get; private set; } = null!;

    public string ContentType { get; private set; } = null!;

    /// <summary>What the client said it would upload. Checked against the real object on completion.</summary>
    public long DeclaredSizeBytes { get; private set; }

    /// <summary>The size storage reported when the upload was verified. Null until committed.</summary>
    public long? SizeBytes { get; private set; }

    public SessionNoteAttachmentStatus Status { get; private set; }
    public Guid UploadedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>When the pre-signed upload URL stops working.</summary>
    public DateTime UploadExpiresAtUtc { get; private set; }

    public DateTime? CommittedAtUtc { get; private set; }
    public DateTime? DeletionRequestedAtUtc { get; private set; }

    /// <summary>Display order within the note, assigned when the upload is requested.</summary>
    public int SortOrder { get; private set; }

    /// <summary>Postgres <c>xmin</c>, so two concurrent completions cannot both write.</summary>
    public uint Version { get; private set; }

    private SessionNoteAttachment() { }   // EF Core

    public static SessionNoteAttachment Request(
        Guid sessionNoteId, string contentType, long declaredSizeBytes, Guid uploadedByUserId,
        int sortOrder, DateTime nowUtc, DateTime uploadExpiresAtUtc)
    {
        var attachment = new SessionNoteAttachment
        {
            SessionNoteId = sessionNoteId,
            ContentType = contentType,
            DeclaredSizeBytes = declaredSizeBytes,
            UploadedByUserId = uploadedByUserId,
            SortOrder = sortOrder,
            Status = SessionNoteAttachmentStatus.Pending,
            CreatedAtUtc = nowUtc,
            UploadExpiresAtUtc = uploadExpiresAtUtc
        };

        attachment.StorageKey =
            $"{KeyPrefix}{sessionNoteId:D}/{attachment.Id:D}.{ExtensionFor(contentType)}";

        return attachment;
    }

    public static bool IsSupportedContentType(string? contentType) =>
        contentType is JpegContentType or PngContentType or WebpContentType;

    public static string ExtensionFor(string contentType) => contentType switch
    {
        JpegContentType => "jpg",
        PngContentType => "png",
        WebpContentType => "webp",
        _ => throw new ArgumentOutOfRangeException(nameof(contentType), contentType,
            "Not a supported attachment content type.")
    };

    /// <summary>
    /// Whether <paramref name="header"/> — the first bytes of the stored object — is really the
    /// format <paramref name="contentType"/> claims. The declared type alone is never trusted.
    /// </summary>
    public static bool SignatureMatches(string contentType, ReadOnlySpan<byte> header) => contentType switch
    {
        // FF D8 FF: every JPEG starts with the SOI marker followed by another marker.
        JpegContentType => header.Length >= 3
            && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,

        // The fixed eight-byte PNG signature.
        PngContentType => header.Length >= 8
            && header[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),

        // A RIFF container ("RIFF", four length bytes) whose form type is "WEBP".
        WebpContentType => header.Length >= 12
            && header[..4].SequenceEqual("RIFF"u8)
            && header[8..12].SequenceEqual("WEBP"u8),

        _ => false
    };

    /// <summary>How many leading bytes <see cref="SignatureMatches"/> needs at most.</summary>
    public const int SignatureLength = 12;

    public void Commit(long verifiedSizeBytes, DateTime nowUtc)
    {
        if (Status != SessionNoteAttachmentStatus.Pending)
            throw new InvalidOperationException($"Only a pending attachment can be committed, not {Status}.");

        SizeBytes = verifiedSizeBytes;
        CommittedAtUtc = nowUtc;
        Status = SessionNoteAttachmentStatus.Committed;
    }

    /// <summary>
    /// Hides the attachment from every reader and queues its object for removal. Idempotent. The
    /// row itself stays until the object is confirmed gone.
    /// </summary>
    public void MarkForDeletion(DateTime nowUtc)
    {
        if (Status == SessionNoteAttachmentStatus.Deleting) return;

        Status = SessionNoteAttachmentStatus.Deleting;
        DeletionRequestedAtUtc = nowUtc;
    }
}
