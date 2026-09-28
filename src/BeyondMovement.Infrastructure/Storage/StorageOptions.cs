namespace BeyondMovement.Infrastructure.Storage;

/// <summary>
/// Private object storage for uploaded files (the <c>Storage</c> configuration section).
/// <para>
/// <b>There is no access key or secret here, and there must never be one.</b> In ECS the SDK's
/// default credential chain picks up the task role (<c>BeyondMovementECSTaskRole</c>); on a
/// developer machine it finds an AWS profile or environment credentials if there are any. Without
/// credentials the API still starts — only attachment operations fail, with
/// <c>STORAGE_UNAVAILABLE</c>.
/// </para>
/// <para>
/// <b>The bucket is a deployment choice, not an application one.</b> It ships empty and each
/// environment supplies it (<c>Storage__S3__BucketName</c>, <c>Storage__S3__Region</c>), the same
/// way <c>Payments__InstaPay__*</c> works. Left empty, the API starts normally and attachment
/// operations answer <c>STORAGE_UNAVAILABLE</c>.
/// </para>
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Lifetime of a pre-signed upload URL. Kept short: it is a write capability.</summary>
    public int UploadUrlMinutes { get; set; } = 5;

    /// <summary>Lifetime of a pre-signed download URL.</summary>
    public int DownloadUrlMinutes { get; set; } = 15;

    /// <summary>Largest image accepted, in bytes. 10 MB.</summary>
    public long MaxImageBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Pending plus committed images one note may hold.</summary>
    public int MaxAttachmentsPerNote { get; set; } = 5;

    public S3StorageOptions S3 { get; set; } = new();

    /// <summary>Whether a bucket has been configured at all. Without one, storage is unavailable.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(S3.BucketName);

    public TimeSpan UploadUrlLifetime => TimeSpan.FromMinutes(UploadUrlMinutes);
    public TimeSpan DownloadUrlLifetime => TimeSpan.FromMinutes(DownloadUrlMinutes);
}

public sealed class S3StorageOptions
{
    /// <summary>
    /// The private bucket. Supplied per environment (<c>Storage__S3__BucketName</c>); there is no
    /// default. Not a secret.
    /// </summary>
    public string BucketName { get; set; } = "";

    /// <summary>
    /// The bucket's region, e.g. <c>ap-south-1</c> (<c>Storage__S3__Region</c>). Required whenever
    /// a bucket is set, unless <see cref="ServiceUrl"/> points at an emulator.
    /// </summary>
    public string Region { get; set; } = "";

    /// <summary>
    /// Only for a local S3-compatible emulator (MinIO, LocalStack). Leave empty for AWS. When set,
    /// path-style addressing is used, which is what emulators expect.
    /// </summary>
    public string? ServiceUrl { get; set; }
}
