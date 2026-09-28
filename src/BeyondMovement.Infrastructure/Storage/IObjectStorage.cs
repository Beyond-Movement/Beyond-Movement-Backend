namespace BeyondMovement.Infrastructure.Storage;

/// <summary>
/// The few things the API needs from private object storage, and nothing else. Endpoint and
/// domain code depend on this rather than on the AWS SDK, so the SDK stays in one file and the
/// integration tests can run against an in-memory store with no AWS account.
/// <para>
/// Every failure to reach storage surfaces as <see cref="StorageUnavailableException"/>, which
/// the API turns into <c>503 STORAGE_UNAVAILABLE</c>.
/// </para>
/// <para>
/// <b>Pre-signed URLs are capabilities.</b> Anyone holding one can use it until it expires, so
/// they are never logged, never stored, and never built for longer than the configured lifetime.
/// </para>
/// </summary>
public interface IObjectStorage
{
    /// <summary>
    /// A URL that allows exactly one thing: PUT to <paramref name="key"/> with this content type,
    /// until it expires.
    /// </summary>
    Task<PresignedUrl> PresignUploadAsync(string key, string contentType, TimeSpan lifetime);

    /// <summary>A URL that allows GET of <paramref name="key"/> until it expires.</summary>
    Task<PresignedUrl> PresignDownloadAsync(string key, TimeSpan lifetime);

    /// <summary>The object's stored size and content type, or null when there is no such object.</summary>
    Task<StoredObjectInfo?> GetInfoAsync(string key, CancellationToken ct);

    /// <summary>
    /// Up to <paramref name="length"/> bytes from the start of the object — enough to check a file
    /// signature without downloading the file. Empty when the object does not exist.
    /// </summary>
    Task<byte[]> ReadPrefixAsync(string key, int length, CancellationToken ct);

    /// <summary>Removes the object. Deleting one that does not exist succeeds.</summary>
    Task DeleteAsync(string key, CancellationToken ct);
}

public sealed record PresignedUrl(string Url, DateTime ExpiresAtUtc);

public sealed record StoredObjectInfo(long SizeBytes, string? ContentType);

/// <summary>Storage could not be reached, or refused the API's own credentials.</summary>
public sealed class StorageUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
