using BeyondMovement.Api.Endpoints;
using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Finance;
using BeyondMovement.Modules.Finance.Contracts;
using BeyondMovement.Modules.Finance.Domain;
using BeyondMovement.Modules.Packages;
using BeyondMovement.Modules.Packages.Contracts;
using BeyondMovement.Modules.Packages.Domain;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Api.Finance;

/// <summary>
/// Package purchase and manual payment confirmation — Phase 8.
/// <para>
/// This lives in the composition root rather than in the Finance module because both operations
/// write across module boundaries: selecting reads the athlete's loyalty flag (Athletes) and the
/// catalogue (Packages) to resolve a price, and confirming creates a <c>PurchasedPackage</c>
/// (Packages) beside the purchase (Finance). CLAUDE.md section 4: a cross-module write is
/// orchestrated in the Api, inside one transaction.
/// </para>
/// </summary>
public sealed class PurchaseCheckoutService(
    AppDbContext db, PurchaseReader reader, IClock clock, IAuditLogger audit)
{
    /// <summary>
    /// The athlete chooses a package option.
    /// <para>
    /// The price is resolved here, by the same <see cref="PackagePricing"/> rule that produced
    /// the number the athlete was already shown in their catalogue, and then <b>snapshotted</b>
    /// onto the purchase along with the name, the session count and the features. Nothing about
    /// the money comes from the request: an athlete who could send a price could send a
    /// different one from the one they were quoted.
    /// </para>
    /// <para>
    /// If the athlete already has a pending request, this <b>revises</b> it rather than opening a
    /// second one — one pending purchase per athlete, and no way to get stuck behind a mistake.
    /// </para>
    /// </summary>
    public async Task<Result<PurchaseSelectionResult>> SelectAsync(
        Guid athleteUserId,
        Guid packageOptionId,
        CancellationToken ct)
    {
        var athlete = await db.AthleteProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == athleteUserId && x.DeletedAtUtc == null, ct);

        if (athlete is null)
            return Result<PurchaseSelectionResult>.Failure(PricingErrors.AthleteNotFound);

        var option = await db.PackageOptions.AsNoTracking()
            .Include(PackageOption.FeaturesNavigation)
            .FirstOrDefaultAsync(x => x.Id == packageOptionId && x.CoachId == athlete.CoachId, ct);

        // An option belonging to another coach is indistinguishable from one that does not
        // exist, so an athlete cannot probe the id space of a catalogue that is not theirs.
        if (option is null)
            return Result<PurchaseSelectionResult>.Failure(PackageErrors.NotFound);

        if (option.IsArchived)
            return Result<PurchaseSelectionResult>.Failure(PackageErrors.Archived);

        // BR-03, checked early so the athlete is told before they are sent to InstaPay rather
        // than after they have paid. It is checked again at confirmation, which is where the
        // race actually lives.
        if (await HasActivePackageAsync(athlete.Id, ct))
            return Result<PurchaseSelectionResult>.Failure(PackageErrors.ActivePackageExists);

        var customPriceMinor = await db.AthletePackagePrices.AsNoTracking()
            .Where(x => x.AthleteUserId == athleteUserId && x.PackageOptionId == option.Id)
            .Select(x => (long?)x.PriceMinor)
            .SingleOrDefaultAsync(ct);

        var priceMinor = PackagePricing.Effective(
            option.DefaultPriceMinor, athlete.IsLoyal, customPriceMinor);

        string[] features = [.. option.OrderedFeatures.Select(feature => feature.Text)];
        var now = clock.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Lock this athlete's pending row, if there is one, so two taps cannot both decide there
        // is nothing to revise and both insert.
        await LockPendingAsync(athlete.Id, ct);

        var existing = await db.PackagePurchases.FirstOrDefaultAsync(
            x => x.AthleteProfileId == athlete.Id && x.Status == PurchasePaymentStatus.Pending, ct);

        PackagePurchase purchase;
        bool created;

        if (existing is not null)
        {
            var revised = existing.ReviseSelection(
                option.Id, option.Name, option.Sessions, features, priceMinor, Currency.Egp, now);

            // Only reachable if the row turned Paid between the lock and the read, which the
            // lock prevents. Surfaced rather than swallowed so a future change cannot hide it.
            if (revised.IsFailure)
                return Result<PurchaseSelectionResult>.Failure(revised.Error!);

            purchase = existing;
            created = false;
        }
        else
        {
            purchase = PackagePurchase.Select(
                athlete.CoachId, athlete.Id, athleteUserId, option.Id, option.Name,
                option.Sessions, features, priceMinor, Currency.Egp, now);

            db.PackagePurchases.Add(purchase);
            created = true;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception) when (IsPendingPurchaseConflict(exception))
        {
            // Two selections raced past the lock on a row that did not exist yet. The unique
            // index caught it; retrying takes the revise path above.
            await transaction.RollbackAsync(ct);
            return Result<PurchaseSelectionResult>.Failure(PackageErrors.ConcurrencyConflict);
        }

        await audit.WriteAsync(
            created ? "PackagePurchaseRequested" : "PackagePurchaseRevised",
            athleteUserId,
            $"purchase={purchase.Id} athleteProfile={athlete.Id} option={option.Id} " +
            $"sessions={purchase.SessionCount} priceMinor={purchase.PriceMinor}",
            ct);

        await transaction.CommitAsync(ct);

        // Read after the commit: the response labels the purchase with the athlete's name as it
        // stands now, which is not part of the snapshot the transaction protects.
        var label = await reader.LabelAsync(athleteUserId, ct);

        return Result<PurchaseSelectionResult>.Success(new PurchaseSelectionResult(
            purchase.ToResponse(label.FullName, label.Email), created));
    }

    /// <summary>
    /// The Admin confirms the money arrived. The only status transition this product has.
    /// <para>
    /// <b>Idempotent.</b> A repeat returns the purchase and the package the first request
    /// created, with <c>alreadyPaid: true</c>, and never makes a second package. Three things
    /// hold that, in order: the row lock taken below, so a concurrent repeat waits and then sees
    /// Paid; the unique index on <c>PurchasedPackageId</c>; and BR-03's partial unique index on
    /// the package itself.
    /// </para>
    /// <para>
    /// If the athlete has acquired an active package since selecting, the purchase is <b>left
    /// Pending</b> and the caller gets 409 <c>ACTIVE_PACKAGE_EXISTS</c>. Nothing is half-done:
    /// the coach closes the current package and confirms again.
    /// </para>
    /// </summary>
    public async Task<Result<MarkPurchasePaidResponse>> MarkPaidAsync(
        Guid coachId,
        Guid purchaseId,
        Guid actorUserId,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Serialises concurrent confirmations of the same purchase. The second one blocks here
        // until the first commits, then reads the row again and finds it already Paid - which is
        // what turns a double tap into one package and one idempotent answer.
        await db.Database.ExecuteSqlAsync(
            $"""SELECT 1 FROM "PackagePurchases" WHERE "Id" = {purchaseId} FOR UPDATE""", ct);

        var purchase = await db.PackagePurchases.FirstOrDefaultAsync(
            x => x.Id == purchaseId && x.CoachId == coachId, ct);

        // Another coach's purchase is a 404, the same as one that does not exist.
        if (purchase is null)
            return Result<MarkPurchasePaidResponse>.Failure(FinanceErrors.PurchaseNotFound);

        if (purchase.Status == PurchasePaymentStatus.Paid)
        {
            var alreadyBought = await db.PurchasedPackages.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == purchase.PurchasedPackageId, ct);

            // A paid purchase always has its package: the check constraint requires the id, and
            // the relationship is Restrict so the package cannot be deleted out from under it.
            if (alreadyBought is null)
                return Result<MarkPurchasePaidResponse>.Failure(PackageErrors.PackageNotFound);

            await transaction.CommitAsync(ct);

            var repeatLabel = await reader.LabelAsync(purchase.AthleteUserId, ct);

            return Result<MarkPurchasePaidResponse>.Success(new MarkPurchasePaidResponse(
                purchase.ToResponse(repeatLabel.FullName, repeatLabel.Email),
                alreadyBought.ToResponse(), AlreadyPaid: true));
        }

        if (await HasActivePackageAsync(purchase.AthleteProfileId, ct))
            return Result<MarkPurchasePaidResponse>.Failure(PackageErrors.ActivePackageExists);

        var now = clock.UtcNow;

        // Built entirely from the snapshot. The catalogue is not consulted here, so an option
        // renamed, repriced or archived between selection and confirmation cannot change what
        // the athlete receives or what they are recorded as having paid.
        var package = PurchasedPackage.Purchase(
            purchase.CoachId,
            purchase.AthleteProfileId,
            purchase.PackageOptionId,
            purchase.PackageName,
            purchase.SessionCount,
            purchase.PriceMinor,
            DateOnly.FromDateTime(now),
            endDate: null,
            notes: null,
            now);

        db.PurchasedPackages.Add(package);

        var paid = purchase.MarkPaid(package.Id, actorUserId, now);
        if (paid.IsFailure)
            return Result<MarkPurchasePaidResponse>.Failure(paid.Error!);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception) when (IsActivePackageConflict(exception))
        {
            // BR-03 lost a race with another purchase or an Admin direct sale. The transaction
            // is rolled back whole, so the purchase is still Pending and can be confirmed once
            // the other package is closed.
            await transaction.RollbackAsync(ct);
            return Result<MarkPurchasePaidResponse>.Failure(PackageErrors.ActivePackageExists);
        }

        await audit.WriteAsync(
            "PackagePurchasePaid",
            actorUserId,
            $"purchase={purchase.Id} package={package.Id} athleteProfile={purchase.AthleteProfileId} " +
            $"sessions={package.TotalSessions} priceMinor={purchase.PriceMinor}",
            ct);

        await transaction.CommitAsync(ct);

        var label = await reader.LabelAsync(purchase.AthleteUserId, ct);

        return Result<MarkPurchasePaidResponse>.Success(new MarkPurchasePaidResponse(
            purchase.ToResponse(label.FullName, label.Email),
            package.ToResponse(), AlreadyPaid: false));
    }

    private Task<bool> HasActivePackageAsync(Guid athleteProfileId, CancellationToken ct) =>
        db.PurchasedPackages.AnyAsync(
            x => x.AthleteProfileId == athleteProfileId
                 && x.Status == PurchasedPackageStatus.Active, ct);

    /// <summary>
    /// Takes a row lock on the athlete's pending purchase without loading it. Loading it through
    /// <c>FromSql</c> instead would mean naming every mapped column, including the <c>xmin</c>
    /// row version, which <c>SELECT *</c> does not return.
    /// </summary>
    private Task LockPendingAsync(Guid athleteProfileId, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync(
            $"""
             SELECT 1 FROM "PackagePurchases"
             WHERE "AthleteProfileId" = {athleteProfileId} AND "Status" = 'Pending'
             FOR UPDATE
             """, ct);

    private static bool IsActivePackageConflict(DbUpdateException exception) =>
        exception.InnerException?.Message.Contains(
            "IX_PurchasedPackages_OneActivePerAthlete", StringComparison.Ordinal) == true;

    private static bool IsPendingPurchaseConflict(DbUpdateException exception) =>
        exception.InnerException?.Message.Contains(
            "IX_PackagePurchases_OnePendingPerAthlete", StringComparison.Ordinal) == true;
}
