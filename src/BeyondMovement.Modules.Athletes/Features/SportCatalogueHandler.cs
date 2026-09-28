using BeyondMovement.Modules.Athletes.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Modules.Athletes.Features;

/// <summary>One sport the athlete can pick. The ordering key is deliberately not part of it.</summary>
public sealed record SportResponse(Guid Id, string Name);

/// <summary>The sports catalogue, read-only. Nothing in the API writes to it.</summary>
public sealed class SportCatalogueHandler(IAthletesDbContext db)
{
    /// <summary>
    /// Alphabetical, with <c>Other</c> last: ordered by the sort group, then by name. Id breaks
    /// any tie, so the order is identical on every call even if two names ever compared equal.
    /// </summary>
    public async Task<IReadOnlyList<SportResponse>> ListAsync(CancellationToken ct = default) =>
        await db.Sports.AsNoTracking()
            .OrderBy(s => s.SortOrder)
            .ThenBy(s => s.Name)
            .ThenBy(s => s.Id)
            .Select(s => new SportResponse(s.Id, s.Name))
            .ToListAsync(ct);
}
