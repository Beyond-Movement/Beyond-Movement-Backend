namespace BeyondMovement.SharedKernel;

/// <summary>
/// The platform bills in Egyptian pounds only. It is returned on every price so the client
/// never has to assume, and so adding a second currency later is a value change rather than a
/// contract change.
/// <para>
/// It lives in SharedKernel rather than in Packages because Packages, Finance and the Api all
/// price things, and a module may not reference another module (CLAUDE.md section 4). The same
/// reason <see cref="Gender"/> and <c>PackageFeature</c> are here. Moving it changed no contract:
/// the value on the wire is and was the string "EGP".
/// </para>
/// </summary>
public static class Currency
{
    public const string Egp = "EGP";

    /// <summary>Piastres to the pound — what a "minor unit" means here.</summary>
    public const int MinorUnitsPerUnit = 100;
}
