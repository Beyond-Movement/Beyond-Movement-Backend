using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Api.Athletes;

/// <summary>
/// Read model for the Admin's athlete screens. Joins <c>Users</c> to <c>AthleteProfiles</c>,
/// which spans two modules, so it sits in the Api composition root and is strictly read-only —
/// the same licence CLAUDE.md gives the Reporting module.
/// </summary>
public sealed class AthleteDirectory(AppDbContext db)
{
    public async Task<PagedResult<AthleteListItem>> ListAsync(
        Guid coachId,
        string? search,
        AthleteStatusFilter status,
        AthleteListSort sort,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var query = BaseQuery(coachId);

        query = ApplyStatusFilter(query, status);
        query = ApplySearch(query, search);

        var total = await query.CountAsync(ct);

        var items = await ApplySort(query, sort)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new AthleteListItem(
                x.User.Id, x.Profile.Id, x.User.FullName, x.User.Email, x.Profile.Sport,
                x.Profile.IsLoyal, x.User.Status, x.User.CreatedAtUtc))
            .ToListAsync(ct);

        return new PagedResult<AthleteListItem>(items, page, pageSize, total);
    }

    public Task<AthleteDetail?> GetAsync(Guid coachId, Guid athleteUserId, CancellationToken ct = default) =>
        BaseQuery(coachId)
            .Where(x => x.User.Id == athleteUserId)
            .Select(x => new AthleteDetail(
                x.User.Id,
                x.Profile.Id,
                x.User.FullName,
                x.User.Email,
                x.User.Phone,
                x.Profile.DateOfBirth,
                x.Profile.Gender,
                x.Profile.Sport,
                x.Profile.IsLoyal,
                x.User.Status,
                x.User.ProfileCompletedAtUtc != null,
                x.User.CreatedAtUtc))
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Every athlete of this coach, paused ones included — pausing hides an athlete from
    /// themselves, never from their coach. Deleted accounts and soft-deleted profiles are
    /// excluded; A-07 will decide what deletion actually does.
    /// </summary>
    private IQueryable<UserProfilePair> BaseQuery(Guid coachId) =>
        from user in db.Users.AsNoTracking()
        join profile in db.AthleteProfiles.AsNoTracking() on user.Id equals profile.UserId
        where user.CoachId == coachId
              && user.Role == UserRole.Athlete
              && user.Status != UserStatus.Deleted
              && profile.DeletedAtUtc == null
        select new UserProfilePair { User = user, Profile = profile };

    private static IQueryable<UserProfilePair> ApplyStatusFilter(
        IQueryable<UserProfilePair> query, AthleteStatusFilter status) => status switch
        {
            AthleteStatusFilter.Active => query.Where(x => x.User.Status == UserStatus.Active),
            AthleteStatusFilter.Paused => query.Where(x => x.User.Status == UserStatus.Paused),
            _ => query
        };

    private static IQueryable<UserProfilePair> ApplySearch(IQueryable<UserProfilePair> query, string? search)
    {
        var term = search?.Trim();

        if (string.IsNullOrEmpty(term))
            return query;

        // ILike is PostgreSQL's case-insensitive LIKE. EF.Functions.Like with ToLower() would
        // also work but defeats any index; ILike keeps the door open for a trigram index later.
        var pattern = $"%{Escape(term)}%";

        // An athlete who has not completed their profile has no name to match on, so searching
        // falls back to the email — otherwise they would be unfindable in the coach's own list.
        return query.Where(x =>
            (x.User.FullName != null && EF.Functions.ILike(x.User.FullName, pattern)) ||
            EF.Functions.ILike(x.User.Email, pattern) ||
            (x.Profile.Sport != null && EF.Functions.ILike(x.Profile.Sport, pattern)));
    }

    /// <summary>A name containing % or _ must match literally, not as a wildcard.</summary>
    private static string Escape(string term) =>
        term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static IQueryable<UserProfilePair> ApplySort(
        IQueryable<UserProfilePair> query, AthleteListSort sort) => sort switch
        {
            // Athletes with no name yet sort last on both name orders, the same way athletes
            // with no sport do: an unnamed row at the top of the list looks like a bug.
            AthleteListSort.NameDesc => query
                .OrderBy(x => x.User.FullName == null)
                .ThenByDescending(x => x.User.FullName)
                .ThenBy(x => x.User.Id),
            // Athletes with no sport yet sort last rather than leading the list.
            AthleteListSort.Sport => query
                .OrderBy(x => x.Profile.Sport == null)
                .ThenBy(x => x.Profile.Sport)
                .ThenBy(x => x.User.FullName)
                .ThenBy(x => x.User.Id),
            AthleteListSort.NewestFirst => query.OrderByDescending(x => x.User.CreatedAtUtc).ThenBy(x => x.User.Id),
            AthleteListSort.OldestFirst => query.OrderBy(x => x.User.CreatedAtUtc).ThenBy(x => x.User.Id),
            _ => query
                .OrderBy(x => x.User.FullName == null)
                .ThenBy(x => x.User.FullName)
                .ThenBy(x => x.User.Id)
        };

    // Id is the tie-breaker on every sort: without one, two athletes sharing a name or a
    // created timestamp can swap places between pages and the coach sees a duplicate or a gap.
    private sealed class UserProfilePair
    {
        public required User User { get; init; }
        public required Modules.Athletes.Domain.AthleteProfile Profile { get; init; }
    }
}
