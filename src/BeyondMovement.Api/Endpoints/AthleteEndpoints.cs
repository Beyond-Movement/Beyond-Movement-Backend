using System.Security.Claims;
using BeyondMovement.Api.Athletes;
using BeyondMovement.Modules.Identity;
using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Identity.Features.AccountStatus;
using BeyondMovement.Modules.Identity.Persistence;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Api.Endpoints;

public static class AthleteEndpoints
{
    private const string ProblemJson = "application/problem+json";

    public static IEndpointRouteBuilder MapAthleteEndpoints(this IEndpointRouteBuilder app)
    {
        // Admin-only throughout. An athlete reaching these gets 403; a foreign athlete id
        // gets 404, because a 403 there would confirm the record exists.
        var group = app.MapGroup("/api/v1/athletes")
            .WithTags("Athletes")
            .RequireAuthorization("AdminOnly");

        MapList(group);
        MapDetail(group);
        MapPause(group);
        MapReactivate(group);

        return app;
    }

    private static void MapList(RouteGroupBuilder group) =>
        group.MapGet(string.Empty, async (
            AthleteDirectory directory,
            ClaimsPrincipal principal,
            string? search,
            // Defaults rather than nullables: an optional enum query parameter would otherwise
            // be generated as "enum or null", which is awkward to model on the client.
            AthleteStatusFilter status = AthleteStatusFilter.All,
            AthleteListSort sort = AthleteListSort.NameAsc,
            int page = 1,
            int pageSize = PagedResult<AthleteListItem>.DefaultPageSize,
            CancellationToken ct = default) =>
        {
            if (!principal.TryGetIdentity(out _, out var coachId))
                return Results.Unauthorized();

            var (normalizedPage, normalizedSize) = PagedResult<AthleteListItem>.Normalize(page, pageSize);

            var result = await directory.ListAsync(
                coachId, search, status, sort, normalizedPage, normalizedSize, ct);

            return Results.Ok(result);
        })
        .WithName("ListAthletes")
        .WithSummary("The coach's athletes, searchable, filterable and sorted.")
        .WithDescription(
            "search matches full name, email or sport, case-insensitively, on any part of the " +
            "value, and is trimmed before use. Email is included so an athlete who has not yet " +
            "completed their profile, and so has no name, is still findable. status filters by " +
            "ACCOUNT status - whether the athlete can sign in - and is not the same as having " +
            "an active package, which arrives in phase 4 as a separate parameter. sort accepts " +
            "NameAsc, NameDesc, Sport, NewestFirst and OldestFirst; Sport places athletes with " +
            "no sport last, and both name orders place athletes with no name last. page starts " +
            "at 1 and pageSize is capped at 100 - values outside the range are clamped rather " +
            "than rejected. Paused athletes are always included: pausing hides an athlete from " +
            "themselves, never from their coach. Athletes who have registered but not completed " +
            "their profile are also always included, with fullName null.")
        .Produces<PagedResult<AthleteListItem>>()
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson);

    private static void MapDetail(RouteGroupBuilder group) =>
        group.MapGet("/{id:guid}", async (
            Guid id,
            AthleteDirectory directory,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (!principal.TryGetIdentity(out _, out var coachId))
                return Results.Unauthorized();

            var athlete = await directory.GetAsync(coachId, id, ct);

            return athlete is null
                ? IdentityErrors.AthleteNotFound.ToProblem(http)
                : Results.Ok(athlete);
        })
        .WithName("GetAthlete")
        .WithSummary("One athlete, read-only.")
        .WithDescription(
            "Read-only in phase 2 - there is no endpoint to change an athlete's personal " +
            "details. phone is always null for now, because no screen collects one yet. " +
            "Profile photo is deferred until file storage exists. An unknown id, another " +
            "coach's athlete and a deleted athlete all return 404 ATHLETE_NOT_FOUND.")
        .Produces<AthleteDetail>()
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

    private static void MapPause(RouteGroupBuilder group) =>
        group.MapPost("/{id:guid}/pause", async (
            Guid id,
            SetAccountStatusHandler handler,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (!principal.TryGetIdentity(out var actorId, out var coachId))
                return Results.Unauthorized();

            var result = await handler.PauseAsync(coachId, id, actorId, ct);

            return result.IsSuccess
                ? Results.Ok(new AthleteStatusResponse(id, result.Value))
                : result.Error!.ToProblem(http);
        })
        .WithName("PauseAthlete")
        .WithSummary("Suspend an athlete's access.")
        .WithDescription(
            "Sets account status to Paused and revokes every refresh token the athlete holds, so " +
            "the session cannot be renewed. Their current access token stays valid for its " +
            "remaining minutes, but each request re-checks status, so the next call returns 403 " +
            "ACCOUNT_PAUSED - login does the same. The athlete stays visible to the coach and " +
            "their data is untouched. Pausing an already-paused athlete succeeds and changes " +
            "nothing, so a retry is safe. Push device tokens are NOT yet revoked; that table " +
            "arrives with notifications in phase 10.")
        .Produces<AthleteStatusResponse>()
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

    private static void MapReactivate(RouteGroupBuilder group) =>
        group.MapPost("/{id:guid}/reactivate", async (
            Guid id,
            SetAccountStatusHandler handler,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (!principal.TryGetIdentity(out var actorId, out var coachId))
                return Results.Unauthorized();

            var result = await handler.ReactivateAsync(coachId, id, actorId, ct);

            return result.IsSuccess
                ? Results.Ok(new AthleteStatusResponse(id, result.Value))
                : result.Error!.ToProblem(http);
        })
        .WithName("ReactivateAthlete")
        .WithSummary("Restore a paused athlete's access.")
        .WithDescription(
            "Sets account status back to Active. Issues no tokens - the athlete signs in again " +
            "themselves, since the tokens revoked at pause are gone for good. Reactivating an " +
            "already-active athlete succeeds and changes nothing.")
        .Produces<AthleteStatusResponse>()
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

    /// <summary>
    /// The coach's own UI preferences. Not under /athletes because it belongs to the coach
    /// doing the sorting, not to any athlete (architecture section 6).
    /// </summary>
    public static IEndpointRouteBuilder MapPreferenceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut("/api/v1/auth/me/preferences", async (
            UpdatePreferencesRequest request,
            IIdentityDbContext db,
            IClock clock,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!principal.TryGetUserId(out var userId))
                return Results.Unauthorized();

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);

            if (user is null)
                return Results.Unauthorized();

            user.SetAthleteListSort(request.AthleteListSort, clock.UtcNow);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new PreferencesResponse(user.AthleteListSort));
        })
        .WithTags("Authentication")
        .WithName("UpdatePreferences")
        .WithSummary("Save the signed-in user's UI preferences.")
        .WithDescription(
            "Stored server-side rather than on the device, so the chosen athlete-list sort " +
            "survives a restart and follows the coach to another device. The saved value also " +
            "comes back on every authentication response and from /auth/me, so the app can " +
            "apply it at startup without an extra call.")
        .Produces<PreferencesResponse>()
        .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson);

        return app;
    }
}
