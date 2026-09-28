using BeyondMovement.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BeyondMovement.UnitTests.Storage;

/// <summary>
/// The S3 adapter's behaviour when an environment has not been given a bucket. None of these
/// reach AWS: an unconfigured adapter refuses before the SDK is ever constructed.
/// </summary>
public sealed class S3ObjectStorageTests
{
    private const string Key = "session-notes/00000000-0000-0000-0000-000000000001/00000000-0000-0000-0000-000000000002.jpg";

    private static S3ObjectStorage Unconfigured(string region = "") => new(
        Options.Create(new StorageOptions { S3 = new S3StorageOptions { BucketName = "", Region = region } }),
        NullLogger<S3ObjectStorage>.Instance);

    [Fact]
    public void Constructing_the_adapter_touches_nothing()
    {
        // Configured or not, construction must never resolve credentials or build a client:
        // that is what lets the API start on a machine with no AWS setup at all.
        using var unconfigured = Unconfigured();
        using var configured = new S3ObjectStorage(
            Options.Create(new StorageOptions { S3 = new S3StorageOptions { BucketName = "b", Region = "ap-south-1" } }),
            NullLogger<S3ObjectStorage>.Instance);
    }

    [Fact]
    public async Task Every_operation_is_unavailable_without_a_bucket()
    {
        using var storage = Unconfigured(region: "ap-south-1");

        await Assert.ThrowsAsync<StorageUnavailableException>(() => storage.PresignUploadAsync(Key, "image/jpeg", TimeSpan.FromMinutes(5)));
        await Assert.ThrowsAsync<StorageUnavailableException>(() => storage.PresignDownloadAsync(Key, TimeSpan.FromMinutes(15)));
        await Assert.ThrowsAsync<StorageUnavailableException>(() => storage.GetInfoAsync(Key, CancellationToken.None));
        await Assert.ThrowsAsync<StorageUnavailableException>(() => storage.ReadPrefixAsync(Key, 12, CancellationToken.None));
        await Assert.ThrowsAsync<StorageUnavailableException>(() => storage.DeleteAsync(Key, CancellationToken.None));
    }

    [Fact]
    public void The_bucket_is_not_configured_by_default()
    {
        var options = new StorageOptions();

        Assert.False(options.IsConfigured);
        Assert.Equal("", options.S3.BucketName);
        Assert.Equal("", options.S3.Region);

        // The product limits do have defaults: they are rules, not deployment choices.
        Assert.Equal(5, options.UploadUrlMinutes);
        Assert.Equal(15, options.DownloadUrlMinutes);
        Assert.Equal(10 * 1024 * 1024, options.MaxImageBytes);
        Assert.Equal(5, options.MaxAttachmentsPerNote);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("beyond-movement-files-745059801486-ap-south-1-an", true)]
    public void A_bucket_name_is_what_makes_storage_configured(string bucket, bool configured) =>
        Assert.Equal(configured, new StorageOptions { S3 = new S3StorageOptions { BucketName = bucket } }.IsConfigured);
}
