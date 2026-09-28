using BeyondMovement.SharedKernel;

namespace BeyondMovement.Api.Scheduling;

/// <summary>
/// The stable codes for session note attachments. They follow the project's status conventions:
/// 400 for a request that can never succeed as sent, 404 for anything the caller may not see,
/// 409 for a state the client has to resolve, 503 for a dependency that is down.
/// </summary>
public static class SessionNoteAttachmentErrors
{
    public const string UnsupportedAttachmentTypeCode = "UNSUPPORTED_ATTACHMENT_TYPE";
    public const string AttachmentTooLargeCode = "ATTACHMENT_TOO_LARGE";
    public const string AttachmentLimitReachedCode = "ATTACHMENT_LIMIT_REACHED";
    public const string AttachmentNotFoundCode = "ATTACHMENT_NOT_FOUND";
    public const string AttachmentUploadInvalidCode = "ATTACHMENT_UPLOAD_INVALID";
    public const string StorageUnavailableCode = "STORAGE_UNAVAILABLE";

    /// <summary>Only JPEG, PNG and WebP. HEIC is re-encoded to JPEG by the app before upload.</summary>
    public static readonly Error UnsupportedAttachmentType = new(UnsupportedAttachmentTypeCode,
        "Attach a JPEG, PNG or WebP image.", 400);

    /// <summary>Also returned for a size of zero or less, which is never a real image.</summary>
    public static Error AttachmentTooLarge(long maxBytes) => new(AttachmentTooLargeCode,
        $"An image must be larger than 0 bytes and at most {maxBytes} bytes.", 400);

    /// <summary>
    /// Counts uploads still in progress as well as finished ones, so five parallel requests
    /// cannot each see four and all succeed.
    /// </summary>
    public static Error AttachmentLimitReached(int max) => new(AttachmentLimitReachedCode,
        $"A note can have at most {max} images.", 409);

    /// <summary>
    /// Unknown, belonging to another note, still pending where a committed one is needed, or
    /// deleted — one answer for all, so an id can never be probed for existence.
    /// </summary>
    public static readonly Error AttachmentNotFound = new(AttachmentNotFoundCode,
        "Attachment not found.", 404);

    /// <summary>
    /// The upload could not be verified: nothing was uploaded, the upload window has closed, or
    /// the object is not the size, type or image format that was declared. Except when nothing
    /// arrived yet, the attachment is discarded and the client must request a new upload.
    /// </summary>
    public static Error AttachmentUploadInvalid(string why) => new(AttachmentUploadInvalidCode,
        why, 409);

    /// <summary>Object storage could not be reached. Nothing was changed; retry later.</summary>
    public static readonly Error StorageUnavailable = new(StorageUnavailableCode,
        "File storage is temporarily unavailable. Try again shortly.", 503);

    public static readonly string[] AllCodes =
    [
        UnsupportedAttachmentTypeCode, AttachmentTooLargeCode, AttachmentLimitReachedCode,
        AttachmentNotFoundCode, AttachmentUploadInvalidCode, StorageUnavailableCode
    ];
}
