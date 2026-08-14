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

    public string FromName { get; set; } = "Beyond Movement";

    public PostmarkOptions Postmark { get; set; } = new();

    /// <summary>
    /// True once Postmark can actually be used. When false the application falls back to the
    /// console stub rather than failing at startup, so a fresh clone runs with no account.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Postmark.ServerToken) && !string.IsNullOrWhiteSpace(FromAddress);
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
