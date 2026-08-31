using BeyondMovement.Infrastructure.Auditing;
using BeyondMovement.Modules.Athletes.Domain;
using BeyondMovement.Modules.Athletes.Persistence;
using BeyondMovement.Modules.Finance.Domain;
using BeyondMovement.Modules.Finance.Persistence;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Identity.Persistence;
using BeyondMovement.Modules.Packages.Domain;
using BeyondMovement.Modules.Packages.Persistence;
using BeyondMovement.Modules.Scheduling.Domain;
using BeyondMovement.Modules.Scheduling.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Infrastructure;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : DbContext(options), IIdentityDbContext, IAthletesDbContext, IPackagesDbContext, ISchedulingDbContext,
      IFinanceDbContext
{
    // Identity module
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<Invitation> Invitations => Set<Invitation>();

    // Athletes module
    public DbSet<AthleteProfile> AthleteProfiles => Set<AthleteProfile>();

    // Packages module
    public DbSet<PackageOption> PackageOptions => Set<PackageOption>();
    public DbSet<PackageOptionFeature> PackageOptionFeatures => Set<PackageOptionFeature>();
    public DbSet<AthletePackagePrice> AthletePackagePrices => Set<AthletePackagePrice>();
    public DbSet<PurchasedPackage> PurchasedPackages => Set<PurchasedPackage>();

    // Finance module
    public DbSet<PackagePurchase> PackagePurchases => Set<PackagePurchase>();

    // Scheduling module
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<SessionNote> SessionNotes => Set<SessionNote>();
    public DbSet<CalendlyWebhookEvent> CalendlyWebhookEvents => Set<CalendlyWebhookEvent>();
    public DbSet<BookingOperation> BookingOperations => Set<BookingOperation>();
    public DbSet<SchedulingChange> SchedulingChanges => Set<SchedulingChange>();
    public DbSet<CalendlyUnmatchedBooking> CalendlyUnmatchedBookings => Set<CalendlyUnmatchedBooking>();
    public DbSet<CalendlyReconciliationRun> CalendlyReconciliationRuns => Set<CalendlyReconciliationRun>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Picks up every IEntityTypeConfiguration in this assembly.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // ...and one call per module assembly. Add a line here as each module arrives.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(User).Assembly);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AthleteProfile).Assembly);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PackageOption).Assembly);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(Session).Assembly);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PackagePurchase).Assembly);

        modelBuilder.Entity<Session>().HasOne<AthleteProfile>().WithMany()
            .HasForeignKey(x => x.AthleteProfileId).OnDelete(DeleteBehavior.Restrict);

        // Sessions and purchased packages are owned by different modules, so neither can declare
        // the relationship between them - it is wired here, where the whole graph is visible.
        // Restrict, because deleting a package that sessions were deducted from would erase the
        // evidence for those deductions.
        modelBuilder.Entity<Session>().HasOne<PurchasedPackage>().WithMany()
            .HasForeignKey(x => x.PackageId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PurchasedPackage>().HasOne<AthleteProfile>().WithMany()
            .HasForeignKey(x => x.AthleteProfileId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<BookingOperation>().HasOne<AthleteProfile>().WithMany()
            .HasForeignKey(x => x.AthleteProfileId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<BookingOperation>().HasOne<Session>().WithMany()
            .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<SchedulingChange>().HasOne<Session>().WithMany()
            .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);

        // A purchase spans three modules' tables and may name none of them from inside Finance,
        // so its relationships are declared here for the same reason Session's are.
        //
        // SetNull to the catalogue entry: deleting an option must never delete the record of a
        // payment. The purchase carries its own snapshot, so losing the provenance costs nothing.
        modelBuilder.Entity<PackagePurchase>().HasOne<PackageOption>().WithMany()
            .HasForeignKey(x => x.PackageOptionId).OnDelete(DeleteBehavior.SetNull);

        // Restrict to the package: a package is the evidence that a purchase was paid, and
        // deleting one out from under its purchase would leave a paid row that bought nothing -
        // a state the CK_PackagePurchases_PaidConsistency check constraint forbids anyway.
        modelBuilder.Entity<PackagePurchase>().HasOne<PurchasedPackage>().WithMany()
            .HasForeignKey(x => x.PurchasedPackageId).OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PackagePurchase>().HasOne<AthleteProfile>().WithMany()
            .HasForeignKey(x => x.AthleteProfileId).OnDelete(DeleteBehavior.Restrict);
    }
}
