using BeyondMovement.Modules.Identity.Services;
using Microsoft.Extensions.Logging;

namespace BeyondMovement.Infrastructure.Email;

/// <summary>
/// Development stub. Phase 3 replaces this with a real transactional provider;
/// nothing outside this class changes when it does.
/// </summary>
public sealed class ConsoleEmailSender(ILogger<ConsoleEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string toEmail, string subject, string body, CancellationToken ct = default)
    {
        logger.LogInformation("EMAIL (not really sent)\n  To: {To}\n  Subject: {Subject}\n  {Body}",
            toEmail, subject, body);
        return Task.CompletedTask;
    }
}
