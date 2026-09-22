using System.Security.Claims;
using BeyondMovement.Api.Dashboard;
using BeyondMovement.Api.Finance;
using BeyondMovement.Modules.Finance;
using BeyondMovement.Modules.Finance.Contracts;
using BeyondMovement.Modules.Finance.Domain;
using BeyondMovement.Modules.Finance.Features;
using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.SharedKernel;
using FluentValidation;

namespace BeyondMovement.Api.Endpoints;

/// <summary>
/// The Admin's Finance screen: what the coach spent, and the three numbers that answer "how much
/// came in, how much went out, what is the difference?".
/// <para>
/// <b>Purchases are not here.</b> They keep their own routes under <c>/purchases</c> and are
/// unchanged — a purchase is the athlete's transaction and the coach's payment queue, while an
/// expense is the coach's own cost with no athlete on it at all. The summary reads both.
/// </para>
/// <para>
/// <b>Not here, deliberately:</b> no expense categories, no receipts, no refunds, and no
/// financial cards on Admin Home. The coach asked to write down what they spent, not for
/// accounting software.
/// </para>
/// </summary>
public static class FinanceEndpoints
{
    private const string ProblemJson = "application/problem+json";

    public static IEndpointRouteBuilder MapFinanceEndpoints(this IEndpointRouteBuilder app)
    {
        var expenses = app.MapGroup("/api/v1/expenses")
            .WithTags("Finance")
            .RequireAuthorization("AdminOnly");

        expenses.MapGet(string.Empty, List)
            .WithName("ListExpenses")
            .WithSummary("A page of the coach's expenses, newest first.")
            .WithDescription(
                "The Admin's own costs. There is no athlete on an expense and no athlete ever " +
                "sees one; this whole group is Admin-only. " +
                "from and to filter on incurredOn and are BOTH INCLUSIVE, unlike the half-open " +
                "UTC windows the session endpoints use - these are dates a person typed, so " +
                "from=2026-03-01&to=2026-03-31 means the whole of March including the 31st. " +
                "Both are optional and either may be sent alone. Format is YYYY-MM-DD. " +
                "PAGED in the usual envelope: items plus page, pageSize, totalCount, totalPages, " +
                "hasNextPage and hasPreviousPage. page starts at 1, pageSize defaults to 20 and " +
                "is capped at 100, and values outside the range are clamped rather than " +
                "rejected. " +
                "Ordered by incurredOn DESCENDING with the id breaking ties, so the order is " +
                "total and an expense cannot appear on two pages. " +
                "A coach with no expenses, or none in the range, gets an EMPTY PAGE rather than " +
                "a 404.")
            .Produces<PagedResult<ExpenseResponse>>()
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson);

