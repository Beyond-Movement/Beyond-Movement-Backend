using BeyondMovement.Modules.Athletes.Domain;
using BeyondMovement.Modules.Athletes.Persistence;
using BeyondMovement.SharedKernel;

namespace BeyondMovement.Modules.Athletes.Features;

/// <summary>
/// Creates the empty profile that registration pairs with a new athlete user. Called inside
/// the registration transaction so an athlete can never exist without one.
/// </summary>
public sealed class CreateProfileHandler(IAthletesDbContext db, IClock clock)
{
    public async Task<Guid> HandleAsync(Guid userId, Guid coachId, CancellationToken ct = default)
    {
        var profile = AthleteProfile.CreateEmpty(userId, coachId, clock.UtcNow);

        db.AthleteProfiles.Add(profile);
        await db.SaveChangesAsync(ct);

        return profile.Id;
    }
}
