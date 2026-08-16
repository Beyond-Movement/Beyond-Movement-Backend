namespace BeyondMovement.Modules.Identity.Services;

/// <summary>
/// One outbound email. Both bodies are always supplied: mail clients that refuse HTML, and
/// screen readers, fall back to <paramref name="TextBody"/>, and a message with no plain-text
/// part scores worse with spam filters.
/// </summary>
public sealed record EmailMessage(string To, string Subject, string HtmlBody, string TextBody);

/// <summary>
/// Outbound email. Implemented in Infrastructure — a real provider in deployment, a console
/// stub locally — so handlers never know which is in use.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}
