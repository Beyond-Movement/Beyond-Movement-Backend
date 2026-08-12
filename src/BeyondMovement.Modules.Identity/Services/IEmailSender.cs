namespace BeyondMovement.Modules.Identity.Services;

/// <summary>
/// Outbound email. A console implementation is enough until phase 3 replaces it with a
/// real provider — the interface is what the handlers depend on, so that swap is local.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(string toEmail, string subject, string body, CancellationToken ct = default);
}
