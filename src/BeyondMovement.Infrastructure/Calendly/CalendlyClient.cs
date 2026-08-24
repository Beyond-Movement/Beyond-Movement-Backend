using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeyondMovement.Modules.Scheduling.Calendly;
using Microsoft.Extensions.Options;

namespace BeyondMovement.Infrastructure.Calendly;

public sealed class CalendlyClient(HttpClient http, IOptions<CalendlyOptions> options) : ICalendlyClient
{
    private readonly CalendlyOptions _options = options.Value;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<CalendlyAccount> GetCurrentUserAsync(CancellationToken ct)
    {
        using var doc = await GetAsync("/users/me", ct);
        var r = doc.RootElement.GetProperty("resource");
        return new(r.GetProperty("uri").GetString()!, r.GetProperty("current_organization").GetString()!, r.GetProperty("name").GetString()!);
    }

    public async Task<IReadOnlyList<CalendlyEventType>> GetEventTypesAsync(CancellationToken ct)
    {
        var user = string.IsNullOrWhiteSpace(_options.UserUri) ? (await GetCurrentUserAsync(ct)).UserUri : _options.UserUri;
        var items = await GetAllAsync($"/event_types?user={Uri.EscapeDataString(user)}&active=true&count=100", ct);
        return items.Select(ParseEventType).ToArray();
    }

    public async Task<CalendlyEventType> GetEventTypeAsync(string eventTypeUri, CancellationToken ct)
    {
        var uuid = eventTypeUri.TrimEnd('/').Split('/').Last();
        using var doc = await GetAsync($"/event_types/{Uri.EscapeDataString(uuid)}", ct);
        return ParseEventType(doc.RootElement.GetProperty("resource"));
    }

    public async Task<IReadOnlyList<CalendlySlot>> GetAvailableTimesAsync(string eventTypeUri, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var url = $"/event_type_available_times?event_type={Uri.EscapeDataString(eventTypeUri)}&start_time={Uri.EscapeDataString(Utc(fromUtc))}&end_time={Uri.EscapeDataString(Utc(toUtc))}";
        var items = await GetAllAsync(url, ct);
        return items.Select(x =>
        {
            var start = x.GetProperty("start_time").GetDateTime().ToUniversalTime();
            var link = x.TryGetProperty("scheduling_url", out var s) ? s.GetString() ?? string.Empty : string.Empty;
            return new CalendlySlot(start, link);
        }).ToArray();
    }

