namespace BeyondMovement.Modules.Identity.Services;

public sealed class GoogleOptions
{
    public const string SectionName = "Google";

    /// <summary>
    /// The OAuth client IDs, one per platform. These are public values by design
    /// (architecture section 12) — they are an audience to check, not a secret.
    /// </summary>
    public GoogleClientIds ClientId { get; set; } = new();

    public IEnumerable<string> AllClientIds =>
        new[] { ClientId.Web, ClientId.Android, ClientId.iOS }
            .Where(id => !string.IsNullOrWhiteSpace(id))!;
}

public sealed class GoogleClientIds
{
    public string? Web { get; set; }
    public string? Android { get; set; }
    public string? iOS { get; set; }
}
