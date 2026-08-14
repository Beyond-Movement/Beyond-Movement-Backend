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

/// <summary>Branding the templates need. Bound from the "Email" configuration section.</summary>
public sealed class EmailBrandingOptions
{
    public const string SectionName = "Email";

    /// <summary>
    /// Absolute HTTPS address of the logo, or empty to use the wordmark set as type.
    /// <para>
    /// It must be a real URL on a host that allows hotlinking: mail clients fetch it when the
    /// message is opened, and a <c>data:</c> URI is stripped by Gmail — the logo would then be
    /// missing for most recipients while looking correct in local testing.
    /// </para>
    /// </summary>
    public string? LogoUrl { get; set; }
}
