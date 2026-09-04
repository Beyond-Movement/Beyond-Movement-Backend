using System.Security.Claims;
using BeyondMovement.Api.Finance;
using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Finance;
using BeyondMovement.Modules.Finance.Contracts;
using BeyondMovement.Modules.Finance.Domain;
using BeyondMovement.Modules.Finance.Payments;
using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.SharedKernel;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BeyondMovement.Api.Endpoints;

/// <summary>
/// Package purchase and manual payment tracking — Phase 8, and the resolution of open decision
/// <b>C-01</b>: a purchase is <c>Pending</c> or <c>Paid</c>, and nothing else.
/// <para>
/// The flow the product actually has: the athlete picks an option and gets a pending request and
/// the coach's InstaPay details; they pay outside this platform, which never sees the money; the
/// Admin confirms receipt, and only then does the package exist. There is no gateway, no
/// automatic verification (BR-14), no partial payment and no cancellation.
/// </para>
/// <para>
/// <b>These routes are <c>/purchases</c>, not the <c>/payments</c> of software-architecture
/// §14.7.</b> That section was written for a model with a separate append-only payment record
/// and a derived package payment status; with one manual confirmation and two states, the
/// purchase <em>is</em> the payment record, and naming the route after the thing it returns is
/// worth more than matching a document the client has since superseded. Instructions stay under
/// <c>/payments/instapay-instructions</c>, exactly as §14.7 has them.
/// </para>
/// </summary>
public static class PurchaseEndpoints
{
    private const string ProblemJson = "application/problem+json";

    public static IEndpointRouteBuilder MapPurchaseEndpoints(this IEndpointRouteBuilder app)
    {
        var mine = app.MapGroup("/api/v1/me/purchases")
            .WithTags("Purchases")
            .RequireAuthorization("AthleteOnly");

        mine.MapPost(string.Empty, Select)
            .WithName("CreatePurchase")
            .WithSummary("Select a package to buy, creating a pending purchase request.")
            .WithDescription(
                "Send only the packageOptionId. The name, session count, features, price and " +
                "currency are resolved server-side and snapshotted onto the purchase - the price " +
                "from the same rule that produced the number in GET /api/v1/catalogue, so the " +
                "app never calculates loyalty, custom pricing, discounts or rounding, and a " +
                "later edit to the option or to this athlete's pricing cannot change a purchase " +
                "that already exists. " +
                "The purchase starts Pending; no package exists yet and an athlete can never " +
                "activate one. Show the athlete GET /api/v1/payments/instapay-instructions next. " +
                "An athlete may have only ONE pending purchase: posting a different option while " +
                "one is pending REPLACES the selection on it and returns 200 with the same " +
                "purchase id, re-priced at today's rules. A new request returns 201. There is no " +
                "cancel - replacing is how a wrong choice is corrected. " +
                "409 ACTIVE_PACKAGE_EXISTS when the athlete still has an active package: they " +
                "cannot buy the next one until it is closed or runs out.")
            .Produces<PackagePurchaseResponse>(StatusCodes.Status201Created)
            .Produces<PackagePurchaseResponse>(StatusCodes.Status200OK)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, ProblemJson);

        mine.MapGet("/current", Current)
            .WithName("GetMyCurrentPurchase")
            .WithSummary("The athlete's pending purchase, or their most recent one.")
            .WithDescription(
                "Always the caller's own - there is no athlete id, and an athlete can never read " +
                "another's purchase. Returns the pending request if there is one, otherwise the " +
                "most recently created purchase, so the screen can show either \"waiting for " +
                "confirmation\" or the last receipt. 404 PURCHASE_NOT_FOUND when the athlete has " +
                "never bought anything, which is a normal state and not a failure to report.")
            .Produces<PackagePurchaseResponse>()
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

        var admin = app.MapGroup("/api/v1/purchases")
            .WithTags("Purchases")
            .RequireAuthorization("AdminOnly");

