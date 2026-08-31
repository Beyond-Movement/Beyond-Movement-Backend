using BeyondMovement.Api.Endpoints;
using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Finance.Domain;
using BeyondMovement.Modules.Packages;
using BeyondMovement.Modules.Packages.Contracts;
using BeyondMovement.Modules.Packages.Domain;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Api.Packages;

/// <summary>
/// Coordinates a package purchase across athlete data, catalogue pricing and the purchased
/// package record. This belongs in the composition root because those records span modules.
/// </summary>
public sealed class PackagePurchaseService(AppDbContext db, IClock clock, IAuditLogger audit)
{
    public async Task<Result<PurchasedPackageResponse>> PurchaseAsync(
        Guid coachId,
        Guid athleteUserId,
        Guid actorUserId,
        PurchasePackageRequest request,
        CancellationToken ct)
    {
        var athlete = await db.AthleteProfiles.FirstOrDefaultAsync(x =>
            x.UserId == athleteUserId
            && x.CoachId == coachId
            && x.DeletedAtUtc == null, ct);

        if (athlete is null)
            return Result<PurchasedPackageResponse>.Failure(PricingErrors.AthleteNotFound);

        // Features are loaded because the purchase record snapshots them alongside the name and
        // the price - see the PackagePurchase created below.
        var option = await db.PackageOptions
            .Include(PackageOption.FeaturesNavigation)
            .FirstOrDefaultAsync(x => x.Id == request.PackageOptionId && x.CoachId == coachId, ct);

        if (option is null)
            return Result<PurchasedPackageResponse>.Failure(PackageErrors.NotFound);

        if (option.IsArchived)
            return Result<PurchasedPackageResponse>.Failure(PackageErrors.Archived);

        if (await db.PurchasedPackages.AnyAsync(x =>
                x.AthleteProfileId == athlete.Id
                && x.Status == PurchasedPackageStatus.Active, ct))
            return Result<PurchasedPackageResponse>.Failure(PackageErrors.ActivePackageExists);

        var customPrice = await db.AthletePackagePrices.AsNoTracking()
            .Where(x => x.AthleteUserId == athleteUserId && x.PackageOptionId == option.Id)
            .Select(x => (long?)x.PriceMinor)
            .SingleOrDefaultAsync(ct);

        var now = clock.UtcNow;
        var priceMinor = PackagePricing.Effective(option.DefaultPriceMinor, athlete.IsLoyal, customPrice);

        var package = PurchasedPackage.Purchase(
            coachId,
            athlete.Id,
            option.Id,
            option.Name,
            option.Sessions,
            priceMinor,
            request.StartDate ?? DateOnly.FromDateTime(now),
            request.EndDate,
            request.Notes,
            now);

        // Phase 8: a package recorded directly by the Admin is a sale that already happened -
        // cash, a bank transfer, something agreed off-app - so it gets a Paid purchase beside it
        // in the same transaction. Without this, payment history and the Athlete Profile's
        // payment status would be blind to every package that did not come through the app, and
        // "is this athlete paid up?" would have two different answers depending on which screen
        // asked. Born Paid because recording it IS the confirmation; there is nothing to await.
        var purchase = PackagePurchase.RecordAdminSale(
            coachId,
            athlete.Id,
            athleteUserId,
            option.Id,
            option.Name,
            option.Sessions,
            [.. option.OrderedFeatures.Select(feature => feature.Text)],
            priceMinor,
            Currency.Egp,
            package.Id,
            actorUserId,
            now);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.PurchasedPackages.Add(package);
        db.PackagePurchases.Add(purchase);

        try
        {
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(
                "PackagePurchased",
                actorUserId,
                $"package={package.Id} purchase={purchase.Id} athleteProfile={athlete.Id} " +
                $"option={option.Id} sessions={package.TotalSessions} " +
                $"pricePaidMinor={package.PricePaidMinor}",
                ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException exception) when (IsActivePackageConflict(exception))
        {
            await transaction.RollbackAsync(ct);
            return Result<PurchasedPackageResponse>.Failure(PackageErrors.ActivePackageExists);
        }

        return Result<PurchasedPackageResponse>.Success(package.ToResponse());
    }

    public async Task<Result<PurchasedPackageResponse>> CloseAsync(
        Guid coachId,
        Guid packageId,
        Guid actorUserId,
        CancellationToken ct)
    {
        var package = await db.PurchasedPackages.FirstOrDefaultAsync(
            x => x.Id == packageId && x.CoachId == coachId, ct);

        if (package is null)
            return Result<PurchasedPackageResponse>.Failure(PackageErrors.PackageNotFound);

        var closed = package.Close(clock.UtcNow);

        if (closed.IsFailure)
            return Result<PurchasedPackageResponse>.Failure(closed.Error!);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(
            "PackageClosed",
            actorUserId,
            $"package={package.Id} athleteProfile={package.AthleteProfileId} " +
            $"used={package.UsedSessions} remaining={package.RemainingSessions}",
            ct);
        await transaction.CommitAsync(ct);

        return Result<PurchasedPackageResponse>.Success(package.ToResponse());
    }

    private static bool IsActivePackageConflict(DbUpdateException exception) =>
        exception.InnerException?.Message.Contains(
            "IX_PurchasedPackages_OneActivePerAthlete", StringComparison.Ordinal) == true;
}
