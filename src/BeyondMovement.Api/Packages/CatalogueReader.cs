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

    /// <summary>
    /// The Admin's pricing view for one athlete: every active option with its list price, this
    /// athlete's price, and which rule decided it.
    /// <para>
    /// Null when the athlete is unknown or belongs to another coach — the same answer
    /// <see cref="PreviewForCoachAsync"/> gives, so an id cannot be probed for existence.
    /// </para>
    /// </summary>
    public async Task<AthletePricingResponse?> AdminPricingAsync(
        Guid coachId,
        Guid athleteUserId,
        CancellationToken ct = default)
    {
        var athlete = await db.AthleteProfiles.AsNoTracking()
            .Where(profile => profile.UserId == athleteUserId
                              && profile.CoachId == coachId
                              && profile.DeletedAtUtc == null)
            .Select(profile => new { profile.IsLoyal })
            .FirstOrDefaultAsync(ct);

        if (athlete is null)
            return null;

        var (options, customPrices) = await PricingInputsAsync(coachId, athleteUserId, ct);

        var items = options
            .Select(option =>
            {
                // One call, so the price and the reason for it come from the same decision.
                var (priceMinor, source) = PackagePricing.Resolve(
                    option.DefaultPriceMinor,
                    athlete.IsLoyal,
                    customPrices.TryGetValue(option.Id, out var custom) ? custom : null);

                return new AthletePricingItem(
                    option.Id, option.Name, option.Sessions,
                    option.DefaultPriceMinor, priceMinor, source);
            })
            // By name, matching GET /package-options. The athlete catalogue sorts by price
            // because the athlete is shopping; the coach is looking up a package they know.
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.PackageOptionId)
            .ToArray();

        return new AthletePricingResponse(
            athleteUserId,
            athlete.IsLoyal,
            athlete.IsLoyal ? PackagePricing.LoyaltyDiscountPercent : null,
            Currency.Egp,
            items);
    }

    /// <summary>
    /// The two reads both pricing paths need: this coach's sellable options, and this athlete's
    /// overrides among them. Shared so the Admin view and the athlete catalogue cannot disagree
    /// about which options are in scope.
    /// </summary>
    private async Task<(List<PackageOption> Options, Dictionary<Guid, long> CustomPrices)> PricingInputsAsync(
        Guid coachId,
        Guid athleteUserId,
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

        return (options, customPrices);
    }

    private async Task<IReadOnlyList<CatalogueItemResponse>> ReadAsync(
        Guid coachId,
        Guid athleteUserId,
        bool isLoyal,
        CancellationToken ct)
    {
        var (options, customPrices) = await PricingInputsAsync(coachId, athleteUserId, ct);

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
