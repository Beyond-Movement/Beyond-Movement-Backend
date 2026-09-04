using System.Security.Claims;
using BeyondMovement.Api.Packages;
using BeyondMovement.Modules.Athletes.Features;
using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Packages.Contracts;
using BeyondMovement.Modules.Packages.Features;
using BeyondMovement.SharedKernel;
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
        MapAthletePricing(admin);

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
            CatalogueReader reader,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (!principal.TryGetIdentity(out _, out var coachId))
                return Results.Unauthorized();

            // An empty list is a real answer here - most athletes have no overrides - so an
            // unknown athlete has to be told apart from one that simply has none set.
            if (!await reader.BelongsToCoachAsync(coachId, athleteId, ct))
                return PricingErrors.AthleteNotFound.ToProblem(http);

            return Results.Ok(await handler.ListForAthleteAsync(coachId, athleteId, ct));
        })
        .WithName("ListAthleteCustomPrices")
        .WithSummary("Every price override this athlete has.")
        .WithDescription(
            "Only the overrides, and an empty list is normal - most athletes have none. A package " +
            "option missing from this list is priced by loyalty or by its default; use the " +
            "catalogue preview to see what the athlete will actually pay. An unknown athlete, or " +
            "one belonging to another coach, returns 404 ATHLETE_NOT_FOUND rather than an empty " +
            "list, so a bad id cannot be mistaken for an athlete who simply has no overrides.")
        .Produces<IReadOnlyList<CustomPriceResponse>>()
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

    private static void MapSetCustomPrice(RouteGroupBuilder group) =>
        group.MapPut("/custom-prices/{packageOptionId:guid}", async (
            Guid athleteId,
            Guid packageOptionId,
            SetCustomPriceRequest request,
            IValidator<SetCustomPriceRequest> validator,
            CustomPriceHandler handler,
            CatalogueReader reader,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
                return validation.ToValidationProblem(http);

            if (!principal.TryGetIdentity(out _, out var coachId))
                return Results.Unauthorized();

            // The athlete id arrives in the URL, so it is checked before anything is written.
            // Without this an override could be attached to an id that is not this coach's
            // athlete, and nothing downstream would ever notice.
            if (!await reader.BelongsToCoachAsync(coachId, athleteId, ct))
                return PricingErrors.AthleteNotFound.ToProblem(http);

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
            CatalogueReader reader,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (!principal.TryGetIdentity(out _, out var coachId))
                return Results.Unauthorized();

            if (!await reader.BelongsToCoachAsync(coachId, athleteId, ct))
                return PricingErrors.AthleteNotFound.ToProblem(http);

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
            HttpContext http,
            CancellationToken ct) =>
        {
            if (!principal.TryGetIdentity(out _, out var coachId))
                return Results.Unauthorized();

            var catalogue = await reader.PreviewForCoachAsync(coachId, athleteId, ct);

            return catalogue is null
                ? PricingErrors.AthleteNotFound.ToProblem(http)
                : Results.Ok(catalogue);
        })
        .WithName("PreviewAthleteCatalogue")
        .WithSummary("What this athlete sees, priced exactly as they will see it.")
        .WithDescription(
            "The same calculation the athlete's own catalogue uses, so the coach can check a " +
            "price without reproducing the precedence rule on the client. An unknown athlete, or " +
            "one belonging to another coach, returns 404 ATHLETE_NOT_FOUND. An empty list means " +
            "the coach has no active package options, not that the athlete is unknown.")
        .Produces<IReadOnlyList<CatalogueItemResponse>>()
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

    private static void MapAthletePricing(RouteGroupBuilder group) =>
        group.MapGet("/pricing", async (
            Guid athleteId,
            CatalogueReader reader,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (!principal.TryGetIdentity(out _, out var coachId))
                return Results.Unauthorized();

            var pricing = await reader.AdminPricingAsync(coachId, athleteId, ct);

            return pricing is null
                ? PricingErrors.AthleteNotFound.ToProblem(http)
                : Results.Ok(pricing);
        })
        .WithName("GetAthletePricing")
        .WithSummary("The Admin pricing view: list price, this athlete's price, and why.")
        .WithDescription(
            "Everything the Athlete Pricing screen needs in ONE call - it replaces combining " +
            "/package-options, /custom-prices and /catalogue and inferring the rest on the " +
            "client, which would mean reproducing the pricing precedence in the app. " +
            "Each item carries defaultPriceMinor (the list price), effectivePriceMinor (what " +
            "this athlete pays) and pricingSource, which is Default, Loyalty or Custom. The price " +
            "and the source come from ONE server-side decision, so they can never disagree about " +
            "which rule applied. " +
            "PRECEDENCE, decided server-side and never on the client: a custom override wins " +
            "outright; otherwise loyalty applies; otherwise the default price stands. They do NOT " +
            "compound - a loyal athlete with an override pays the override exactly, undiscounted, " +
            "because an override is an agreed price rather than a starting point. " +
            "pricingSource is also what tells the screen which action applies: Custom means an " +
            "override exists and can be removed, Default and Loyalty mean there is none to " +
            "remove. " +
            "isLoyal is repeated here so the loyalty toggle and the prices it affects render " +
            "from one response. loyaltyDiscountPercent is null when the athlete is not loyal - " +
            "there is no percentage to state - and applies only to items whose pricingSource is " +
            "Loyalty. " +
            "ACTIVE options only, ordered by name to match GET /package-options; archived options " +
            "cannot be sold, so pricing them would price something nobody can buy. An empty items " +
            "list means the coach has no active package options, not that the athlete is unknown. " +
            "Prices are integer piastres - divide by 100 to display. " +
            "An unknown athlete, or one belonging to another coach, returns 404 ATHLETE_NOT_FOUND. " +
            "Setting and removing overrides is unchanged: PUT and DELETE " +
            "/athletes/{athleteId}/custom-prices/{packageOptionId}.")
        .Produces<AthletePricingResponse>()
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

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

internal static class PricingErrors
{
    /// <summary>
    /// 404 rather than 403, so the API never confirms that an athlete id it will not serve
    /// exists. The same code and message the athlete endpoints already return, so the client
    /// has one case to handle rather than one per area.
    /// </summary>
    public static readonly Error AthleteNotFound =
        new(ApiErrorCodes.AthleteNotFound, "No such athlete.", StatusCodes.Status404NotFound);
}

public sealed record LoyaltyResponse(Guid AthleteId, bool IsLoyal);
