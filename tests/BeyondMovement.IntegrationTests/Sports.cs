using BeyondMovement.Modules.Athletes.Domain;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// Catalogue ids by name, so a test can say <c>Sports.Id("Rowing")</c> and read as the sport it
/// means. The ids come from <see cref="SportCatalogue"/> — the same source the migration seeds
/// from — so a test can never name a sport the database does not have.
/// </summary>
internal static class Sports
{
    public static Guid Id(string name) => SportCatalogue.All.Single(s => s.Name == name).Id;
}
