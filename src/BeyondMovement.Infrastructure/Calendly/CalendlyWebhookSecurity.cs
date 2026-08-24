using System.Security.Cryptography;
using System.Text;
using BeyondMovement.Modules.Scheduling.Calendly;
using Microsoft.Extensions.Options;

namespace BeyondMovement.Infrastructure.Calendly;

public sealed class CalendlyWebhookVerifier(IOptions<CalendlyOptions> options) : ICalendlyWebhookVerifier
{
    private readonly CalendlyOptions _options = options.Value;
    public bool IsValid(string payload, string? header, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(header) || _options.WebhookSigningKeys.Length == 0) return false;
        var fields = header.Split(',').Select(x => x.Trim().Split('=', 2)).Where(x => x.Length == 2)
            .ToDictionary(x => x[0], x => x[1], StringComparer.OrdinalIgnoreCase);
        if (!fields.TryGetValue("t", out var rawTimestamp) || !long.TryParse(rawTimestamp, out var timestamp) ||
            !fields.TryGetValue("v1", out var supplied)) return false;
        var sent = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime;
        if (Math.Abs((nowUtc - sent).TotalMinutes) > 5) return false;
        var signed = Encoding.UTF8.GetBytes($"{timestamp}.{payload}");
        foreach (var key in _options.WebhookSigningKeys.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), signed));
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(supplied.ToLowerInvariant()))) return true;
        }
        return false;
    }
}
