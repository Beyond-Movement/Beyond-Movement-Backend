using BeyondMovement.Modules.Packages.Contracts;
using BeyondMovement.Modules.Packages.Domain;
using BeyondMovement.Modules.Packages.Persistence;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Modules.Packages.Features;

/// <summary>
/// Per-athlete price overrides. The athlete is identified by id only — this module cannot see
/// the Athletes module, so the caller is responsible for having established that the athlete
/// exists and belongs to this coach before calling in.
/// </summary>
public sealed class CustomPriceHandler(IPackagesDbContext db, IClock clock)
{
    public async Task<Result<CustomPriceResponse>> SetAsync(
        Guid coachId, Guid athleteUserId, Guid packageOptionId, long priceMinor,
        CancellationToken ct = default)
    {
        // The option has to be this coach's, or an override could be attached to a stranger's
        // catalogue entry through a guessed id.
        var optionExists = await db.PackageOptions
            .AnyAsync(o => o.Id == packageOptionId && o.CoachId == coachId, ct);

        if (!optionExists)
            return Result<CustomPriceResponse>.Failure(PackageErrors.NotFound);

        var existing = await db.AthletePackagePrices.FirstOrDefaultAsync(
            p => p.AthleteUserId == athleteUserId && p.PackageOptionId == packageOptionId, ct);

        if (existing is null)
        {
            existing = AthletePackagePrice.Create(athleteUserId, packageOptionId, priceMinor, clock.UtcNow);
            db.AthletePackagePrices.Add(existing);
        }
        else
        {
            // Moves the existing override rather than adding a second, so the "one per pair"
            // rule holds whether the coach is setting a price or changing one.
            existing.SetPrice(priceMinor, clock.UtcNow);
        }

        await db.SaveChangesAsync(ct);

        return Result<CustomPriceResponse>.Success(
            new CustomPriceResponse(athleteUserId, packageOptionId, existing.PriceMinor, Currency.Egp));
    }

    /// <summary>
    /// Removing an override does not set a price back to anything — it deletes the row, and the
    /// normal calculation (loyalty, then default) applies again from the next read.
    /// </summary>
    public async Task<Result> RemoveAsync(
        Guid coachId, Guid athleteUserId, Guid packageOptionId, CancellationToken ct = default)
    {
        var existing = await db.AthletePackagePrices
            .Where(p => p.AthleteUserId == athleteUserId && p.PackageOptionId == packageOptionId)
            .Join(db.PackageOptions.Where(o => o.CoachId == coachId),
                price => price.PackageOptionId, option => option.Id, (price, _) => price)
            .FirstOrDefaultAsync(ct);

        if (existing is null)
            return Result.Failure(PackageErrors.CustomPriceNotFound);

        db.AthletePackagePrices.Remove(existing);
        await db.SaveChangesAsync(ct);

        return Result.Success();
    }

    public async Task<IReadOnlyList<CustomPriceResponse>> ListForAthleteAsync(
        Guid coachId, Guid athleteUserId, CancellationToken ct = default)
    {
        var prices = await db.AthletePackagePrices
            .AsNoTracking()
            .Where(p => p.AthleteUserId == athleteUserId)
            .Join(db.PackageOptions.Where(o => o.CoachId == coachId),
                price => price.PackageOptionId, option => option.Id, (price, _) => price)
            .OrderBy(p => p.PackageOptionId)
            .ToListAsync(ct);

        return [.. prices.Select(p =>
            new CustomPriceResponse(p.AthleteUserId, p.PackageOptionId, p.PriceMinor, Currency.Egp))];
    }
}
