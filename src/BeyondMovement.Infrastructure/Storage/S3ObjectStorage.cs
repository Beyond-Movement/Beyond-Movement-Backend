using System.Net;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeyondMovement.Infrastructure.Storage;

/// <summary>
/// <see cref="IObjectStorage"/> over Amazon S3. The only file in the solution that names the AWS
/// SDK.
/// <para>
/// <b>Credentials come from the SDK's default chain</b> — the ECS task role in production — and
/// never from configuration. The client is created on first use rather than at startup, so a
/// machine with no AWS credentials at all still boots, serves every non-attachment endpoint, and
/// answers attachment calls with <c>STORAGE_UNAVAILABLE</c>.
/// </para>
/// <para>
/// <b>With no bucket configured, every operation fails as unavailable without touching the SDK</b>,
/// so an environment that has not been given a bucket never reaches AWS at all.
/// </para>
/// <para>
/// Nothing here sets an ACL: the bucket is private with Block Public Access on, and every object
/// inherits that. Log lines carry the storage key, which is two GUIDs, and never a URL.
/// </para>
/// </summary>
public sealed class S3ObjectStorage : IObjectStorage, IDisposable
{
    private readonly S3StorageOptions _options;
    private readonly bool _configured;
    private readonly ILogger<S3ObjectStorage> _logger;
    private readonly Lazy<AmazonS3Client> _client;

    public S3ObjectStorage(IOptions<StorageOptions> options, ILogger<S3ObjectStorage> logger)
    {
        _options = options.Value.S3;
        _configured = options.Value.IsConfigured;
        _logger = logger;
        // PublicationOnly: a failed construction (no region, a transient credentials problem) is
        // retried on the next call instead of being cached for the life of the process.
        _client = new Lazy<AmazonS3Client>(CreateClient, LazyThreadSafetyMode.PublicationOnly);
    }

    private AmazonS3Client CreateClient()
    {
        var config = new AmazonS3Config();

        if (!string.IsNullOrWhiteSpace(_options.ServiceUrl))
        {
            config.ServiceURL = _options.ServiceUrl;
            config.ForcePathStyle = true;
            if (!string.IsNullOrWhiteSpace(_options.Region))
                config.AuthenticationRegion = _options.Region;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(_options.Region);
        }

        // No credentials argument: the default chain (environment, profile, ECS container
        // credentials, instance metadata) is resolved by the SDK.
        return new AmazonS3Client(config);
    }

    public Task<PresignedUrl> PresignUploadAsync(string key, string contentType, TimeSpan lifetime) =>
        Presign(key, HttpVerb.PUT, contentType, lifetime);

    public Task<PresignedUrl> PresignDownloadAsync(string key, TimeSpan lifetime) =>
        Presign(key, HttpVerb.GET, contentType: null, lifetime);

    private Task<PresignedUrl> Presign(string key, HttpVerb verb, string? contentType, TimeSpan lifetime) =>
        Guard($"presign {verb}", key, async () =>
        {
            var expiresAtUtc = DateTime.UtcNow.Add(lifetime);
            var request = new GetPreSignedUrlRequest
            {
                BucketName = _options.BucketName,
                Key = key,
                Verb = verb,
                Expires = expiresAtUtc,
                Protocol = string.IsNullOrWhiteSpace(_options.ServiceUrl) ? Protocol.HTTPS : ProtocolFromServiceUrl()
            };

            // Signed into the URL, so the PUT must carry exactly this Content-Type.
            if (contentType is not null) request.ContentType = contentType;

            var url = await _client.Value.GetPreSignedURLAsync(request);
            return new PresignedUrl(url, expiresAtUtc);
        });

    private Protocol ProtocolFromServiceUrl() =>
        _options.ServiceUrl!.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? Protocol.HTTP
            : Protocol.HTTPS;

    public Task<StoredObjectInfo?> GetInfoAsync(string key, CancellationToken ct) =>
        Guard("head", key, async () =>
        {
            try
            {
                var response = await _client.Value.GetObjectMetadataAsync(
                    new GetObjectMetadataRequest { BucketName = _options.BucketName, Key = key }, ct);

                return (StoredObjectInfo?)new StoredObjectInfo(
                    response.Headers.ContentLength, response.Headers.ContentType);
            }
            catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        });

    public Task<byte[]> ReadPrefixAsync(string key, int length, CancellationToken ct) =>
        Guard("read prefix", key, async () =>
        {
            try
            {
                using var response = await _client.Value.GetObjectAsync(new GetObjectRequest
                {
                    BucketName = _options.BucketName,
                    Key = key,
                    ByteRange = new ByteRange(0, length - 1)
                }, ct);

                var buffer = new byte[length];
                var read = 0;
                while (read < length)
                {
                    var n = await response.ResponseStream.ReadAsync(buffer.AsMemory(read, length - read), ct);
                    if (n == 0) break;
                    read += n;
                }

                return buffer[..read];
            }
            catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                return [];
            }
        });

    public Task DeleteAsync(string key, CancellationToken ct) =>
        Guard("delete", key, async () =>
        {
            await _client.Value.DeleteObjectAsync(
                new DeleteObjectRequest { BucketName = _options.BucketName, Key = key }, ct);
            return true;
        });

    /// <summary>
    /// One translation from every SDK failure to <see cref="StorageUnavailableException"/>. The
    /// SDK's own message is logged here and never reaches the client.
    /// </summary>
    private async Task<T> Guard<T>(string operation, string key, Func<Task<T>> action)
    {
        if (!_configured)
        {
            _logger.LogWarning(
                "Object storage {Operation} refused for {StorageKey}: Storage:S3:BucketName is not configured",
                operation, key);
            throw new StorageUnavailableException("Object storage is not configured.");
        }

        try
        {
            return await action();
        }
        catch (Exception e) when (e is not OperationCanceledException and not StorageUnavailableException)
        {
            _logger.LogError(e, "Object storage {Operation} failed for {StorageKey}", operation, key);
            throw new StorageUnavailableException($"Object storage {operation} failed.", e);
        }
    }

    public void Dispose()
    {
        if (_client.IsValueCreated) _client.Value.Dispose();
    }
}
