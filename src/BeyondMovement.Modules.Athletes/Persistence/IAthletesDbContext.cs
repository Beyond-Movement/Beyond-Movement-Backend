using BeyondMovement.Modules.Athletes.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Modules.Athletes.Persistence;

/// <summary>
/// The slice of the database this module is allowed to touch. Same pattern as
/// IIdentityDbContext: modules never reference Infrastructure (CLAUDE.md section 4).
/// </summary>
public interface IAthletesDbContext
{
    DbSet<AthleteProfile> AthleteProfiles { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
