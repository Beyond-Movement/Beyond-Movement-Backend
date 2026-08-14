using System.Net.Http.Json;
using System.Text.Json.Serialization;
using BeyondMovement.Modules.Identity.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeyondMovement.Infrastructure.Email;

/// <summary>
/// Sends through Postmark's transactional API.
/// <para>
/// Postmark ships a .NET SDK, but sending is a single JSON POST, so this talks to the API
/// directly: one fewer dependency, and the request is trivial to assert in a test with a stub
/// handler.
/// </para>
/// </summary>
public sealed class PostmarkEmailSender(
    HttpClient http,
    IOptions<EmailOptions> options,
    ILogger<PostmarkEmailSender> logger) : IEmailSender
{
    public const string ApiEndpoint = "https://api.postmarkapp.com/email";
    public const string TokenHeader = "X-Postmark-Server-Token";

    private readonly EmailOptions _options = options.Value;

    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var payload = new PostmarkPayload
        {
            From = string.IsNullOrWhiteSpace(_options.FromName)
                ? _options.FromAddress
                : $"{_options.FromName} <{_options.FromAddress}>",
            To = message.To,
            Subject = message.Subject,
            HtmlBody = message.HtmlBody,
            TextBody = message.TextBody,
            MessageStream = _options.Postmark.MessageStream
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, ApiEndpoint)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add(TokenHeader, _options.Postmark.ServerToken);

        using var response = await http.SendAsync(request, ct);

        if (response.IsSuccessStatusCode)
        {
            // The recipient is never logged in full (CLAUDE.md section 7).
            logger.LogInformation("Email sent: {Subject}", message.Subject);
            return;
        }

        // Postmark answers failures with a JSON body naming the reason - an unverified sender
        // signature, an inactive recipient, a bad token. Surfacing it saves a long hunt.
        var detail = await SafeReadAsync(response, ct);

        logger.LogError(
            "Postmark rejected an email. Status {Status}. {Detail}",
            (int)response.StatusCode, detail);

        throw new InvalidOperationException(
            $"Sending email failed: Postmark returned {(int)response.StatusCode}. {detail}");
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            return $"(the error body could not be read: {ex.Message})";
        }
    }

    private sealed class PostmarkPayload
    {
        [JsonPropertyName("From")] public required string From { get; init; }
        [JsonPropertyName("To")] public required string To { get; init; }
        [JsonPropertyName("Subject")] public required string Subject { get; init; }
        [JsonPropertyName("HtmlBody")] public required string HtmlBody { get; init; }
        [JsonPropertyName("TextBody")] public required string TextBody { get; init; }
        [JsonPropertyName("MessageStream")] public required string MessageStream { get; init; }
    }
}
