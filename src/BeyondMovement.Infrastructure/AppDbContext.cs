using BeyondMovement.Infrastructure.Auditing;
using BeyondMovement.Modules.Athletes.Domain;
using BeyondMovement.Modules.Athletes.Persistence;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Identity.Persistence;
using BeyondMovement.Modules.Packages.Domain;
using BeyondMovement.Modules.Packages.Persistence;
using BeyondMovement.Modules.Scheduling.Domain;
using BeyondMovement.Modules.Scheduling.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Infrastructure;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : DbContext(options), IIdentityDbContext, IAthletesDbContext, IPackagesDbContext, ISchedulingDbContext
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

    // Scheduling module
    public DbSet<Session> Sessions => Set<Session>();
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

        modelBuilder.Entity<Session>().HasOne<AthleteProfile>().WithMany()
            .HasForeignKey(x => x.AthleteProfileId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<BookingOperation>().HasOne<AthleteProfile>().WithMany()
            .HasForeignKey(x => x.AthleteProfileId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<BookingOperation>().HasOne<Session>().WithMany()
            .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<SchedulingChange>().HasOne<Session>().WithMany()
            .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
    }
}
