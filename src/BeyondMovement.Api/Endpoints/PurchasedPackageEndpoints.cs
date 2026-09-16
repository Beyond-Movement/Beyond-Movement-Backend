using System.Security.Claims;
using BeyondMovement.Api.Packages;
using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Packages;
using BeyondMovement.Modules.Packages.Contracts;
using BeyondMovement.Modules.Packages.Domain;
using BeyondMovement.SharedKernel;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Api.Endpoints;

/// <summary>
/// Packages an athlete has bought — the purchase model the architecture deferred out of Phase 4
/// (section 14.3), built now because attendance has nothing to deduct from without it.
/// <para>
/// <c>athleteId</c> in these routes is the athlete's <b>user</b> id, matching every other
/// <c>/athletes/{athleteId}</c> route in this API. The package itself is keyed by profile id,
/// which is what <c>athleteProfileId</c> in the response carries and what the session endpoints
/// use.
/// </para>
/// <para>
/// <b>Not here, deliberately:</b> there is no payment status on a package and no endpoint to
/// edit its price. Payment is Phase 8, package payment status is derived from confirmed payments
/// that cannot exist yet, and which values it takes is open decision C-01.
/// </para>
/// </summary>
public static class PurchasedPackageEndpoints
{
    private const string ProblemJson = "application/problem+json";

    public static IEndpointRouteBuilder MapPurchasedPackageEndpoints(this IEndpointRouteBuilder app)
    {
        var athlete = app.MapGroup("/api/v1/athletes/{athleteId:guid}/packages")
            .WithTags("Packages")
            .RequireAuthorization("AdminOnly");

        athlete.MapPost(string.Empty, Purchase)
            .WithName("PurchasePackage")
            .WithSummary("Record that an athlete bought a package.")
            .WithDescription(
                "The price is not in the request and cannot be: it is computed server-side from " +
                "the option's default price, the athlete's loyalty flag and any price override " +
                "they have - the same rule and the same number the catalogue already showed them " +
                "- and then copied onto the package as paid. Repricing or archiving the option " +
                "afterwards never changes it. BR-03 allows one active package per athlete, so a " +
                "second purchase while one is active is 409 ACTIVE_PACKAGE_EXISTS; close the " +
                "current one first. An archived option cannot be sold (409 " +
                "PACKAGE_OPTION_ARCHIVED). startDate defaults to today in UTC, and endDate is " +
                "optional because a package normally ends when its sessions run out rather than " +
                "on a date.")
            .Produces<PurchasedPackageResponse>(StatusCodes.Status201Created)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, ProblemJson);

        athlete.MapGet(string.Empty, History)
            .WithName("ListAthletePackages")
            .WithSummary("A page of this athlete's packages, newest first.")
            .WithDescription(
                "Active, completed and closed together, because the screen showing history shows " +
                "all three. An athlete who has never bought one gets an empty page; an unknown " +
                "athlete, or one belonging to another coach, gets 404 ATHLETE_NOT_FOUND, so a bad " +
                "id cannot be mistaken for an athlete with no packages. " +
                "PAGED, in the same envelope as GET /api/v1/athletes and GET /api/v1/purchases: " +
                "items plus page, pageSize, totalCount, totalPages, hasNextPage and " +
                "hasPreviousPage. page starts at 1 and pageSize defaults to 20 and is capped at " +
                "100 - values outside the range are clamped rather than rejected. " +
                "Ordered newest first by createdAtUtc, with the id breaking ties so the order is " +
                "total and a package cannot appear on two pages. " +
                "The Athlete Profile shows the most recent PREVIOUS package by taking the first " +
                "item whose status is not Active: at most one package is Active at a time " +
                "(BR-03), and it is the newest, so it sorts first when it exists. Page 1 is " +
                "therefore enough for that card, and View All pages from here.")
            .Produces<PagedResult<PurchasedPackageResponse>>()
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

        athlete.MapGet("/active", Active)
            .WithName("GetAthleteActivePackage")
            .WithSummary("The athlete's current package and balance.")
            .WithDescription(
                "404 PACKAGE_NOT_FOUND when the athlete has none, which is a normal state - " +
                "between packages, or before the first one - and not an error to report as a " +
                "failure. This is the call behind the athlete list's Active/Inactive badge and " +
                "behind the balance on Session Details. remainingSessions can legitimately be 0: " +
                "the UI shows \"New sessions pending\" for that, but the number stays a number.")
            .Produces<PurchasedPackageResponse>()
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

        var packages = app.MapGroup("/api/v1/packages")
            .WithTags("Packages")
            .RequireAuthorization("AdminOnly");

        packages.MapGet("/{id:guid}", Detail)
            .WithName("GetPackage")
            .WithSummary("One package.")
            .WithDescription(
                "A package belonging to another coach is 404 PACKAGE_NOT_FOUND, the same as one " +
                "that does not exist, so an id cannot be probed for existence.")
            .Produces<PurchasedPackageResponse>()
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

