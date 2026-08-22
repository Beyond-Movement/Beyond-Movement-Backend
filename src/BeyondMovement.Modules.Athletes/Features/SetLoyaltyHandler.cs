using BeyondMovement.Modules.Athletes.Persistence;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Modules.Athletes.Features;

/// <summary>
/// Marks an athlete loyal, or removes it. Scoped to the coach, so an id belonging to somebody
/// else is a 404 rather than a 403 (CLAUDE.md section 6).
/// </summary>
public sealed class SetLoyaltyHandler(IAthletesDbContext db, IClock clock)
{
    public async Task<Result<bool>> HandleAsync(
        Guid coachId, Guid athleteUserId, bool isLoyal, CancellationToken ct = default)
    {
        var profile = await db.AthleteProfiles.FirstOrDefaultAsync(
            p => p.UserId == athleteUserId && p.CoachId == coachId && p.DeletedAtUtc == null, ct);

        if (profile is null)
            return Result<bool>.Failure(new Error(
                "ATHLETE_NOT_FOUND", "No such athlete.", 404));

        profile.SetLoyalty(isLoyal, clock.UtcNow);
        await db.SaveChangesAsync(ct);

        return Result<bool>.Success(profile.IsLoyal);
    }
}
