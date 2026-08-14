using BeyondMovement.Modules.Identity.Services;
using Microsoft.Extensions.Logging;

namespace BeyondMovement.Infrastructure.Email;

/// <summary>
/// Development stub: prints the message instead of sending it, so invitation codes and reset
/// links can be read from the API console with no email account configured.
/// <para>
/// Selected automatically whenever Postmark is not configured. It prints the plain-text body
/// rather than the HTML, because a wall of markup in a terminal helps nobody.
/// </para>
/// </summary>
public sealed class ConsoleEmailSender(ILogger<ConsoleEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        logger.LogInformation(
            "EMAIL (not sent - no provider configured)\n  To: {To}\n  Subject: {Subject}\n\n{Body}\n",
            message.To, message.Subject, message.TextBody);

        return Task.CompletedTask;
    }
}
