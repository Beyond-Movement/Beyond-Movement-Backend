namespace BeyondMovement.Modules.Identity.Services;

/// <summary>
/// The branding the templates need, resolved from configuration. Anything left empty is
/// omitted from the email rather than filled with a placeholder — a fake postal address or a
/// button pointing at a domain that does not resolve is worse than no address and no button.
/// </summary>
public sealed record EmailBranding(
    string? LogoUrl = null,
    string? PostalAddress = null)
{
    public static readonly EmailBranding None = new();
}

/// <summary>Bound from the "Email" configuration section.</summary>
public sealed class EmailBrandingOptions
{
    public const string SectionName = "Email";

    /// <summary>
    /// Absolute HTTPS address of the logo, or empty to use the wordmark set as type.
    /// <para>
    /// It must be reachable from the public internet: mail clients fetch it when the message
    /// is opened, and Gmail fetches it through its own proxy. A <c>data:</c> URI is not a
    /// workaround — Gmail strips those — and a localhost address resolves to nothing on the
    /// recipient's device.
    /// </para>
    /// </summary>
    public string? LogoUrl { get; set; }

    /// <summary>
    /// A real postal address for the footer. Left empty until the business has one; an invented
    /// address in a footer is a false statement about a real organisation.
    /// </summary>
    public string? PostalAddress { get; set; }

    public EmailBranding ToBranding() => new(LogoUrl, PostalAddress);
}