        expenses.MapPost(string.Empty, Create)
            .WithName("CreateExpense")
            .WithSummary("Record something the coach paid for.")
            .WithDescription(
                "Send title, amountMinor and incurredOn; note is optional. " +
                "THERE IS NO CATEGORY, and none is coming in this phase - the coach wanted to " +
                "write down \"Office rental, EGP 500\", not to keep books. " +
                "coachId and currency are NOT in the request and cannot be: the coach comes from " +
                "the token, and the currency is server-controlled EGP, exactly as every package " +
                "price is. " +
                "title is required, non-blank, trimmed, at most 200 characters. " +
                "amountMinor is an integer count of PIASTRES and must be GREATER THAN ZERO - a " +
                "zero-value expense is a typo, not a record, which is why this differs from a " +
                "package price where 0 is a real decision. Its ceiling is 1,000,000,000 " +
                "piastres, ten million EGP, which guards against a mistyped number rather than " +
                "stating a business rule. " +
                "incurredOn is the date on the receipt as YYYY-MM-DD, and is the date the " +
                "expense counts towards in the summary - NOT the date it was entered - so a " +
                "receipt typed up late still lands in the month it belongs to. It may be in the " +
                "past or the future and is deliberately unbounded: entering last year's receipts " +
                "and recording a cost already committed for next month are both legitimate. " +
                "note is optional, at most 1000 characters; null and an empty string both clear " +
                "it and both read back as null.")
            .Produces<ExpenseResponse>(StatusCodes.Status201Created)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson);

        expenses.MapPut("/{id:guid}", Edit)
            .WithName("UpdateExpense")
            .WithSummary("Rewrite an expense.")
            .WithDescription(
                "A FULL REPLACEMENT, NOT A PATCH: send title, amountMinor and incurredOn every " +
                "time, and send note every time you intend to keep it - a field left out of the " +
                "body is cleared, not left alone. PUT rather than PATCH for exactly that reason, " +
                "and to match every other update in this API. " +
                "The same validation as POST applies, so an expense cannot be edited into a " +
                "state it could not have been created in. " +
                "An expense never changes hands: there is no coachId in the body and no way to " +
                "move one to another coach. " +
                "An unknown id, or one belonging to another coach, is 404 EXPENSE_NOT_FOUND - " +
                "deliberately indistinguishable, so an id cannot be probed for existence.")
            .Produces<ExpenseResponse>()
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

        expenses.MapDelete("/{id:guid}", Delete)
            .WithName("DeleteExpense")
            .WithSummary("Remove an expense for good.")
            .WithDescription(
                "A HARD DELETE: the row is gone and the expense stops counting towards every " +
                "summary immediately, including past periods it used to appear in. That is " +
                "deliberate and is safe here in a way it would not be for a purchase - an " +
                "expense is the coach's own note to themselves, nothing else references it, and " +
                "a mistyped one they could not remove would sit in their totals forever. " +
                "The deletion is written to the audit log with the amount and date first, so " +
                "what was removed is recoverable from the log even though the row is not. " +
                "204 with no body on success. An unknown id, or one belonging to another coach, " +
                "is 404 EXPENSE_NOT_FOUND. Deleting an already-deleted expense is therefore 404 " +
                "rather than a silent success.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

        app.MapGet("/api/v1/finance/summary", Summary)
            .WithTags("Finance")
            .RequireAuthorization("AdminOnly")
            .WithName("GetFinanceSummary")
            .WithSummary("Money in, money out, and the difference, for one period.")
            .WithDescription(
                "period defaults to Monthly and takes the same four values as the dashboard, " +
                "computed by the SAME code: Weekly is the current week starting MONDAY, Monthly " +
                "the current month from the 1st, Yearly the current year from 1 January, and " +
                "AllTime has no date bound at all. Boundaries are the ADMIN'S OWN TIME ZONE " +
                "(their User.TimeZone) converted to UTC, so a late-evening payment falls in the " +
                "month the coach was working rather than the one UTC says. The zone actually " +
                "used is echoed back as timeZone and falls back to UTC if the stored value is " +
                "not recognised. fromUtc and toUtc are the resolved window, BOTH NULL for " +
                "AllTime; toUtc is in the future for a period still running. " +
                "incomeMinor is money RECEIVED: the sum of paid purchases, dated by when they " +
                "were paid. A PENDING PURCHASE IS NEVER INCOME - the money has not arrived - and " +
                "is reported separately as pendingMinor. " +
                "A zero-price paid purchase is counted and contributes 0 to incomeMinor and 1 to " +
                "incomeCount, which is correct: a comped package is a real transaction that " +
                "brought in no money. A purchase the Admin recorded directly is income like any " +
                "other - it is money taken in person. " +
                "expensesMinor is the sum of expenses dated by incurredOn, the date on the " +
                "receipt. " +
                "netMinor is incomeMinor minus expensesMinor and is SIGNED: it is legitimately " +
                "NEGATIVE in a period the coach spent more than they took. Do not clamp it. " +
                "pendingMinor and pendingCount are OUTSTANDING INFORMATION ONLY - what athletes " +
                "still owe. They are bounded by when the purchase was created, since a pending " +
                "purchase has no payment date. NEVER add pendingMinor to incomeMinor. " +
                "Every amount is an integer count of PIASTRES in the single currency this " +
                "platform bills in, returned once as currency rather than on each figure. " +
                "A period with nothing in it returns ZEROS, not a 404. " +
                "This is separate from GET /api/v1/dashboard/admin, which is delivery " +
                "statistics and carries no money; the two share only their period machinery.")
            .Produces<FinanceSummaryResponse>()
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson);

        return app;
    }

    private static async Task<IResult> List(
        ExpenseHandler handler, ClaimsPrincipal principal, CancellationToken ct,
        DateOnly? from = null, DateOnly? to = null,
        int page = 1, int pageSize = PagedResult<ExpenseResponse>.DefaultPageSize)
    {
        if (!principal.TryGetIdentity(out _, out var coachId)) return Results.Unauthorized();

        var (normalizedPage, normalizedSize) =
            PagedResult<ExpenseResponse>.Normalize(page, pageSize);

        return Results.Ok(
            await handler.ListAsync(coachId, from, to, normalizedPage, normalizedSize, ct));
    }

    private static async Task<IResult> Create(
        SaveExpenseRequest request, IValidator<SaveExpenseRequest> validator,
        ExpenseHandler handler, ClaimsPrincipal principal, HttpContext http, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return validation.ToValidationProblem(http);

        if (!principal.TryGetIdentity(out var actorUserId, out var coachId))
            return Results.Unauthorized();

        var expense = await handler.CreateAsync(coachId, actorUserId, request, ct);

        return Results.Created($"/api/v1/expenses/{expense.Id}", expense);
    }

    private static async Task<IResult> Edit(
        Guid id, SaveExpenseRequest request, IValidator<SaveExpenseRequest> validator,
        ExpenseHandler handler, ClaimsPrincipal principal, HttpContext http, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return validation.ToValidationProblem(http);

        if (!principal.TryGetIdentity(out var actorUserId, out var coachId))
            return Results.Unauthorized();

        var result = await handler.EditAsync(coachId, id, actorUserId, request, ct);

        return result.IsSuccess ? Results.Ok(result.Value) : result.Error!.ToProblem(http);
    }

    private static async Task<IResult> Delete(
        Guid id, ExpenseHandler handler, ClaimsPrincipal principal, HttpContext http,
        CancellationToken ct)
    {
        if (!principal.TryGetIdentity(out var actorUserId, out var coachId))
            return Results.Unauthorized();

        var result = await handler.DeleteAsync(coachId, id, actorUserId, ct);

        return result.IsSuccess ? Results.NoContent() : result.Error!.ToProblem(http);
    }

    private static async Task<IResult> Summary(
        FinanceSummaryReader reader, ClaimsPrincipal principal, CancellationToken ct,
        DashboardPeriod period = DashboardPeriod.Monthly)
    {
        if (!principal.TryGetIdentity(out _, out var coachId)) return Results.Unauthorized();

        return Results.Ok(await reader.ReadAsync(coachId, period, ct));
    }
}