        admin.MapGet(string.Empty, List)
            .WithName("ListPurchases")
            .WithSummary("A page of purchases, newest first, filterable by status and athlete.")
            .WithDescription(
                "The Admin payments screen. Omit status to see pending and paid together; pass " +
                "status=Pending for the queue of athletes waiting to be confirmed, or " +
                "status=Paid for the payment history. athleteId is the athlete's USER id, " +
                "matching every other /athletes/{athleteId} route. An unknown athlete id returns " +
                "404 ATHLETE_NOT_FOUND rather than an empty list, because an empty list is a real " +
                "answer - most athletes have no purchases yet - and a bad id must not be " +
                "mistaken for one. " +
                "PAGED, in the same envelope as GET /api/v1/athletes: items plus page, pageSize, " +
                "totalCount, totalPages, hasNextPage and hasPreviousPage. page starts at 1 and " +
                "pageSize defaults to 20 and is capped at 100 - values outside the range are " +
                "clamped rather than rejected. Filters apply BEFORE paging, so totalCount is the " +
                "number of purchases matching the filter, not the number that exist. " +
                "Ordered newest first, with the id breaking ties on createdAtUtc so the order is " +
                "total and a row cannot appear on two pages. " +
                "Each row carries athleteName and athleteEmail, so the screen needs no second " +
                "call to label it.")
            .Produces<PagedResult<PackagePurchaseResponse>>()
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

        admin.MapGet("/{id:guid}", Detail)
            .WithName("GetPurchase")
            .WithSummary("One purchase.")
            .WithDescription(
                "A purchase belonging to another coach is 404 PURCHASE_NOT_FOUND, the same as one " +
                "that does not exist, so an id cannot be probed for existence.")
            .Produces<PackagePurchaseResponse>()
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

