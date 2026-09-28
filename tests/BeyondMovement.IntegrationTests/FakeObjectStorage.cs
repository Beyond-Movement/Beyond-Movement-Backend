using System.Collections.Concurrent;
using BeyondMovement.Infrastructure.Storage;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// An in-memory stand-in for S3, so the suite needs no AWS account and no network. Everything
/// the API does with storage — issuing URLs, checking the object, reading its first bytes,
/// deleting it — is the real code path against this store.
/// <para>
/// A test "uploads" the way the app would: it takes the upload URL the API returned and calls
/// <see cref="SimulatePut"/> with it. The key is recovered from the URL, so a test never builds a
/// storage key itself and cannot upload anywhere the API did not authorise.
/// </para>
/// </summary>
public sealed class FakeObjectStorage : IObjectStorage
{
    public const string Host = "https://fake-storage.test/";

    private readonly ConcurrentDictionary<string, (byte[] Bytes, string ContentType)> _objects = new();

    /// <summary>Every operation throws, as if S3 were unreachable.</summary>
    public bool Unavailable { get; set; }

    /// <summary>Only deletes fail, as if the delete permission were briefly missing.</summary>
    public bool FailDeletes { get; set; }

    public TimeSpan? LastUploadLifetime { get; private set; }
    public TimeSpan? LastDownloadLifetime { get; private set; }

    public bool Exists(string key) => _objects.ContainsKey(key);

    public IReadOnlyCollection<string> Keys => [.. _objects.Keys];

    /// <summary>Puts bytes where the given upload URL points, as the app's PUT would.</summary>
    public string SimulatePut(string uploadUrl, byte[] bytes, string contentType)
    {
        Assert.StartsWith(Host, uploadUrl, StringComparison.Ordinal);
        Assert.Contains("verb=PUT", uploadUrl, StringComparison.Ordinal);

        var key = KeyOf(uploadUrl);
        _objects[key] = (bytes, contentType);
        return key;
    }

    public static string KeyOf(string url) => Uri.UnescapeDataString(url[Host.Length..url.IndexOf('?')]);

    public Task<PresignedUrl> PresignUploadAsync(string key, string contentType, TimeSpan lifetime)
    {
        ThrowIfUnavailable();
        LastUploadLifetime = lifetime;
        return Task.FromResult(Sign(key, "PUT", lifetime));
    }

    public Task<PresignedUrl> PresignDownloadAsync(string key, TimeSpan lifetime)
    {
        ThrowIfUnavailable();
        LastDownloadLifetime = lifetime;
        return Task.FromResult(Sign(key, "GET", lifetime));
    }

    public Task<StoredObjectInfo?> GetInfoAsync(string key, CancellationToken ct)
    {
        ThrowIfUnavailable();
        return Task.FromResult(_objects.TryGetValue(key, out var o)
            ? new StoredObjectInfo(o.Bytes.LongLength, o.ContentType)
            : null);
    }

    public Task<byte[]> ReadPrefixAsync(string key, int length, CancellationToken ct)
    {
        ThrowIfUnavailable();
        return Task.FromResult(_objects.TryGetValue(key, out var o)
            ? o.Bytes.Take(length).ToArray()
            : []);
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        ThrowIfUnavailable();
        if (FailDeletes) throw new StorageUnavailableException("Simulated delete failure.");
        _objects.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    private static PresignedUrl Sign(string key, string verb, TimeSpan lifetime)
    {
        var expires = DateTime.UtcNow.Add(lifetime);
        return new PresignedUrl(
            $"{Host}{Uri.EscapeDataString(key)}?verb={verb}&sig={Guid.NewGuid():N}", expires);
    }

    private void ThrowIfUnavailable()
    {
        if (Unavailable) throw new StorageUnavailableException("Simulated outage.");
    }
}
