using System.Net;
using System.Text.Json;
using BeyondMovement.Infrastructure.Email;
using BeyondMovement.Modules.Identity.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BeyondMovement.UnitTests.Identity;

public class PostmarkEmailSenderTests
{
    /// <summary>Captures the outgoing request and answers with whatever the test wants.</summary>
    private sealed class StubHandler(HttpStatusCode status, string body = "{}") : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private static (PostmarkEmailSender sender, StubHandler handler) Create(
        HttpStatusCode status = HttpStatusCode.OK, string body = "{}")
    {
        var handler = new StubHandler(status, body);

        var options = Options.Create(new EmailOptions
        {
            FromAddress = "no-reply@beyondmovement.com",
            FromName = "Beyond Movement",
            Postmark = new PostmarkOptions { ServerToken = "test-token", MessageStream = "outbound" }
        });

        return (new PostmarkEmailSender(new HttpClient(handler), options, NullLogger<PostmarkEmailSender>.Instance),
                handler);
    }

    private static readonly EmailMessage Message =
        new("athlete@example.com", "Subject line", "<p>html</p>", "text");

    [Fact]
    public async Task It_posts_to_the_postmark_endpoint_with_the_server_token()
    {
        var (sender, handler) = Create();

        await sender.SendAsync(Message);

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal(PostmarkEmailSender.ApiEndpoint, handler.Request.RequestUri!.ToString());
        Assert.Equal("test-token", handler.Request.Headers.GetValues(PostmarkEmailSender.TokenHeader).Single());
    }

    [Fact]
    public async Task It_sends_both_bodies_and_the_transactional_stream()
    {
        var (sender, handler) = Create();

        await sender.SendAsync(Message);

        var payload = JsonSerializer.Deserialize<JsonElement>(handler.RequestBody!);

        Assert.Equal("athlete@example.com", payload.GetProperty("To").GetString());
        Assert.Equal("Subject line", payload.GetProperty("Subject").GetString());
        Assert.Equal("<p>html</p>", payload.GetProperty("HtmlBody").GetString());
        Assert.Equal("text", payload.GetProperty("TextBody").GetString());

        // Transactional mail on a broadcast stream harms deliverability for both.
        Assert.Equal("outbound", payload.GetProperty("MessageStream").GetString());
    }

    [Fact]
    public async Task It_formats_the_sender_as_name_and_address()
    {
        var (sender, handler) = Create();

        await sender.SendAsync(Message);

        var payload = JsonSerializer.Deserialize<JsonElement>(handler.RequestBody!);

        Assert.Equal("Beyond Movement <no-reply@beyondmovement.com>", payload.GetProperty("From").GetString());
    }

    [Fact]
    public async Task A_rejected_send_throws_and_carries_postmark_s_reason()
    {
        // 300 with ErrorCode 400 is how Postmark reports an unverified sender signature -
        // the single most common first-time failure.
        var (sender, _) = Create(
            HttpStatusCode.MultipleChoices,
            """{"ErrorCode":400,"Message":"Sender signature not confirmed"}""");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(Message));

        Assert.Contains("Sender signature not confirmed", exception.Message);
    }

    [Fact]
    public async Task An_unauthorised_token_throws_rather_than_failing_silently()
    {
        var (sender, _) = Create(HttpStatusCode.Unauthorized, """{"Message":"Invalid token"}""");

        // Swallowing this would leave invitations that were never delivered, with no signal.
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(Message));
    }
}
