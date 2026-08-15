namespace BeyondMovement.Infrastructure.Email;

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>
    /// Must be an address on a domain verified in Postmark, with SPF and DKIM records in place.
    /// Sending from an unverified domain is rejected outright, and even when accepted it lands
    /// in spam — which for an invitation means the athlete never joins (BR-01).
    /// </summary>
    public string FromAddress { get; set; } = "";

    /// <summary>
    /// The display name recipients see. Empty sends as the bare address.
    /// <para>
    /// Leave it empty while sending from a free provider such as Gmail. A brand display name
    /// over an @gmail.com address is the shape of phishing, and Gmail files it as spam — this
    /// was measured, not guessed: identical mail landed in spam with the name and in the inbox
    /// without it. Set the name once sending from a domain with DKIM, where it helps.
    /// </para>
    /// </summary>
    public string FromName { get; set; } = "";

    /// <summary>
    /// Where replies go. Defaults to the from-address. Worth setting to a monitored mailbox:
    /// mail that invites no reply and accepts none scores worse with spam filters.
    /// </summary>
    public string? ReplyToAddress { get; set; }

    public PostmarkOptions Postmark { get; set; } = new();

    public SmtpOptions Smtp { get; set; } = new();

    /// <summary>Postmark is usable: a token and a from-address are both present.</summary>
    public bool PostmarkConfigured =>
        !string.IsNullOrWhiteSpace(Postmark.ServerToken) && !string.IsNullOrWhiteSpace(FromAddress);

    /// <summary>
    /// SMTP is usable. Intended for a local mail catcher during development; production uses
    /// Postmark, which wins when both are configured.
    /// </summary>
    public bool SmtpConfigured =>
        !string.IsNullOrWhiteSpace(Smtp.Host) && !string.IsNullOrWhiteSpace(FromAddress);
}

public sealed class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 1025;
    public bool UseSsl { get; set; }

    /// <summary>Left empty for a local catcher, which accepts anonymous mail.</summary>
    public string? Username { get; set; }
    public string? Password { get; set; }
}

public sealed class PostmarkOptions
{
    /// <summary>Server API token — a secret. User secrets locally, secret store in deployment.</summary>
    public string ServerToken { get; set; } = "";

    /// <summary>
    /// Postmark separates transactional from bulk mail. Invitations and password resets are
    /// transactional; sending them on a broadcast stream harms deliverability for both.
    /// </summary>
    public string MessageStream { get; set; } = "outbound";
}
