using BeyondMovement.SharedKernel;

namespace BeyondMovement.Modules.Finance.Domain;

/// <summary>
/// Something the coach paid for — "Office rental, EGP 500".
/// <para>
/// Deliberately the simplest record that answers "how much did I pay out?". There is <b>no
/// category</b>, no receipt, no supplier, no VAT and no account code: the client asked to jot
/// costs down, not to keep books, and every one of those fields would be a column somebody has
/// to fill in on a screen they use once a week. Adding one later is additive; removing one that
/// rows already carry is not.
/// </para>
/// <para>
/// It belongs to the coach and to nobody else. There is no athlete on an expense — an athlete
/// never sees one, and attributing costs to individuals is the bookkeeping this deliberately is
/// not.
/// </para>
/// </summary>
public sealed class Expense
{
    public const int MaxTitleLength = 200;
    public const int MaxNoteLength = 1000;

    /// <summary>
    /// Ten million pounds, the same ceiling <c>PackagePricing.MaxPriceMinor</c> uses. Not a
    /// business rule — a guard against a fat-fingered amount overflowing the summary's SUM.
    /// </summary>
    public const long MaxAmountMinor = 1_000_000_000;

    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>
    /// Whose expense it is. Always taken from the authenticated Admin's token and never from a
    /// request body, which is what makes another coach's expense unreachable rather than merely
    /// unlisted.
    /// </summary>
    public Guid CoachId { get; private set; }

    /// <summary>What it was. Required — an untitled amount is a number nobody can account for.</summary>
    public string Title { get; private set; } = null!;

    /// <summary>
    /// Piastres, as paid. Strictly positive: a zero-value expense is a typo, not a record. That
    /// differs from a package price, where zero is a real decision — a comped athlete — and is
    /// why <c>CK_Expenses_Amount</c> is <c>&gt; 0</c> where <c>CK_PackagePurchases_Price</c> is
    /// <c>&gt;= 0</c>. Money is an integer count of minor units here for the reason it is
    /// everywhere else: see <c>PackagePricing</c>.
    /// </summary>
    public long AmountMinor { get; private set; }

    public string Currency { get; private set; } = SharedKernel.Currency.Egp;

    /// <summary>
    /// The day the cost was incurred, which is the day it counts towards. A
    /// <see cref="DateOnly"/> rather than a timestamp: a receipt has a date, not an instant, and
    /// the coach types the date on the receipt rather than the moment they got round to entering
    /// it. It also means the summary needs no time-zone conversion on this side.
    /// </summary>
    public DateOnly IncurredOn { get; private set; }

    /// <summary>Optional context. Null and blank both mean none, and both read back as null.</summary>
    public string? Note { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    /// <summary>
    /// Maps to Postgres' <c>xmin</c>, as <c>PackagePurchase</c>, <c>PurchasedPackage</c> and
    /// <c>Session</c> do. Two Admin devices editing one expense must not silently overwrite each
    /// other.
    /// </summary>
    public uint Version { get; private set; }

    private Expense() { }   // EF Core

    public static Expense Record(
        Guid coachId, string title, long amountMinor, DateOnly incurredOn, string? note,
        DateTime nowUtc)
    {
        var expense = new Expense
        {
            CoachId = coachId,
            CreatedAtUtc = nowUtc
        };

        expense.Apply(title, amountMinor, incurredOn, note, nowUtc);
        return expense;
    }

    /// <summary>
    /// Rewrites the expense. A <b>full replacement</b>, not a patch: every field is sent every
    /// time and one left out is being cleared rather than left alone — the same rule the profile
    /// and purchase endpoints follow, and the reason the route is PUT.
    /// <para>
    /// <see cref="CoachId"/> is not a parameter. An expense never changes hands.
    /// </para>
    /// </summary>
    public void Edit(string title, long amountMinor, DateOnly incurredOn, string? note,
        DateTime nowUtc) =>
        Apply(title, amountMinor, incurredOn, note, nowUtc);

    private void Apply(string title, long amountMinor, DateOnly incurredOn, string? note,
        DateTime nowUtc)
    {
        // The validator rejects these at the endpoint and says which field was wrong. These are
        // the guard that survives a caller which does not, because an expense with a zero amount
        // would quietly distort every summary it appears in rather than failing.
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amountMinor);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(amountMinor, MaxAmountMinor);

        Title = title.Trim();
        AmountMinor = amountMinor;
        IncurredOn = incurredOn;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        UpdatedAtUtc = nowUtc;
    }
}
