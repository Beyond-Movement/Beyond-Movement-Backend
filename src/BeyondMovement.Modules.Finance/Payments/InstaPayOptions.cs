namespace BeyondMovement.Modules.Finance.Payments;

/// <summary>
/// The coach's InstaPay destination, supplied entirely by configuration.
/// <para>
/// None of this is hard-coded and none of it ships in the repository. The QR image and the
/// payment link are the coach's own, they may change without a release, and a payment
/// destination baked into a binary is a payment destination that cannot be corrected. Bind from
/// <c>Payments:InstaPay</c>; in production supply <c>Payments__InstaPay__PaymentUrl</c> and the
/// rest as environment variables.
/// </para>
/// <para>
/// The platform never proxies InstaPay, never sees a transaction, and never verifies one
/// automatically (BR-14). It hands the athlete a destination and waits for the Admin to say the
/// money arrived — which is why this type holds display values only, and no credentials.
/// </para>
/// </summary>
public sealed class InstaPayOptions
{
    public const string SectionName = "Payments:InstaPay";

    /// <summary>
    /// Absolute URL of the QR-code image. May point at a file served from this API's
    /// <c>wwwroot</c>, exactly as the email logo does, or at any other public HTTPS address.
    /// </summary>
    public string QrImageUrl { get; set; } = string.Empty;

    /// <summary>The InstaPay destination the app opens when the athlete taps Pay.</summary>
    public string PaymentUrl { get; set; } = string.Empty;

    /// <summary>Who the athlete is paying — shown so they can check before sending money.</summary>
    public string RecipientName { get; set; } = string.Empty;

    /// <summary>The InstaPay address or mobile number, for an athlete paying by hand.</summary>
    public string RecipientHandle { get; set; } = string.Empty;

    /// <summary>Ordered steps shown beside the QR code.</summary>
    public string[] Instructions { get; set; } = [];

    /// <summary>
    /// At least one way to actually pay must be present. Recipient details and instructions are
    /// useful context but cannot stand on their own: an athlete cannot pay a paragraph of text.
    /// Until this is true the endpoint answers 503 INSTAPAY_NOT_CONFIGURED.
    /// </summary>
    public bool Configured =>
        !string.IsNullOrWhiteSpace(PaymentUrl) || !string.IsNullOrWhiteSpace(QrImageUrl);
}
