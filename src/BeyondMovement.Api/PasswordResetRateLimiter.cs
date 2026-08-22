using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using BeyondMovement.SharedKernel;

namespace BeyondMovement.Api;

/// <summary>
/// Throttles password-reset requests per email address, which the rate-limiting middleware
/// cannot do: the address is in the request body, and a partitioner runs before the body has
/// been read. So the per-IP limit is middleware and this one is called from the endpoint.
/// <para>
/// <b>It must not become an account-enumeration oracle.</b> The endpoint's whole design is that
/// an existing address and an unknown one are indistinguishable (architecture section 942). The
/// counter is therefore keyed on whatever address was submitted, before any database lookup, so
/// the third request from one address is refused identically whether or not an account exists.
/// Counting only real accounts would tell an attacker exactly which addresses are registered.
/// </para>
/// <para>
/// The trade-off, which is inherent to per-email limiting rather than a flaw in it: someone can
/// spend a victim's allowance to stop them requesting a reset for the rest of the window. The
/// window is short, and the alternative — no per-email limit — lets one address be mail-bombed
/// indefinitely.
/// </para>
/// </summary>
public sealed class PasswordResetRateLimiter : IDisposable
{
    /// <summary>Three per hour per email, per architecture section 12.5.</summary>
    public const int DefaultPermitLimit = 3;

    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly PartitionedRateLimiter<string> _limiter;

    public PasswordResetRateLimiter(IConfiguration configuration)
    {
        var permitLimit = configuration.GetValue("RateLimits:PasswordResetPerEmailPerHour", DefaultPermitLimit);

        _limiter = PartitionedRateLimiter.Create<string, string>(email =>
            RateLimitPartition.GetFixedWindowLimiter(
                // Hashed, not the address itself: partition keys live in memory for the life of
                // the window and can surface in a dump or a diagnostic, and CLAUDE.md section 7
                // says a full email address is never written anywhere it might be read.
                partitionKey: Fingerprint(email),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = Window
                }));
    }

    /// <summary>
    /// Null when the request may proceed, or the error to return when it may not. Normalises the
    /// address first so casing and surrounding space cannot buy extra attempts.
    /// </summary>
    public Error? Check(string email)
    {
        var lease = _limiter.AttemptAcquire(email.Trim().ToLowerInvariant());

        if (lease.IsAcquired)
            return null;

        var retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var value)
            ? (int)value.TotalSeconds
            : (int)Window.TotalSeconds;

        return new Error(
            "TOO_MANY_REQUESTS",
            "Too many password reset requests for this address. Try again later.",
            StatusCodes.Status429TooManyRequests,
            retryAfter);
    }

    /// <summary>A stable, non-reversible key for one address.</summary>
    private static string Fingerprint(string email) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email)));

    public void Dispose() => _limiter.Dispose();
}