        admin.MapPost("/{id:guid}/mark-paid", MarkPaid)
            .WithName("MarkPurchasePaid")
            .WithSummary("Confirm the money arrived, creating the athlete's package.")
            .WithDescription(
                "The only status transition this API has: Pending to Paid. There is no cancel, " +
                "no partial payment, and a paid purchase never returns to Pending - corrections " +
                "and refunds are outside this scope. " +
                "In one transaction this records who confirmed and when, creates the purchased " +
                "package from the STORED SNAPSHOT rather than from the catalogue, and links the " +
                "two. The response carries the purchase and the package as they both stand " +
                "afterwards, so the app does not have to re-read them separately. " +
                "It is IDEMPOTENT: repeating it returns 200 with the same purchase and the same " +
                "package id and alreadyPaid: true. It never produces a second package, however " +
                "many times or however concurrently it is called - safe to retry after a timeout. " +
                "409 ACTIVE_PACKAGE_EXISTS if the athlete has acquired an active package since " +
                "selecting (BR-03); the purchase is LEFT PENDING and can be confirmed once that " +
                "package is closed.")
            .Produces<MarkPurchasePaidResponse>()
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, ProblemJson);

        // Both roles: the athlete needs somewhere to pay, and the Admin needs to see what the
        // athlete is being shown when they are asked about it.
        app.MapGet("/api/v1/payments/instapay-instructions", Instructions)
            .WithTags("Purchases")
            .RequireAuthorization()
            .WithName("GetInstaPayInstructions")
            .WithSummary("The configured InstaPay QR code, payment link and instructions.")
            .WithDescription(
                "Every value is configuration supplied by the coach - none of it is hard-coded, " +
                "and the destination can change without an app release, so the app must read it " +
                "here rather than embedding it. qrImageUrl is an absolute URL served without " +
                "authentication, because an image request cannot carry a bearer token. " +
                "The platform never proxies InstaPay, never sees a transaction and never " +
                "verifies one automatically (BR-14): the app opens paymentUrl, and the Admin " +
                "confirms receipt afterwards. " +
                "503 INSTAPAY_NOT_CONFIGURED until real values are supplied - the feature exists " +
                "but has no destination yet, so show a \"contact your coach\" state and keep the " +
                "Pay button rather than hiding it for good.")
            .Produces<PaymentInstructionsResponse>()
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, ProblemJson);

        return app;
    }

    private static async Task<IResult> Select(
        CreatePurchaseRequest request, IValidator<CreatePurchaseRequest> validator,
        PurchaseCheckoutService service, ClaimsPrincipal principal, HttpContext http,
        CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return validation.ToValidationProblem(http);

        if (!principal.TryGetUserId(out var athleteUserId)) return Results.Unauthorized();

        var result = await service.SelectAsync(athleteUserId, request.PackageOptionId, ct);

        if (result.IsFailure) return result.Error!.ToProblem(http);

        // 201 for a new request, 200 for a revised one. Same body either way - the athlete has
        // one pending purchase, and its id does not change when they change their mind.
        return result.Value.Created
            ? Results.Created($"/api/v1/purchases/{result.Value.Purchase.Id}", result.Value.Purchase)
            : Results.Ok(result.Value.Purchase);
    }

    private static async Task<IResult> Current(
        PurchaseReader reader, ClaimsPrincipal principal, HttpContext http, CancellationToken ct)
    {
        if (!principal.TryGetUserId(out var athleteUserId)) return Results.Unauthorized();

        // Pending first, then newest. A pending request is what the screen is waiting on; with
        // none, the most recent purchase is the last thing that happened.
        var purchase = await reader.CurrentAsync(athleteUserId, ct);

        return purchase is null
            ? FinanceErrors.PurchaseNotFound.ToProblem(http)
            : Results.Ok(purchase);
    }

    private static async Task<IResult> List(
        AppDbContext db, PurchaseReader reader, ClaimsPrincipal principal, HttpContext http,
        CancellationToken ct,
        PurchasePaymentStatus? status = null, Guid? athleteId = null,
        int page = 1, int pageSize = PagedResult<PackagePurchaseResponse>.DefaultPageSize)
    {
        if (!principal.TryGetIdentity(out _, out var coachId)) return Results.Unauthorized();

        if (athleteId is { } id && !await db.AthleteProfiles.AsNoTracking().AnyAsync(
                x => x.UserId == id && x.CoachId == coachId && x.DeletedAtUtc == null, ct))
            return PricingErrors.AthleteNotFound.ToProblem(http);

        var (normalizedPage, normalizedSize) =
            PagedResult<PackagePurchaseResponse>.Normalize(page, pageSize);

        return Results.Ok(await reader.ListAsync(
            coachId, status, athleteId, normalizedPage, normalizedSize, ct));
    }

    private static async Task<IResult> Detail(
        Guid id, PurchaseReader reader, ClaimsPrincipal principal, HttpContext http,
        CancellationToken ct)
    {
        if (!principal.TryGetIdentity(out _, out var coachId)) return Results.Unauthorized();

        var purchase = await reader.GetAsync(coachId, id, ct);

        return purchase is null
            ? FinanceErrors.PurchaseNotFound.ToProblem(http)
            : Results.Ok(purchase);
    }

    private static async Task<IResult> MarkPaid(
        Guid id, PurchaseCheckoutService service, ClaimsPrincipal principal, HttpContext http,
        CancellationToken ct)
    {
        if (!principal.TryGetIdentity(out var actorUserId, out var coachId))
            return Results.Unauthorized();

        var result = await service.MarkPaidAsync(coachId, id, actorUserId, ct);

        return result.IsSuccess ? Results.Ok(result.Value) : result.Error!.ToProblem(http);
    }

    /// <summary>
    /// Read through <see cref="IOptionsSnapshot{T}"/> rather than captured at startup — the trap
    /// in CLAUDE.md section 7.4, where configuration added by the test host after the host is
    /// built is invisible to anything that read it during service registration.
    /// </summary>
    private static IResult Instructions(
        IOptionsSnapshot<InstaPayOptions> options, HttpContext http)
    {
        var instaPay = options.Value;

        if (!instaPay.Configured)
            return FinanceErrors.InstaPayNotConfigured.ToProblem(http);

        return Results.Ok(new PaymentInstructionsResponse(
            NullIfBlank(instaPay.QrImageUrl),
            NullIfBlank(instaPay.PaymentUrl),
            NullIfBlank(instaPay.RecipientName),
            NullIfBlank(instaPay.RecipientHandle),
            instaPay.Instructions));
    }

    /// <summary>
    /// An unset value is null, never an empty string. A client that renders whatever it is given
    /// would otherwise draw an empty row for a recipient nobody configured.
    /// </summary>
    private static string? NullIfBlank(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
