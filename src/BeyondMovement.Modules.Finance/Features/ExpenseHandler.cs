using BeyondMovement.Modules.Finance.Contracts;
using BeyondMovement.Modules.Finance.Domain;
using BeyondMovement.Modules.Finance.Persistence;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Modules.Finance.Features;

/// <summary>
/// The coach's own costs: list, record, rewrite, remove.
/// <para>
/// This lives inside the Finance module rather than in the Api composition root, unlike
/// <c>PurchaseReader</c> and <c>PurchaseCheckoutService</c>. Those span modules — a purchase
/// needs the athlete's name from Identity and creates a package in Packages. An expense needs
/// nothing from anywhere: it belongs to the coach, names no athlete, and has no relationship to
/// declare. There is nothing here for the composition root to compose.
/// </para>
/// <para>
/// <b>Every method takes <paramref name="coachId"/> and every query filters on it.</b> That is
/// the whole of the authorization model, and it is applied in the query rather than checked
/// afterwards: another coach's expense is not found rather than found and refused, which is what
/// makes it a 404 and not a way to discover that an id is real.
/// </para>
/// </summary>
public sealed class ExpenseHandler(IFinanceDbContext db, IClock clock, IAuditLogger audit)
{
    /// <summary>
    /// A page of this coach's expenses, newest first.
    /// </summary>
    /// <param name="from">
    /// Inclusive lower bound on <see cref="Expense.IncurredOn"/>, or null for no lower bound.
    /// </param>
    /// <param name="to">
    /// <b>Inclusive</b> upper bound, unlike the half-open UTC windows the session endpoints use.
    /// These are dates a person typed, not instants: a coach asking for 1–31 March means the
    /// 31st included, and an exclusive bound there is the kind of off-by-one nobody reports
    /// because it silently drops one day's costs.
    /// </param>
    public async Task<PagedResult<ExpenseResponse>> ListAsync(
        Guid coachId, DateOnly? from, DateOnly? to, int page, int pageSize,
        CancellationToken ct = default)
    {
        var query = db.Expenses.AsNoTracking().Where(x => x.CoachId == coachId);

        if (from is { } start) query = query.Where(x => x.IncurredOn >= start);
        if (to is { } end) query = query.Where(x => x.IncurredOn <= end);

        var total = await query.CountAsync(ct);

        // Newest first by the date the cost was incurred, which is the order a coach reads their
        // own spending in. Id breaks the tie so the order is total: without it two expenses on
        // the same day could swap places between requests, and offset paging would show one of
        // them twice and the other never.
        var expenses = await query
            .OrderByDescending(x => x.IncurredOn)
            .ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<ExpenseResponse>(
            [.. expenses.Select(x => x.ToResponse())], page, pageSize, total);
    }

    public async Task<Result<ExpenseResponse>> GetAsync(
        Guid coachId, Guid id, CancellationToken ct = default)
    {
        var expense = await FindAsync(coachId, id, tracked: false, ct);

        return expense is null
            ? Result<ExpenseResponse>.Failure(FinanceErrors.ExpenseNotFound)
            : Result<ExpenseResponse>.Success(expense.ToResponse());
    }

    public async Task<ExpenseResponse> CreateAsync(
        Guid coachId, Guid actorUserId, SaveExpenseRequest request, CancellationToken ct = default)
    {
        var expense = Expense.Record(
            coachId, request.Title, request.AmountMinor, request.IncurredOn, request.Note,
            clock.UtcNow);

        db.Expenses.Add(expense);
        await db.SaveChangesAsync(ct);

        // Money leaving the practice is exactly what the audit log is for (CLAUDE.md section 7).
        await WriteAuditAsync("ExpenseCreated", actorUserId, expense, ct);

        return expense.ToResponse();
    }

    /// <summary>
    /// Rewrites an expense whole. There is no partial update: the screen edits every field
    /// together, so a field left out of the body is one being cleared.
    /// </summary>
    public async Task<Result<ExpenseResponse>> EditAsync(
        Guid coachId, Guid id, Guid actorUserId, SaveExpenseRequest request,
        CancellationToken ct = default)
    {
        var expense = await FindAsync(coachId, id, tracked: true, ct);

        if (expense is null)
            return Result<ExpenseResponse>.Failure(FinanceErrors.ExpenseNotFound);

        expense.Edit(
            request.Title, request.AmountMinor, request.IncurredOn, request.Note, clock.UtcNow);

        await db.SaveChangesAsync(ct);

        await WriteAuditAsync("ExpenseEdited", actorUserId, expense, ct);

        return Result<ExpenseResponse>.Success(expense.ToResponse());
    }

    /// <summary>
    /// Removes an expense for good.
    /// <para>
    /// A <b>hard</b> delete, which is the one place this module destroys a financial row. It is
    /// safe here in a way it would not be for a purchase: an expense is the coach's own note to
    /// themselves, nobody else's record depends on it, and a mistyped one they cannot remove
    /// would sit in their totals forever. The audit entry is written <b>before</b> the row goes,
    /// carrying the amount and the date, so what was deleted is recoverable from the log even
    /// though the row is not.
    /// </para>
    /// </summary>
    public async Task<Result> DeleteAsync(
        Guid coachId, Guid id, Guid actorUserId, CancellationToken ct = default)
    {
        var expense = await FindAsync(coachId, id, tracked: true, ct);

        if (expense is null)
            return Result.Failure(FinanceErrors.ExpenseNotFound);

        // Read off the entity while it still exists. After SaveChanges the instance is detached
        // and an audit entry written from it would be describing something already gone.
        await WriteAuditAsync("ExpenseDeleted", actorUserId, expense, ct);

        db.Expenses.Remove(expense);
        await db.SaveChangesAsync(ct);

        return Result.Success();
    }

    private Task<Expense?> FindAsync(Guid coachId, Guid id, bool tracked, CancellationToken ct)
    {
        var query = tracked ? db.Expenses : db.Expenses.AsNoTracking();

        // CoachId is part of the predicate, not a check after the fact. An expense belonging to
        // somebody else simply does not resolve.
        return query.FirstOrDefaultAsync(x => x.Id == id && x.CoachId == coachId, ct);
    }

    private Task WriteAuditAsync(
        string action, Guid actorUserId, Expense expense, CancellationToken ct) =>
        audit.WriteAsync(
            action,
            actorUserId,
            $"expense={expense.Id} amountMinor={expense.AmountMinor} " +
            $"currency={expense.Currency} incurredOn={expense.IncurredOn:O}",
            ct);
}
