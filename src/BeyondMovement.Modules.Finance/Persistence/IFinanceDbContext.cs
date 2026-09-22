using BeyondMovement.Modules.Finance.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Modules.Finance.Persistence;

public interface IFinanceDbContext
{
    DbSet<PackagePurchase> PackagePurchases { get; }
    DbSet<PackagePurchaseFeature> PackagePurchaseFeatures { get; }
    DbSet<Expense> Expenses { get; }

    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
