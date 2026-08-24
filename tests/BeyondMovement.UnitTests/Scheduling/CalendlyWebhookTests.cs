using System.Security.Cryptography;
using System.Text;
using BeyondMovement.Infrastructure.Calendly;
using BeyondMovement.Modules.Scheduling.Calendly;
using Microsoft.Extensions.Options;

namespace BeyondMovement.UnitTests.Scheduling;

public sealed class CalendlyWebhookTests
{
    [Fact]
    public void Valid_signature_and_realistic_created_payload_are_accepted_and_normalized()
    {
        const string key = "test-signing-key";
        var now = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
        var json = CreatedPayload;
        var timestamp = new DateTimeOffset(now).ToUnixTimeSeconds();
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes($"{timestamp}.{json}")));
        var verifier = new CalendlyWebhookVerifier(Options.Create(new CalendlyOptions { WebhookSigningKeys = [key] }));

        Assert.True(verifier.IsValid(json, $"t={timestamp},v1={signature}", now));
        var parsed = new CalendlyWebhookParser().Parse(json);
        Assert.Equal("invitee.created", parsed.EventType);
        Assert.Equal("athlete@example.test", parsed.Invitee.Email);
        Assert.Equal(DateTimeKind.Utc, parsed.Invitee.StartUtc.Kind);
        Assert.False(string.IsNullOrWhiteSpace(parsed.IdempotencyKey));
    }

    [Fact]
    public void Old_or_modified_signature_is_rejected()
    {
        var now = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
        var verifier = new CalendlyWebhookVerifier(Options.Create(new CalendlyOptions { WebhookSigningKeys = ["key"] }));
        Assert.False(verifier.IsValid(CreatedPayload, "t=1,v1=deadbeef", now));
        Assert.False(verifier.IsValid(CreatedPayload, null, now));
    }

    private const string CreatedPayload = """
    {
      "created_at":"2026-08-24T12:00:00Z",
      "event":"invitee.created",
      "payload":{
        "uri":"https://api.calendly.com/scheduled_events/event/invitees/invitee",
        "event":"https://api.calendly.com/scheduled_events/event",
        "event_type":"https://api.calendly.com/event_types/type",
        "email":"athlete@example.test",
        "name":"Athlete Test",
        "status":"active",
        "cancel_url":"https://calendly.test/cancel",
        "reschedule_url":"https://calendly.test/reschedule",
        "rescheduled":false,
        "scheduled_event":{
          "start_time":"2026-08-25T10:00:00Z",
          "end_time":"2026-08-25T11:00:00Z",
          "location":{"type":"zoom","location":"https://zoom.test/meeting"}
        }
      }
    }
    """;
}
