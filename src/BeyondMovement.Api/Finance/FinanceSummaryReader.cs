using BeyondMovement.Api.Dashboard;
using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Finance.Domain;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Api.Finance;

/// <summary>
/// Money in, money out, and the difference — the three questions the Finance screen asks.
/// </summary>
/// <param name="Period">Echoed back so the response is self-describing.</param>
/// <param name="TimeZone">
/// The zone the period boundaries were computed in — the Admin's own <c>User.TimeZone</c>, as on
/// the dashboard. Returned so the app can label the window without guessing, and so a
/// wrong-looking month is diagnosable. Falls back to <c>UTC</c> when the stored value is not a
/// zone this server recognises.
/// </param>
/// <param name="FromUtc">Inclusive start of the window. <b>Null for AllTime</b>, which is unbounded.</param>
/// <param name="ToUtc">
/// Exclusive end. Null for AllTime, and in the future for a period still running — the window is
/// the calendar period, not the part of it that has already happened.
/// </param>
/// <param name="IncomeMinor">
/// Money actually <b>received</b>: the sum of <c>PriceMinor</c> over this coach's purchases whose
/// status is Paid, dated by <c>PaidAtUtc</c>.
/// <para>
/// A purchase still <b>Pending is never here</b> — the money has not arrived, and a figure called
/// income that counts hopes is one nobody can reconcile against a bank balance. It is reported
/// separately as <see cref="PendingMinor"/>.
/// </para>
/// </param>
/// <param name="ExpensesMinor">
/// The sum of this coach's expenses dated by <c>IncurredOn</c> — the date on the receipt, not the
/// date it was typed in, so a receipt entered late still lands in the month it belongs to.
/// </param>
/// <param name="NetMinor">
/// <see cref="IncomeMinor"/> minus <see cref="ExpensesMinor"/>. <b>Signed, and legitimately
/// negative</b> in a month the coach spent more than they took. Sent rather than left to the
/// client so one subtraction is done in one place.
/// </param>
/// <param name="PendingMinor">
/// <b>Outstanding, not income.</b> The sum of purchases still awaiting the coach's confirmation —
/// what athletes owe. It is bounded by <c>CreatedAtUtc</c>, because a pending purchase has no
/// payment date to bound it by; it is the only figure here dated by when something was asked for
/// rather than when money moved.
/// <para>
/// It is deliberately in this response and deliberately not added to anything. Never sum it with
/// <see cref="IncomeMinor"/>.
/// </para>
/// </param>
public sealed record FinanceSummaryResponse(
    DashboardPeriod Period,
    string TimeZone,
    DateTime? FromUtc,
    DateTime? ToUtc,
    string Currency,
    long IncomeMinor,
    long ExpensesMinor,
    long NetMinor,
    int IncomeCount,
    int ExpenseCount,
    long PendingMinor,
    int PendingCount);

/// <summary>
/// The Finance screen's read model.
/// <para>
/// It spans Finance (purchases and expenses) and Identity (the Admin's time zone), so it lives in
/// the composition root and only ever reads — the same arrangement as <c>AdminDashboardReader</c>
/// and <c>PurchaseReader</c> (CLAUDE.md section 4).
/// </para>
/// <para>
/// <b>Income is derived, never stored.</b> There is no Income table and there should not be one:
/// a purchase already records the amount, the currency and the moment it was paid, and a second
/// copy of that would be one more thing to keep in step. This class is that derivation, written
/// once.
/// </para>
/// <para>
/// <b>Kept apart from the dashboard.</b> Admin Home is delivery statistics and carries no money;
/// this is money and carries no sessions. They share only <see cref="DashboardPeriods"/>, which
/// is reused rather than reimplemented so a week here and a week there can never begin on
/// different days.
/// </para>
/// </summary>
public sealed class FinanceSummaryReader(AppDbContext db, IClock clock)
{
    public async Task<FinanceSummaryResponse> ReadAsync(
        Guid coachId, DashboardPeriod period, CancellationToken ct)
    {
        var nowUtc = clock.UtcNow;

        // The Admin's own zone decides where a week or a month begins, read from the coach's user
        // row exactly as the dashboard reads it. Bounding money in UTC would file a late-evening
        // payment under the wrong month, and the coach would count their own takings differently
        // from this screen.
        var zoneId = await db.Users.AsNoTracking()
            .Where(x => x.Id == coachId)
            .Select(x => x.TimeZone)
            .SingleOrDefaultAsync(ct);

        var zone = DashboardPeriods.Resolve(zoneId);
        var window = DashboardPeriods.Window(period, nowUtc, zone);

        var income = await ReadIncomeAsync(coachId, window, ct);
        var expenses = await ReadExpensesAsync(coachId, window, zone, ct);
        var pending = await ReadPendingAsync(coachId, window, ct);

        return new FinanceSummaryResponse(
            period,
            zone.Id,
            window.FromUtc,
            window.ToUtc,
            Currency.Egp,
            income.Minor,
            expenses.Minor,
            // Signed on purpose. A month that cost more than it earned is a real answer.
            income.Minor - expenses.Minor,
            income.Count,
            expenses.Count,
            pending.Minor,
            pending.Count);
    }

