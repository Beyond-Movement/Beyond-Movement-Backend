using BeyondMovement.Modules.Athletes.Features;
using BeyondMovement.Modules.Identity.Contracts;

namespace BeyondMovement.Api.Endpoints;

/// <summary>
/// The sports catalogue, read-only. It is reference data maintained by the application, so there
/// is no create, edit or delete — a new sport ships with a backend release.
/// </summary>
public static class SportEndpoints
{
    private const string ProblemJson = "application/problem+json";

    public static IEndpointRouteBuilder MapSportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/sports", async (SportCatalogueHandler handler, CancellationToken ct) =>
                Results.Ok(await handler.ListAsync(ct)))
            .WithTags("Sports")
            // Either role: the athlete picks from it on Complete and Edit Profile, and the Admin's
            // screens show the same names.
            .RequireAuthorization()
            .WithName("ListSports")
            .WithSummary("Every sport an athlete can pick, in display order.")
            .WithDescription(
                "What the sport dropdown on Complete Profile and Edit Profile lists. Any signed-in " +
                "user, Admin or Athlete. A plain array of { id, name }, not paged - the catalogue " +
                "is small. Already in display order: alphabetical, with Other always last, so " +
                "show it as it arrives rather than re-sorting it. Send the chosen id as sportId to " +
                "POST /athletes/me/profile. Other is an ordinary entry, for an athlete whose sport " +
                "is not listed yet; there is no free-text sport. The list is the same for every " +
                "user and changes only when a sport is added in a backend release, so it is safe " +
                "to cache for the session. Ids are stable across releases and environments.")
            .Produces<IReadOnlyList<SportResponse>>()
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson);

        return app;
    }
}