        packages.MapPost("/{id:guid}/close", Close)
            .WithName("ClosePackage")
            .WithSummary("End a package early.")
            .WithDescription(
                "Ends it without deleting anything: the balance stays as it stands, so the " +
                "history still shows what was used. Closing is what frees the athlete to buy " +
                "another under BR-03. A package that ran out on its own is already Completed and " +
                "does not need closing. Closing an already-closed package is 409 " +
                "PACKAGE_ALREADY_CLOSED.")
            .Produces<PurchasedPackageResponse>()
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, ProblemJson);

        var mine = app.MapGroup("/api/v1/me")
            .WithTags("Packages")
            .RequireAuthorization("AthleteOnly");

        mine.MapGet("/packages", MyPackages)
            .WithName("ListMyPackages")
            .WithSummary("A page of the calling athlete's own packages, newest first.")
            .WithDescription(
                "The athlete's Package History. Always the caller's own - there is no athlete " +
                "id, and an athlete can never read another's history. " +
                "THE SAME RESPONSE AS THE ADMIN'S GET /api/v1/athletes/{athleteId}/packages, " +
                "item for item and field for field, so the two screens share one model: active, " +
                "completed and closed together, in the same paged envelope of items plus page, " +
                "pageSize, totalCount, totalPages, hasNextPage and hasPreviousPage. page starts " +
                "at 1 and pageSize defaults to 20 and is capped at 100 - values outside the " +
                "range are clamped rather than rejected. " +
                "Ordered newest first by createdAtUtc, with the id breaking ties so the order is " +
                "total and a package cannot appear on two pages. " +
                "An athlete who has never had a package gets an EMPTY PAGE, not a 404 - it is a " +
                "normal state and the screen should show its empty view. That differs from " +
                "GET /api/v1/me/package, which is the single ACTIVE package and is 404 when " +
                "there is none. " +
                "The most recent PREVIOUS package is the first item whose status is not Active: " +
                "at most one is Active at a time (BR-03) and it is the newest, so it sorts " +
                "first when it exists.")
            .Produces<PagedResult<PurchasedPackageResponse>>()
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson);

        mine.MapGet("/package", MyPackage)
            .WithName("GetMyPackage")
            .WithSummary("The calling athlete's own active package.")
            .WithDescription(
                "Always the caller's own - there is no athlete id, and an athlete can never read " +
                "another's package. 404 PACKAGE_NOT_FOUND when they have none, which includes an " +
                "athlete who has not completed their profile yet. " +
                "includedFeatures lists the recognised package features this package GRANTS, " +
                "frozen at purchase and OFTEN EMPTY - it is empty for every package bought " +
                "before the codes existed. Read it to show or hide a feature's action: an " +
                "athlete may file an Observation Request only while it contains Observations, " +
                "and POST /me/observation-requests answers 403 OBSERVATIONS_NOT_INCLUDED " +
                "otherwise, so hiding the action is UX and this is the rule. Editing the " +
                "catalogue option afterwards never changes it, and the DISPLAY TEXT of a feature " +
                "is never what decides - read the codes here, not the feature lines on the " +
                "purchase.")
            .Produces<PurchasedPackageResponse>()
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

        return app;
    }

    private static async Task<IResult> Purchase(
        Guid athleteId, PurchasePackageRequest request, IValidator<PurchasePackageRequest> validator,
        PackagePurchaseService service, ClaimsPrincipal principal, HttpContext http, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return validation.ToValidationProblem(http);

        if (!principal.TryGetIdentity(out var actorUserId, out var coachId)) return Results.Unauthorized();

        var result = await service.PurchaseAsync(coachId, athleteId, actorUserId, request, ct);

        return result.IsSuccess
            ? Results.Created($"/api/v1/packages/{result.Value.Id}", result.Value)
            : result.Error!.ToProblem(http);
    }

    private static async Task<IResult> History(
        Guid athleteId, AppDbContext db, CatalogueReader reader, ClaimsPrincipal principal,
        HttpContext http, CancellationToken ct,
        int page = 1, int pageSize = PagedResult<PurchasedPackageResponse>.DefaultPageSize)
    {
        if (!principal.TryGetIdentity(out _, out var coachId)) return Results.Unauthorized();

        // An empty list is a real answer, so an unknown athlete has to be told apart from one
        // who simply has no packages - the same reasoning as the custom-price list.
        if (!await reader.BelongsToCoachAsync(coachId, athleteId, ct))
            return PricingErrors.AthleteNotFound.ToProblem(http);

        return Results.Ok(await HistoryPageAsync(db, coachId, athleteId, page, pageSize, ct));
    }

    /// <summary>
    /// The calling athlete's own history. No ownership check and no id to check one against:
    /// both ids come from the token, so the query cannot be pointed at anyone else.
    /// <para>
    /// An athlete with no packages gets an empty page rather than a 404. The Admin route has to
    /// tell an unknown athlete apart from an empty one because the id is caller-supplied; here
    /// the athlete demonstrably exists — they are holding the token.
    /// </para>
    /// </summary>
    private static async Task<IResult> MyPackages(
        AppDbContext db, ClaimsPrincipal principal, HttpContext http, CancellationToken ct,
        int page = 1, int pageSize = PagedResult<PurchasedPackageResponse>.DefaultPageSize)
    {
        if (!principal.TryGetIdentity(out var userId, out var coachId)) return Results.Unauthorized();

        return Results.Ok(await HistoryPageAsync(db, coachId, userId, page, pageSize, ct));
    }

    /// <summary>
    /// One page of an athlete's packages. Shared by the Admin's history and the athlete's own so
    /// the two cannot drift: the mobile app reuses one Package History screen for both, and a
    /// difference in ordering or paging here would show up as the same list disagreeing with
    /// itself depending on who opened it.
    /// </summary>
    private static async Task<PagedResult<PurchasedPackageResponse>> HistoryPageAsync(
        AppDbContext db, Guid coachId, Guid athleteUserId, int page, int pageSize,
        CancellationToken ct)
    {
        var (normalizedPage, normalizedSize) =
            PagedResult<PurchasedPackageResponse>.Normalize(page, pageSize);

        var query = Owned(db, coachId)
            .Where(x => db.AthleteProfiles.Any(p => p.Id == x.AthleteProfileId && p.UserId == athleteUserId));

        var total = await query.CountAsync(ct);

        // Id breaks the tie on CreatedAtUtc so the order is total. Without it two packages
        // created in the same millisecond could swap places between requests, and offset paging
        // would show one of them twice and the other never.
        var packages = await query
            .OrderByDescending(x => x.CreatedAtUtc)
            .ThenByDescending(x => x.Id)
            .Skip((normalizedPage - 1) * normalizedSize)
            .Take(normalizedSize)
            .ToListAsync(ct);

        return new PagedResult<PurchasedPackageResponse>(
            [.. packages.Select(x => x.ToResponse())], normalizedPage, normalizedSize, total);
    }

    private static async Task<IResult> Active(
        Guid athleteId, AppDbContext db, ClaimsPrincipal principal, HttpContext http, CancellationToken ct)
    {
        if (!principal.TryGetIdentity(out _, out var coachId)) return Results.Unauthorized();

        var package = await Owned(db, coachId)
            .Where(x => x.Status == PurchasedPackageStatus.Active
                        && db.AthleteProfiles.Any(p => p.Id == x.AthleteProfileId && p.UserId == athleteId))
            .FirstOrDefaultAsync(ct);

        return package is null
            ? PackageErrors.PackageNotFound.ToProblem(http)
            : Results.Ok(package.ToResponse());
    }

    private static async Task<IResult> Detail(
        Guid id, AppDbContext db, ClaimsPrincipal principal, HttpContext http, CancellationToken ct)
    {
        if (!principal.TryGetIdentity(out _, out var coachId)) return Results.Unauthorized();

        var package = await Owned(db, coachId).FirstOrDefaultAsync(x => x.Id == id, ct);

        return package is null
            ? PackageErrors.PackageNotFound.ToProblem(http)
            : Results.Ok(package.ToResponse());
    }

    private static async Task<IResult> Close(
        Guid id, PackagePurchaseService service, ClaimsPrincipal principal, HttpContext http, CancellationToken ct)
    {
        if (!principal.TryGetIdentity(out var actorUserId, out var coachId)) return Results.Unauthorized();

        var result = await service.CloseAsync(coachId, id, actorUserId, ct);

        return result.IsSuccess ? Results.Ok(result.Value) : result.Error!.ToProblem(http);
    }

    private static async Task<IResult> MyPackage(
        AppDbContext db, ClaimsPrincipal principal, HttpContext http, CancellationToken ct)
    {
        if (!principal.TryGetIdentity(out var userId, out var coachId)) return Results.Unauthorized();

        var package = await Owned(db, coachId)
            .Where(x => x.Status == PurchasedPackageStatus.Active
                        && db.AthleteProfiles.Any(p => p.Id == x.AthleteProfileId && p.UserId == userId))
            .FirstOrDefaultAsync(ct);

        return package is null
            ? PackageErrors.PackageNotFound.ToProblem(http)
            : Results.Ok(package.ToResponse());
    }

    /// <summary>
    /// Every read starts here. Scoping to the coach from the token rather than from the route is
    /// what makes another coach's package a 404 instead of a leak.
    /// </summary>
    private static IQueryable<PurchasedPackage> Owned(AppDbContext db, Guid coachId) =>
        db.PurchasedPackages.AsNoTracking().Where(x => x.CoachId == coachId);
}
