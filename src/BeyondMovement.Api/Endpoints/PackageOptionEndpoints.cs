using System.Security.Claims;
using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Packages.Contracts;
using BeyondMovement.Modules.Packages.Features;
using FluentValidation;

namespace BeyondMovement.Api.Endpoints;

/// <summary>
/// The Admin's package-option catalogue. Purchasing, payment and remaining sessions are a later
/// phase and deliberately absent here — these endpoints describe what the coach sells, never
/// what an athlete has bought.
/// </summary>
public static class PackageOptionEndpoints
{
    private const string ProblemJson = "application/problem+json";

    public static IEndpointRouteBuilder MapPackageOptionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/package-options")
            .WithTags("Package options")
            .RequireAuthorization("AdminOnly");

        MapList(group);
        MapGetOne(group);
        MapCreate(group);
        MapEdit(group);
        MapArchive(group);
        MapRestore(group);

        return app;
    }

    private static void MapList(RouteGroupBuilder group) =>
        group.MapGet(string.Empty, async (
            PackageOptionHandler handler,
            ClaimsPrincipal principal,
            bool archived = false,
            CancellationToken ct = default) =>
        {
            if (!principal.TryGetIdentity(out _, out var coachId))
                return Results.Unauthorized();

            return Results.Ok(await handler.ListAsync(coachId, archived, ct));
        })
        .WithName("ListPackageOptions")
        .WithSummary("The coach's package options, active by default.")
        .WithDescription(
            "Pass archived=true for the archive instead. The two lists are separate requests " +
            "rather than one list the client filters, because the Admin screens show them " +
            "separately and an athlete must never receive an archived option at all. Ordered by " +
            "name. Prices are integer piastres - see defaultPriceMinor.")
        .Produces<IReadOnlyList<PackageOptionResponse>>()
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson);

    private static void MapGetOne(RouteGroupBuilder group) =>
        group.MapGet("/{id:guid}", async (
            Guid id,
            PackageOptionHandler handler,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (!principal.TryGetIdentity(out _, out var coachId))
                return Results.Unauthorized();

            var result = await handler.GetAsync(coachId, id, ct);

            return result.IsSuccess ? Results.Ok(result.Value) : result.Error!.ToProblem(http);
        })
        .WithName("GetPackageOption")
        .WithSummary("One package option, archived or not.")
        .WithDescription(
            "An id belonging to another coach returns 404 PACKAGE_OPTION_NOT_FOUND rather than " +
            "403, so the API never confirms that an id it will not serve exists. Use the " +
            "returned version when editing, archiving or restoring.")
        .Produces<PackageOptionResponse>()
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

    private static void MapCreate(RouteGroupBuilder group) =>
        group.MapPost(string.Empty, async (
            SavePackageOptionRequest request,
            IValidator<SavePackageOptionRequest> validator,
            PackageOptionHandler handler,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
                return validation.ToValidationProblem(http);

            if (!principal.TryGetIdentity(out _, out var coachId))
                return Results.Unauthorized();

            var result = await handler.CreateAsync(coachId, request, ct);

            return result.IsSuccess
                ? Results.Created($"/api/v1/package-options/{result.Value.Id}", result.Value)
                : result.Error!.ToProblem(http);
        })
        .WithName("CreatePackageOption")
        .WithSummary("Add a package option to the catalogue.")
        .WithDescription(
            "name is trimmed and must be unique among ACTIVE options, case-insensitively - a " +
            "name freed up by archiving may be reused. sessions is 1-1000. defaultPriceMinor is " +
            "a non-negative integer number of piastres, 100 to the EGP, never a decimal. " +
            "features is 1-10 non-blank strings of at most 100 characters each, and the order " +
            "sent is the order stored and returned. A duplicate name returns 409 " +
            "PACKAGE_NAME_CONFLICT; anything else invalid returns 400 VALIDATION_FAILED with " +
            "per-field detail in errors.")
        .Produces<PackageOptionResponse>(StatusCodes.Status201Created)
        .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, ProblemJson);

    private static void MapEdit(RouteGroupBuilder group) =>
        group.MapPut("/{id:guid}", async (
            Guid id,
            EditPackageOptionRequest request,
            IValidator<EditPackageOptionRequest> validator,
            PackageOptionHandler handler,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
                return validation.ToValidationProblem(http);

            if (!principal.TryGetIdentity(out _, out var coachId))
                return Results.Unauthorized();

            var result = await handler.EditAsync(coachId, id, request, ct);

            return result.IsSuccess ? Results.Ok(result.Value) : result.Error!.ToProblem(http);
        })
        .WithName("EditPackageOption")
        .WithSummary("Replace a package option's details.")
        .WithDescription(
            "Every field is replaced, including the whole feature list in the order sent. There " +
            "is no partial update: a package option is read as one card, and editing it field by " +
            "field could leave it half-changed. Send the version you last read - a stale version " +
            "returns 409 CONCURRENCY_CONFLICT rather than overwriting another device's change, " +
            "and the version increases on every successful change. An archived option cannot be " +
            "edited and returns 409 PACKAGE_OPTION_ARCHIVED; restore it first.")
        .Produces<PackageOptionResponse>()
        .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, ProblemJson);

    private static void MapArchive(RouteGroupBuilder group) =>
        group.MapPost("/{id:guid}/archive", async (
            Guid id,
            PackageOptionVersionRequest request,
            IValidator<PackageOptionVersionRequest> validator,
            PackageOptionHandler handler,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
                return validation.ToValidationProblem(http);

            if (!principal.TryGetIdentity(out _, out var coachId))
                return Results.Unauthorized();

            var result = await handler.ArchiveAsync(coachId, id, request.Version, ct);

            return result.IsSuccess ? Results.Ok(result.Value) : result.Error!.ToProblem(http);
        })
        .WithName("ArchivePackageOption")
        .WithSummary("Withdraw a package option from the athlete catalogue.")
        .WithDescription(
            "Package options are never deleted. Archiving hides the option from athletes, leaves " +
            "it visible to the Admin under archived=true, and does not touch anything an athlete " +
            "has already bought - a price withdrawn today is still the price somebody paid last " +
            "week. Archiving an already-archived option returns 409 PACKAGE_OPTION_ARCHIVED.")
        .Produces<PackageOptionResponse>()
        .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, ProblemJson);

    private static void MapRestore(RouteGroupBuilder group) =>
        group.MapPost("/{id:guid}/restore", async (
            Guid id,
            PackageOptionVersionRequest request,
            IValidator<PackageOptionVersionRequest> validator,
            PackageOptionHandler handler,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
                return validation.ToValidationProblem(http);

            if (!principal.TryGetIdentity(out _, out var coachId))
                return Results.Unauthorized();

            var result = await handler.RestoreAsync(coachId, id, request.Version, ct);

            return result.IsSuccess ? Results.Ok(result.Value) : result.Error!.ToProblem(http);
        })
        .WithName("RestorePackageOption")
        .WithSummary("Return an archived package option to the athlete catalogue.")
        .WithDescription(
            "Restoring an option that is not archived returns 409 PACKAGE_OPTION_NOT_ARCHIVED. " +
            "The unique-name rule covers active options only, so if another option has taken the " +
            "name while this one was archived, restoring still succeeds and both names stand - " +
            "refusing would leave the coach unable to recover the option at all. Rename one " +
            "afterwards.")
        .Produces<PackageOptionResponse>()
        .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, ProblemJson);
}
