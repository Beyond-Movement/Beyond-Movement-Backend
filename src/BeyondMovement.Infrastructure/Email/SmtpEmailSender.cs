using System.Net;
using System.Net.Mail;
using BeyondMovement.Modules.Identity.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeyondMovement.Infrastructure.Email;

/// <summary>
/// Sends over plain SMTP. Its purpose is local development against a mail catcher such as
/// Mailpit, where messages can be opened and their links clicked without a provider account,
/// a verified domain, or any risk of reaching a real person.
/// <para>
/// Production uses <see cref="PostmarkEmailSender"/>: deliverability of an invitation is the
/// difference between an athlete joining and not (BR-01), and that needs a real provider.
/// </para>
/// </summary>
public sealed class SmtpEmailSender(
    IOptions<EmailOptions> options,
    ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private readonly EmailOptions _options = options.Value;

    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var smtp = _options.Smtp;

        using var client = new SmtpClient(smtp.Host, smtp.Port) { EnableSsl = smtp.UseSsl };

        if (!string.IsNullOrWhiteSpace(smtp.Username))
            client.Credentials = new NetworkCredential(smtp.Username, smtp.Password);

        // A blank FromName sends as the bare address. Worth having: a brand display name on a
        // free-provider address is a phishing shape, and some filters score it accordingly.
        var from = string.IsNullOrWhiteSpace(_options.FromName)
            ? new MailAddress(_options.FromAddress)
            : new MailAddress(_options.FromAddress, _options.FromName);

        using var mail = new MailMessage
        {
            From = from,
            Subject = message.Subject,
            // The plain-text part is the body; the HTML rides along as an alternate view, so a
            // client that refuses HTML still shows the invitation code.
            Body = message.TextBody,
            IsBodyHtml = false
        };

        mail.To.Add(message.To);

        // A reachable Reply-To is one of the few legitimacy signals available without a domain
        // of our own. Mail from a no-reply address that accepts no reply scores worse.
        mail.ReplyToList.Add(new MailAddress(_options.ReplyToAddress ?? _options.FromAddress));

        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
            message.HtmlBody, null, "text/html"));

        try
        {
            await client.SendMailAsync(mail, ct);
            logger.LogInformation("Email sent over SMTP to {Host}:{Port}: {Subject}",
                smtp.Host, smtp.Port, message.Subject);
        }
        catch (SmtpException ex)
        {
            logger.LogError(ex, "SMTP send failed to {Host}:{Port}", smtp.Host, smtp.Port);

            throw new InvalidOperationException(
                $"Sending email over SMTP failed ({smtp.Host}:{smtp.Port}). " +
                "Is the mail container running? Try: docker compose up -d mailpit", ex);
        }
    }
}
