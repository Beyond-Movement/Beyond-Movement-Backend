using System.Security.Claims;
using BeyondMovement.Api.Packages;
using BeyondMovement.Modules.Athletes.Features;
using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Packages.Contracts;
using BeyondMovement.Modules.Packages.Features;
using FluentValidation;

namespace BeyondMovement.Api.Endpoints;

/// <summary>
/// Loyalty, per-athlete price overrides, and the athlete's own catalogue.
/// <para>
/// The effective price is calculated here and nowhere else. The mobile app is told the final
/// number and deliberately not told which rule produced it — an athlete is shown a price, not
/// the coach's pricing policy.
/// </para>
/// </summary>
public static class PricingEndpoints
{
    private const string ProblemJson = "application/problem+json";

    public static IEndpointRouteBuilder MapPricingEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1/athletes/{athleteId:guid}")
            .WithTags("Pricing")
            .RequireAuthorization("AdminOnly");

        MapSetLoyalty(admin);
        MapListCustomPrices(admin);
        MapSetCustomPrice(admin);
        MapRemoveCustomPrice(admin);
        MapPreviewCatalogue(admin);

        MapAthleteCatalogue(app);

        return app;
    }

    private static void MapSetLoyalty(RouteGroupBuilder group) =>
        group.MapPut("/loyalty", async (
            Guid athleteId,
            SetLoyaltyRequest request,
            SetLoyaltyHandler handler,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (!principal.TryGetIdentity(out _, out var coachId))
                return Results.Unauthorized();

            var result = await handler.HandleAsync(coachId, athleteId, request.IsLoyal, ct);

            return result.IsSuccess
                ? Results.Ok(new LoyaltyResponse(athleteId, result.Value))
                : result.Error!.ToProblem(http);
        })
        .WithName("SetAthleteLoyalty")
        .WithSummary("Mark an athlete as loyal, or remove it.")
        .WithDescription(
            "Loyalty is athlete-level, not per package: a loyal athlete gets 15% off every " +
            "package's default price. It is idempotent - marking an already-loyal athlete loyal " +
            "changes nothing and does not reset how long they have been loyal. isLoyal also " +
            "appears on the athlete list and the athlete detail. An athlete belonging to another " +
            "coach returns 404 ATHLETE_NOT_FOUND rather than 403.")
        .Produces<LoyaltyResponse>()
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

    private static void MapListCustomPrices(RouteGroupBuilder group) =>
        group.MapGet("/custom-prices", async (
            Guid athleteId,
            CustomPriceHandler handler,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!principal.TryGetIdentity(out _, out var coachId))
                return Results.Unauthorized();

            return Results.Ok(await handler.ListForAthleteAsync(coachId, athleteId, ct));
        })
        .WithName("ListAthleteCustomPrices")
        .WithSummary("Every price override this athlete has.")
        .WithDescription(
            "Only the overrides. A package option missing from this list is priced by loyalty or " +
            "by its default - use the catalogue preview to see what the athlete will actually pay.")
        .Produces<IReadOnlyList<CustomPriceResponse>>()
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson);

    private static void MapSetCustomPrice(RouteGroupBuilder group) =>
        group.MapPut("/custom-prices/{packageOptionId:guid}", async (
            Guid athleteId,
            Guid packageOptionId,
            SetCustomPriceRequest request,
            IValidator<SetCustomPriceRequest> validator,
            CustomPriceHandler handler,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
                return validation.ToValidationProblem(http);

            if (!principal.TryGetIdentity(out _, out var coachId))
                return Results.Unauthorized();

            var result = await handler.SetAsync(coachId, athleteId, packageOptionId, request.PriceMinor, ct);

            return result.IsSuccess ? Results.Ok(result.Value) : result.Error!.ToProblem(http);
        })
        .WithName("SetAthleteCustomPrice")
        .WithSummary("Set this athlete's price for one package option.")
        .WithDescription(
            "PUT, because there is at most one override per athlete and package option: calling " +
            "it again moves the price rather than adding a second. priceMinor is a non-negative " +
            "integer number of piastres and is stored exactly as sent - never rounded, and never " +
            "discounted further, even for a loyal athlete. An override is an agreed price, not a " +
            "starting point, so it beats loyalty outright.")
        .Produces<CustomPriceResponse>()
        .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

    private static void MapRemoveCustomPrice(RouteGroupBuilder group) =>
        group.MapDelete("/custom-prices/{packageOptionId:guid}", async (
            Guid athleteId,
            Guid packageOptionId,
            CustomPriceHandler handler,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (!principal.TryGetIdentity(out _, out var coachId))
                return Results.Unauthorized();

            var result = await handler.RemoveAsync(coachId, athleteId, packageOptionId, ct);

            return result.IsSuccess ? Results.NoContent() : result.Error!.ToProblem(http);
        })
        .WithName("RemoveAthleteCustomPrice")
        .WithSummary("Drop this athlete's price override.")
        .WithDescription(
            "Normal pricing resumes immediately: loyalty if the athlete is loyal, otherwise the " +
            "package's default price. Removing an override that does not exist returns 404 " +
            "CUSTOM_PRICE_NOT_FOUND.")
        .Produces(StatusCodes.Status204NoContent)
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

    private static void MapPreviewCatalogue(RouteGroupBuilder group) =>
        group.MapGet("/catalogue", async (
            Guid athleteId,
            CatalogueReader reader,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!principal.TryGetIdentity(out _, out var coachId))
                return Results.Unauthorized();

            return Results.Ok(await reader.PreviewForCoachAsync(coachId, athleteId, ct));
        })
        .WithName("PreviewAthleteCatalogue")
        .WithSummary("What this athlete sees, priced exactly as they will see it.")
        .WithDescription(
            "The same calculation the athlete's own catalogue uses, so the coach can check a " +
            "price without reproducing the precedence rule on the client. Returns an empty list " +
            "for an athlete belonging to another coach rather than disclosing that the id exists.")
        .Produces<IReadOnlyList<CatalogueItemResponse>>()
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson);

    private static void MapAthleteCatalogue(IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/catalogue", async (
            CatalogueReader reader,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!principal.TryGetUserId(out var userId))
                return Results.Unauthorized();

            return Results.Ok(await reader.ForAthleteAsync(userId, ct));
        })
        .RequireAuthorization("AthleteOnly")
        .WithTags("Pricing")
        .WithName("AthleteCatalogue")
        .WithSummary("The packages this athlete can buy, at their price.")
        .WithDescription(
            "Athlete-only, and always the caller's own catalogue - the athlete id comes from the " +
            "token, never a parameter. Archived options are excluded. priceMinor is the FINAL " +
            "price for this athlete in piastres, already accounting for a custom price or " +
            "loyalty; there is deliberately no field saying which applied and no default price " +
            "to compare against, because the athlete is shown a price rather than the coach's " +
            "pricing policy. Ordered cheapest first. Do not reproduce the pricing rule on the " +
            "client - if this number is wrong, it is a backend bug.")
        .Produces<IReadOnlyList<CatalogueItemResponse>>()
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson);
}

/// <param name="IsLoyal">True to mark loyal, false to remove it.</param>
public sealed record SetLoyaltyRequest(bool IsLoyal);

public sealed record LoyaltyResponse(Guid AthleteId, bool IsLoyal);
