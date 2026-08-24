using BeyondMovement.Modules.Scheduling.Domain;

namespace BeyondMovement.Modules.Scheduling.Calendly;

public sealed class CalendlyOptions
{
    public const string SectionName = "Calendly";
    public string BaseUrl { get; set; } = "https://api.calendly.com";
    public string ApiToken { get; set; } = string.Empty;
    public string UserUri { get; set; } = string.Empty;
    public string OrganizationUri { get; set; } = string.Empty;
    public string[] WebhookSigningKeys { get; set; } = [];
    public int ReconciliationMinutes { get; set; } = 15;
    public List<CalendlyEventTypeMapping> EventTypes { get; set; } = [];
    public bool Configured => !string.IsNullOrWhiteSpace(ApiToken);

    public CalendlyEventTypeMapping? FindByUri(string uri) => EventTypes.SingleOrDefault(x =>
        string.Equals(x.Uri.TrimEnd('/'), uri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

    public CalendlyEventTypeMapping? FindById(string id) => EventTypes.SingleOrDefault(x =>
        string.Equals(x.Uri.TrimEnd('/').Split('/').Last(), id, StringComparison.OrdinalIgnoreCase));
}

public sealed class CalendlyEventTypeMapping
{
    public string Uri { get; set; } = string.Empty;
    public DeliveryType DeliveryType { get; set; }
}
