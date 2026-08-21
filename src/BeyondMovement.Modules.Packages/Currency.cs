namespace BeyondMovement.Modules.Packages;

/// <summary>
/// The platform bills in Egyptian pounds only. It is returned on every price so the client
/// never has to assume, and so adding a second currency later is a value change rather than a
/// contract change.
/// </summary>
public static class Currency
{
    public const string Egp = "EGP";

    /// <summary>Piastres to the pound — what a "minor unit" means here.</summary>
    public const int MinorUnitsPerUnit = 100;
}