    public async Task<CalendlyInvitee> CreateInviteeAsync(CreateCalendlyInvitee request, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["event_type"] = request.EventTypeUri,
            ["start_time"] = Utc(request.StartUtc),
            ["invitee"] = new { name = request.Name, email = request.Email, timezone = request.TimeZone }
        };
        if (request.LocationKind is not null)
            payload["location"] = new { kind = request.LocationKind, location = request.Location };
        using var response = await SendAsync(HttpMethod.Post, "/invitees", payload, ct);
        using var doc = await ReadAsync(response, ct);
        var invitee = doc.RootElement.GetProperty("resource");
        var eventUri = invitee.GetProperty("event").GetString()
            ?? throw new CalendlyApiException(CalendlyFailureKind.MalformedResponse, "Calendly returned a booking without an event URI.");
        var eventUuid = eventUri.TrimEnd('/').Split('/').Last();
        using var scheduled = await GetAsync($"/scheduled_events/{eventUuid}", ct);
        return ParseInvitee(scheduled.RootElement.GetProperty("resource"), invitee);
    }

    public async Task CancelEventAsync(string eventUri, string? reason, CancellationToken ct)
    {
        var uuid = eventUri.TrimEnd('/').Split('/').Last();
        using var response = await SendAsync(HttpMethod.Post, $"/scheduled_events/{uuid}/cancellation",
            string.IsNullOrWhiteSpace(reason) ? new { } : new { reason }, ct);
    }

    public async Task<IReadOnlyList<CalendlyInvitee>> GetScheduledInviteesAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var organization = string.IsNullOrWhiteSpace(_options.OrganizationUri)
            ? (await GetCurrentUserAsync(ct)).OrganizationUri : _options.OrganizationUri;
        var url = $"/scheduled_events?organization={Uri.EscapeDataString(organization)}&min_start_time={Uri.EscapeDataString(Utc(fromUtc))}&max_start_time={Uri.EscapeDataString(Utc(toUtc))}&count=100";
        var events = await GetAllAsync(url, ct);
        var result = new List<CalendlyInvitee>();
        foreach (var evt in events)
        {
            var eventUri = evt.GetProperty("uri").GetString()!;
            var uuid = eventUri.TrimEnd('/').Split('/').Last();
            var invitees = await GetAllAsync($"/scheduled_events/{uuid}/invitees?count=100", ct);
            foreach (var invitee in invitees)
                result.Add(ParseInvitee(evt, invitee));
        }
        return result;
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, ct);
        return await ReadAsync(response, ct);
    }

    private async Task<IReadOnlyList<JsonElement>> GetAllAsync(string path, CancellationToken ct)
    {
        var items = new List<JsonElement>();
        string? next = path;
        while (next is not null)
        {
            using var doc = await GetAsync(next, ct);
            items.AddRange(doc.RootElement.GetProperty("collection").EnumerateArray().Select(x => x.Clone()));
            next = null;
            if (doc.RootElement.TryGetProperty("pagination", out var pagination) &&
                pagination.TryGetProperty("next_page_token", out var token) && token.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(token.GetString()))
            {
                var separator = path.Contains('?') ? '&' : '?';
                next = $"{path}{separator}page_token={Uri.EscapeDataString(token.GetString()!)}";
            }
        }
        return items;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        if (!_options.Configured) throw new CalendlyApiException(CalendlyFailureKind.Unauthorized, "Calendly is not configured.");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        try
        {
            var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                int? retryAfter = response.Headers.RetryAfter?.Delta is { } delta ? (int)Math.Ceiling(delta.TotalSeconds) : null;
                var kind = response.StatusCode switch
                {
                    HttpStatusCode.TooManyRequests => CalendlyFailureKind.RateLimited,
                    HttpStatusCode.Unauthorized => CalendlyFailureKind.Unauthorized,
                    HttpStatusCode.Forbidden => CalendlyFailureKind.Forbidden,
                    HttpStatusCode.NotFound => CalendlyFailureKind.NotFound,
                    HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => CalendlyFailureKind.Validation,
                    _ when (int)response.StatusCode >= 500 => CalendlyFailureKind.Unavailable,
                    _ => CalendlyFailureKind.Unavailable
                };
                response.Dispose();
                throw new CalendlyApiException(kind, "Calendly rejected the request.", retryAfter);
            }
            return response;
        }
        catch (HttpRequestException ex) { throw new CalendlyApiException(CalendlyFailureKind.Unavailable, "Calendly could not be reached.", inner: ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new CalendlyApiException(CalendlyFailureKind.Unavailable, "Calendly timed out.", inner: ex); }
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct); }
        catch (JsonException ex) { throw new CalendlyApiException(CalendlyFailureKind.MalformedResponse, "Calendly returned malformed data.", inner: ex); }
    }

    private static CalendlyInvitee ParseInvitee(JsonElement e, JsonElement i)
    {
        var location = e.TryGetProperty("location", out var l) && l.ValueKind == JsonValueKind.Object ? String(l, "location") ?? String(l, "type") : null;
        return new(i.GetProperty("uri").GetString()!, e.GetProperty("uri").GetString()!, e.GetProperty("event_type").GetString()!,
            i.GetProperty("email").GetString()!, i.GetProperty("name").GetString()!, e.GetProperty("start_time").GetDateTime().ToUniversalTime(),
            e.GetProperty("end_time").GetDateTime().ToUniversalTime(), i.GetProperty("status").GetString()!, location,
            location?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true ? location : null,
            String(i, "cancel_url"), String(i, "reschedule_url"), Bool(i, "rescheduled"), String(i, "old_invitee"), String(i, "new_invitee"));
    }
    private static CalendlyEventType ParseEventType(JsonElement x)
    {
        var locations = x.TryGetProperty("locations", out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Select(l => new CalendlyLocationOption(
                String(l, "kind") ?? String(l, "type") ?? "unknown", String(l, "location"))).ToArray()
            : [];
        return new CalendlyEventType(x.GetProperty("uri").GetString()!, x.GetProperty("name").GetString()!,
            x.GetProperty("duration").GetInt32(), x.GetProperty("active").GetBoolean(),
            x.GetProperty("scheduling_url").GetString()!, locations);
    }
    private static string? String(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False && p.GetBoolean();
    private static string Utc(DateTime value) => value.ToUniversalTime().ToString("O");
}