    /// <summary>
    /// Money received. <b>Paid only</b>, dated by when it was paid.
    /// <para>
    /// A zero-price purchase is Paid and is counted — it contributes nothing to the total and one
    /// to the count, which is correct: a comped package is a real transaction that brought in no
    /// money. An <c>AdminDirect</c> sale is counted too; it is money the coach took in person,
    /// and excluding it would make this screen disagree with their bank.
    /// </para>
    /// </summary>
    private Task<Totals> ReadIncomeAsync(Guid coachId, DashboardWindow window, CancellationToken ct)
    {
        var query = db.PackagePurchases.AsNoTracking()
            .Where(x => x.CoachId == coachId && x.Status == PurchasePaymentStatus.Paid);

        // PaidAtUtc, not CreatedAtUtc: money belongs to the period it arrived in, not the period
        // the athlete happened to ask in. The check constraint guarantees a Paid row has one, so
        // there is no null case to defend against here.
        if (window.FromUtc is { } from) query = query.Where(x => x.PaidAtUtc >= from);
        if (window.ToUtc is { } to) query = query.Where(x => x.PaidAtUtc < to);

        return SumAsync(query.Select(x => x.PriceMinor), ct);
    }

    /// <summary>
    /// What athletes still owe. Reported beside income and never added to it.
    /// <para>
    /// Bounded by <c>CreatedAtUtc</c> because a pending purchase has no payment date — that is
    /// the whole of what makes it pending.
    /// </para>
    /// </summary>
    private Task<Totals> ReadPendingAsync(Guid coachId, DashboardWindow window, CancellationToken ct)
    {
        var query = db.PackagePurchases.AsNoTracking()
            .Where(x => x.CoachId == coachId && x.Status == PurchasePaymentStatus.Pending);

        if (window.FromUtc is { } from) query = query.Where(x => x.CreatedAtUtc >= from);
        if (window.ToUtc is { } to) query = query.Where(x => x.CreatedAtUtc < to);

        return SumAsync(query.Select(x => x.PriceMinor), ct);
    }

    /// <summary>
    /// Money paid out, dated by <c>IncurredOn</c>.
    /// <para>
    /// <b>The one place the window is converted back to dates.</b> An expense carries a
    /// <see cref="DateOnly"/> — a day, with no time and no zone — so it cannot be compared to a
    /// UTC instant without first asking which local day that instant fell on. Comparing the raw
    /// UTC bound instead would move the month boundary by a few hours and quietly file the
    /// first or last day of the month on the wrong side of it.
    /// </para>
    /// </summary>
    private Task<Totals> ReadExpensesAsync(
        Guid coachId, DashboardWindow window, TimeZoneInfo zone, CancellationToken ct)
    {
        var query = db.Expenses.AsNoTracking().Where(x => x.CoachId == coachId);

        if (window.FromUtc is { } from)
        {
            var start = LocalDateOf(from, zone);
            query = query.Where(x => x.IncurredOn >= start);
        }

        if (window.ToUtc is { } to)
        {
            // ToUtc is exclusive and lands on local midnight of the day AFTER the period, so the
            // last day the period actually contains is the one before it. Subtracting a day and
            // comparing inclusively keeps a 31 March expense inside March.
            var end = LocalDateOf(to, zone).AddDays(-1);
            query = query.Where(x => x.IncurredOn <= end);
        }

        return SumAsync(query.Select(x => x.AmountMinor), ct);
    }

    private static DateOnly LocalDateOf(DateTime utc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone));

    /// <summary>
    /// The sum and the count in one round trip.
    /// <para>
    /// <c>SumAsync</c> over an empty set returns <c>0</c> rather than null for a non-nullable
    /// selector, which is the answer an empty period wants: a coach who took nothing this week
    /// earned zero, and the screen shows zero rather than an error.
    /// </para>
    /// </summary>
    private static async Task<Totals> SumAsync(IQueryable<long> amounts, CancellationToken ct)
    {
        var totals = await amounts
            .GroupBy(_ => 1)
            .Select(g => new { Minor = g.Sum(x => x), Count = g.Count() })
            .FirstOrDefaultAsync(ct);

        return totals is null ? new Totals(0, 0) : new Totals(totals.Minor, totals.Count);
    }

    private readonly record struct Totals(long Minor, int Count);
}
