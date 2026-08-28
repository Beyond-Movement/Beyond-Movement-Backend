using BeyondMovement.Api.Endpoints;
using BeyondMovement.Infrastructure;
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

        var option = await db.PackageOptions.FirstOrDefaultAsync(x =>
            x.Id == request.PackageOptionId && x.CoachId == coachId, ct);

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
        var package = PurchasedPackage.Purchase(
            coachId,
            athlete.Id,
            option.Id,
            option.Name,
            option.Sessions,
            PackagePricing.Effective(option.DefaultPriceMinor, athlete.IsLoyal, customPrice),
            request.StartDate ?? DateOnly.FromDateTime(now),
            request.EndDate,
            request.Notes,
            now);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.PurchasedPackages.Add(package);

        try
        {
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(
                "PackagePurchased",
                actorUserId,
                $"package={package.Id} athleteProfile={athlete.Id} option={option.Id} " +
                $"sessions={package.TotalSessions} pricePaidMinor={package.PricePaidMinor}",
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
