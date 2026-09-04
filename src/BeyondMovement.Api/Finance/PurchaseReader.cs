using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Finance.Contracts;
using BeyondMovement.Modules.Finance.Domain;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Api.Finance;

/// <summary>
/// Read model for the purchase screens. Joins <c>PackagePurchases</c> (Finance) to <c>Users</c>
/// (Identity), which spans two modules, so it sits in the Api composition root and is strictly
/// read-only — the same arrangement <see cref="Athletes.AthleteDirectory"/> uses.
/// <para>
/// It exists so the join and its <b>left</b>-ness are written once. Every purchase response
/// carries the athlete's name and email, and getting that subtly wrong in five places — an inner
/// join in one of them — would silently drop payment history rather than fail.
/// </para>
/// </summary>
public sealed class PurchaseReader(AppDbContext db)
{
    /// <summary>The athlete's label as it stands now, for a purchase being written by a handler.</summary>
    public async Task<AthleteLabel> LabelAsync(Guid athleteUserId, CancellationToken ct = default)
    {
        // Projected to an anonymous type rather than straight to AthleteLabel: it is a struct, so
        // a missing row would come back as a default value indistinguishable from a real one
        // whose fields happen to be null, and the "no user" case would stop being visible here.
        var found = await db.Users.AsNoTracking()
            .Where(u => u.Id == athleteUserId)
            .Select(u => new { u.FullName, u.Email })
            .FirstOrDefaultAsync(ct);

        return found is null ? AthleteLabel.Unknown : new AthleteLabel(found.FullName, found.Email);
    }

    /// <summary>
    /// The Admin payments screen: this coach's purchases, newest first, optionally narrowed to
    /// one status or one athlete.
    /// </summary>
    public async Task<PagedResult<PackagePurchaseResponse>> ListAsync(
        Guid coachId,
        PurchasePaymentStatus? status,
        Guid? athleteUserId,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var query = db.PackagePurchases.AsNoTracking().Where(x => x.CoachId == coachId);

        if (status is { } wanted) query = query.Where(x => x.Status == wanted);
        if (athleteUserId is { } user) query = query.Where(x => x.AthleteUserId == user);

        var total = await query.CountAsync(ct);

        // Id breaks the tie on CreatedAtUtc so the order is total. Without it two purchases
        // created in the same millisecond could swap places between pages, and offset paging
        // would show one of them twice and the other never.
        var rows = await Join(query)
            .OrderByDescending(x => x.Purchase.CreatedAtUtc)
            .ThenByDescending(x => x.Purchase.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<PackagePurchaseResponse>(
            [.. rows.Select(Map)], page, pageSize, total);
    }

    /// <summary>
    /// One purchase belonging to this coach. Another coach's is null, the same as one that does
    /// not exist, so an id cannot be probed for existence.
    /// </summary>
    public async Task<PackagePurchaseResponse?> GetAsync(
        Guid coachId, Guid purchaseId, CancellationToken ct = default)
    {
        var row = await Join(db.PackagePurchases.AsNoTracking()
                .Where(x => x.Id == purchaseId && x.CoachId == coachId))
            .FirstOrDefaultAsync(ct);

        return row is null ? null : Map(row);
    }

    /// <summary>
    /// The athlete's pending purchase, or their most recent one when nothing is pending — a
    /// screen that is either waiting for confirmation or showing the last receipt.
    /// </summary>
    public async Task<PackagePurchaseResponse?> CurrentAsync(
        Guid athleteUserId, CancellationToken ct = default)
    {
        var row = await Join(db.PackagePurchases.AsNoTracking()
                .Where(x => x.AthleteUserId == athleteUserId))
            .OrderBy(x => x.Purchase.Status == PurchasePaymentStatus.Pending ? 0 : 1)
            .ThenByDescending(x => x.Purchase.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);

        return row is null ? null : Map(row);
    }

    /// <summary>
    /// A <b>left</b> join, and deliberately so. There is no foreign key from
    /// <c>PackagePurchases.AthleteUserId</c> to <c>Users</c> — the only declared relationship is
    /// to <c>AthleteProfiles</c> — so nothing in the database guarantees the user row is there.
    /// An inner join would answer a missing user by dropping the purchase, which means losing a
    /// row of payment history rather than reporting one with no name on it. Nothing today can
    /// delete a user (removal is a status, not a delete), but open decision A-07 has yet to rule
    /// on hard deletion, and this is the difference between that decision being a contract
    /// change and being silent data loss.
    /// </summary>
    private IQueryable<PurchaseWithAthlete> Join(IQueryable<PackagePurchase> purchases) =>
        from purchase in purchases
        join user in db.Users.AsNoTracking() on purchase.AthleteUserId equals user.Id into found
        from user in found.DefaultIfEmpty()
        select new PurchaseWithAthlete
        {
            Purchase = purchase,
            FullName = user == null ? null : user.FullName,
            Email = user == null ? null : user.Email
        };

    private static PackagePurchaseResponse Map(PurchaseWithAthlete row) =>
        row.Purchase.ToResponse(row.FullName, row.Email);

    private sealed class PurchaseWithAthlete
    {
        public required PackagePurchase Purchase { get; init; }
        public required string? FullName { get; init; }
        public required string? Email { get; init; }
    }
}

/// <summary>How a purchase is labelled on screen: the athlete's name, with their email behind it.</summary>
public readonly record struct AthleteLabel(string? FullName, string? Email)
{
    /// <summary>No user row was found — see the note on the left join in <see cref="PurchaseReader"/>.</summary>
    public static readonly AthleteLabel Unknown = new(null, null);
}
