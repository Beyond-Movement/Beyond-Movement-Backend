using BeyondMovement.Modules.Packages.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Modules.Packages.Persistence;

public interface IPackagesDbContext
{
    DbSet<PackageOption> PackageOptions { get; }
    DbSet<PackageOptionFeature> PackageOptionFeatures { get; }
    DbSet<AthletePackagePrice> AthletePackagePrices { get; }
    DbSet<PurchasedPackage> PurchasedPackages { get; }

    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
