using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeyondMovement.Modules.Identity.Domain;

/// <summary>
/// The shape of the <c>Users.UiPreferences</c> jsonb column.
/// <para>
/// A json document rather than a column per setting, because UI preferences accumulate and
/// each new one would otherwise be a migration. Unknown keys written by a newer build are
/// preserved on read-modify-write, so an older server does not silently drop them.
/// </para>
/// </summary>
public sealed record UiPreferencesDocument
{
    [JsonPropertyName("athleteListSort")]
    public AthleteListSort? AthleteListSort { get; init; }

    /// <summary>Anything this version does not know about, carried through untouched.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static UiPreferencesDocument Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new UiPreferencesDocument();

        try
        {
            return JsonSerializer.Deserialize<UiPreferencesDocument>(json, Options) ?? new UiPreferencesDocument();
        }
        catch (JsonException)
        {
            // Malformed json must not make the user unreadable. Preferences are cosmetic;
            // losing them is survivable, failing a login over them is not.
            return new UiPreferencesDocument();
        }
    }

    public static implicit operator string(UiPreferencesDocument document) =>
        JsonSerializer.Serialize(document, Options);
}
