using BeyondMovement.Modules.Athletes.Persistence;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Modules.Athletes.Features;

/// <summary>The athlete details as the profile screen shows them — the sport resolved to its name.</summary>
public sealed record AthleteProfileDetails(
    DateOnly? DateOfBirth,
    Gender? Gender,
    Guid? SportId,
    string? Sport);

/// <summary>
/// The athlete-details half of Complete Profile. The caller updates the user's full name and
/// marks the profile complete in the same transaction.
/// </summary>
public sealed class CompleteProfileHandler(IAthletesDbContext db, IClock clock)
{
    /// <summary>
    /// The sport id names nothing in the catalogue. Checked here rather than in the request's
    /// validator because the catalogue is this module's table and the request is Identity's;
    /// the endpoint reports it as a validation failure on <c>SportId</c>, like any other field.
    /// </summary>
    public static readonly Error UnknownSport =
        new("SPORT_NOT_FOUND", "Choose a sport from the list.", 400);

    /// <returns>The catalogue name of the sport that was saved.</returns>
    public async Task<Result<string>> HandleAsync(
        Guid userId, DateOnly dateOfBirth, Gender gender, Guid sportId, CancellationToken ct = default)
    {
        var sport = await db.Sports.AsNoTracking()
            .Where(s => s.Id == sportId)
            .Select(s => s.Name)
            .FirstOrDefaultAsync(ct);

        if (sport is null)
            return Result<string>.Failure(UnknownSport);

        var profile = await db.AthleteProfiles.FirstOrDefaultAsync(p => p.UserId == userId, ct);

        if (profile is null)
            return Result<string>.Failure(new Error("PROFILE_NOT_FOUND", "No athlete profile exists for this user.", 404));

        profile.CompleteProfile(dateOfBirth, gender, sportId, clock.UtcNow);
        await db.SaveChangesAsync(ct);

        return Result<string>.Success(sport);
    }

    public Task<AthleteProfileDetails?> GetAsync(Guid userId, CancellationToken ct = default) =>
        (from profile in db.AthleteProfiles.AsNoTracking()
         join sport in db.Sports on profile.SportId equals sport.Id into sports
         from sport in sports.DefaultIfEmpty()
         where profile.UserId == userId
         select new AthleteProfileDetails(profile.DateOfBirth, profile.Gender, profile.SportId, sport.Name))
        .FirstOrDefaultAsync(ct);
}
