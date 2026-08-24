using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeyondMovement.Modules.Scheduling.Calendly;

namespace BeyondMovement.Infrastructure.Calendly;

public sealed class CalendlyWebhookParser : ICalendlyWebhookParser
{
    public CalendlyWebhookEnvelope Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var type = root.GetProperty("event").GetString() ?? throw new JsonException("Missing event.");
        var created = root.TryGetProperty("created_at", out var ca) ? ca.GetString() : null;
        var payload = root.GetProperty("payload");
        var inviteeUri = Required(payload, "uri");
        var eventUri = Required(payload, "event");
        var eventTypeUri = Required(payload, "event_type");
        var start = payload.GetProperty("scheduled_event").GetProperty("start_time").GetDateTime().ToUniversalTime();
        var end = payload.GetProperty("scheduled_event").GetProperty("end_time").GetDateTime().ToUniversalTime();
        var scheduled = payload.GetProperty("scheduled_event");
        var location = scheduled.TryGetProperty("location", out var l) && l.ValueKind == JsonValueKind.Object
            ? Optional(l, "location") ?? Optional(l, "type") : null;
        var invitee = new CalendlyInvitee(inviteeUri, eventUri, eventTypeUri,
            Required(payload, "email"), Required(payload, "name"), start, end,
            Optional(payload, "status") ?? "active", location,
            location?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true ? location : null,
            Optional(payload, "cancel_url"), Optional(payload, "reschedule_url"),
            Bool(payload, "rescheduled"), Optional(payload, "old_invitee"), Optional(payload, "new_invitee"));
        var keySource = $"{type}|{inviteeUri}|{created}";
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(keySource)));
        var reason = payload.TryGetProperty("cancellation", out var c) ? Optional(c, "reason") : null;
        return new(type, key, invitee, reason);
    }

    private static string Required(JsonElement e, string name) => e.GetProperty(name).GetString() ?? throw new JsonException($"Missing {name}.");
    private static string? Optional(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False && p.GetBoolean();
}
