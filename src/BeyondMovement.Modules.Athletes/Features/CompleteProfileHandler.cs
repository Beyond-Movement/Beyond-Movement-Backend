using BeyondMovement.Modules.Athletes.Persistence;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Modules.Athletes.Features;

/// <summary>
/// The athlete-details half of Complete Profile. The caller updates the user's full name and
/// marks the profile complete in the same transaction.
/// </summary>
public sealed class CompleteProfileHandler(IAthletesDbContext db, IClock clock)
{
    public async Task<Result> HandleAsync(
        Guid userId, DateOnly? dateOfBirth, string? gender, string? sport, CancellationToken ct = default)
    {
        var profile = await db.AthleteProfiles.FirstOrDefaultAsync(p => p.UserId == userId, ct);

        if (profile is null)
            return Result.Failure(new Error("PROFILE_NOT_FOUND", "No athlete profile exists for this user.", 404));

        profile.CompleteProfile(dateOfBirth, gender, sport, clock.UtcNow);
        await db.SaveChangesAsync(ct);

        return Result.Success();
    }

    public Task<Domain.AthleteProfile?> GetAsync(Guid userId, CancellationToken ct = default) =>
        db.AthleteProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, ct);
}
