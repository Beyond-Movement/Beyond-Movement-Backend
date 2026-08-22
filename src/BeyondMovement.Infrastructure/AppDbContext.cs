using BeyondMovement.Infrastructure.Auditing;
using BeyondMovement.Modules.Athletes.Domain;
using BeyondMovement.Modules.Athletes.Persistence;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Identity.Persistence;
using BeyondMovement.Modules.Packages.Domain;
using BeyondMovement.Modules.Packages.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Infrastructure;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : DbContext(options), IIdentityDbContext, IAthletesDbContext, IPackagesDbContext
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
    }
}
