using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Packages;
using BeyondMovement.Modules.Packages.Contracts;
using BeyondMovement.Modules.Packages.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Api.Packages;

/// <summary>
/// Read model for catalogue pricing. It spans the Athletes and Packages modules, so it lives
/// in the API composition root and never mutates either module.
/// </summary>
public sealed class CatalogueReader(AppDbContext db)
{
    public Task<bool> BelongsToCoachAsync(
        Guid coachId,
        Guid athleteUserId,
        CancellationToken ct = default) =>
        db.AthleteProfiles.AsNoTracking().AnyAsync(
            profile => profile.UserId == athleteUserId
                       && profile.CoachId == coachId
                       && profile.DeletedAtUtc == null,
            ct);

    public async Task<IReadOnlyList<CatalogueItemResponse>?> PreviewForCoachAsync(
        Guid coachId,
        Guid athleteUserId,
        CancellationToken ct = default)
    {
        var athlete = await db.AthleteProfiles.AsNoTracking()
            .Where(profile => profile.UserId == athleteUserId
                              && profile.CoachId == coachId
                              && profile.DeletedAtUtc == null)
            .Select(profile => new { profile.UserId, profile.IsLoyal })
            .FirstOrDefaultAsync(ct);

        return athlete is null
            ? null
            : await ReadAsync(coachId, athlete.UserId, athlete.IsLoyal, ct);
    }

    public async Task<IReadOnlyList<CatalogueItemResponse>> ForAthleteAsync(
        Guid athleteUserId,
        CancellationToken ct = default)
    {
        var athlete = await db.AthleteProfiles.AsNoTracking()
            .Where(profile => profile.UserId == athleteUserId && profile.DeletedAtUtc == null)
            .Select(profile => new { profile.CoachId, profile.IsLoyal })
            .FirstOrDefaultAsync(ct);

        return athlete is null
            ? []
            : await ReadAsync(athlete.CoachId, athleteUserId, athlete.IsLoyal, ct);
    }

    private async Task<IReadOnlyList<CatalogueItemResponse>> ReadAsync(
        Guid coachId,
        Guid athleteUserId,
        bool isLoyal,
        CancellationToken ct)
    {
        var options = await db.PackageOptions.AsNoTracking()
            .Where(option => option.CoachId == coachId && !option.IsArchived)
            .Include(PackageOption.FeaturesNavigation)
            .ToListAsync(ct);

        var optionIds = options.Select(option => option.Id).ToArray();
        var customPrices = await db.AthletePackagePrices.AsNoTracking()
            .Where(price => price.AthleteUserId == athleteUserId
                            && optionIds.Contains(price.PackageOptionId))
            .ToDictionaryAsync(price => price.PackageOptionId, price => price.PriceMinor, ct);

        return options
            .Select(option => new CatalogueItemResponse(
                option.Id,
                option.Name,
                option.Sessions,
                option.OrderedFeatures.Select(feature => feature.Text).ToArray(),
                PackagePricing.Effective(
                    option.DefaultPriceMinor,
                    isLoyal,
                    customPrices.TryGetValue(option.Id, out var customPrice)
                        ? customPrice
                        : null),
                Currency.Egp))
            .OrderBy(option => option.PriceMinor)
            .ThenBy(option => option.Name)
            .ThenBy(option => option.Id)
            .ToArray();
    }
}
