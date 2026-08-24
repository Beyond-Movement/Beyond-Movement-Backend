using BeyondMovement.Modules.Packages.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BeyondMovement.Modules.Packages.Persistence;

public sealed class PackageOptionConfiguration : IEntityTypeConfiguration<PackageOption>
{
    public void Configure(EntityTypeBuilder<PackageOption> b)
    {
        b.ToTable("PackageOptions");
        b.HasKey(x => x.Id);

        b.Property(x => x.Name).IsRequired().HasMaxLength(PackageOption.MaxNameLength);
        b.Property(x => x.Sessions).IsRequired();
        b.Property(x => x.DefaultPriceMinor).IsRequired();
        b.Property(x => x.Version).IsRequired();

        // Uniqueness is case-insensitive and spans archived options, which EF cannot express:
        // it needs a unique index on lower("Name"), created by raw SQL in the migration. The
        // handler checks first so the caller gets PACKAGE_NAME_CONFLICT rather than a database
        // exception; the index is what holds when two Admin devices race.

        // The Admin list is always "this coach's, archived or not", so both screens are one index.
        b.HasIndex(x => new { x.CoachId, x.IsArchived });

        // OrderedFeatures is a sorted view over the same list, not a second relationship. Left
        // alone, EF's conventions see a collection of PackageOptionFeature and map it, giving
        // the features table a second foreign key that nothing writes to.
        b.Ignore(x => x.OrderedFeatures);

        // A field-only navigation: the list is reachable through the backing field and nothing
        // else, so no caller can add a feature behind Edit's back. Exposing a public collection
        // property as well would map the same relationship twice and produce a second, shadow
        // foreign key beside the real one.
        b.HasMany<PackageOptionFeature>(PackageOption.FeaturesNavigation)
            .WithOne()
            .HasForeignKey(f => f.PackageOptionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class PackageOptionFeatureConfiguration : IEntityTypeConfiguration<PackageOptionFeature>
{
    public void Configure(EntityTypeBuilder<PackageOptionFeature> b)
    {
        b.ToTable("PackageOptionFeatures");
        b.HasKey(x => x.Id);

        b.Property(x => x.Text).IsRequired().HasMaxLength(PackageOptionFeature.MaxTextLength);
        b.Property(x => x.Position).IsRequired();

        // Order is meaning here, so two features cannot occupy one position even under a race.
        b.HasIndex(x => new { x.PackageOptionId, x.Position }).IsUnique();
    }
}

public sealed class AthletePackagePriceConfiguration : IEntityTypeConfiguration<AthletePackagePrice>
{
    public void Configure(EntityTypeBuilder<AthletePackagePrice> b)
    {
        b.ToTable("AthletePackagePrices");
        b.HasKey(x => x.Id);

        b.Property(x => x.PriceMinor).IsRequired();

        // "Only one override may exist for each athlete/package-option pair" - the requirement
        // is an invariant, so it is a constraint, not a check the handler remembers to make.
        b.HasIndex(x => new { x.AthleteUserId, x.PackageOptionId }).IsUnique();

        // Serves the athlete catalogue, which loads every override that athlete has at once.
        b.HasIndex(x => x.AthleteUserId);

        b.HasOne<PackageOption>()
            .WithMany()
            .HasForeignKey(x => x.PackageOptionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class PurchasedPackageConfiguration : IEntityTypeConfiguration<PurchasedPackage>
{
    public void Configure(EntityTypeBuilder<PurchasedPackage> b)
    {
        b.ToTable("PurchasedPackages", t =>
        {
            t.HasCheckConstraint("CK_PurchasedPackages_TotalSessions", "\"TotalSessions\" > 0");

            // The database's half of exactly-once deduction. Even a bug that called Consume twice
            // past the domain guard cannot write a balance that says more sessions were used than
            // were ever bought.
            t.HasCheckConstraint("CK_PurchasedPackages_UsedSessions",
                "\"UsedSessions\" >= 0 AND \"UsedSessions\" <= \"TotalSessions\"");

            t.HasCheckConstraint("CK_PurchasedPackages_Price", "\"PricePaidMinor\" >= 0");
            t.HasCheckConstraint("CK_PurchasedPackages_Dates",
                "\"EndDate\" IS NULL OR \"EndDate\" >= \"StartDate\"");
        });
        b.HasKey(x => x.Id);

        b.Property(x => x.Name).IsRequired().HasMaxLength(PurchasedPackage.MaxNameLength);
        b.Property(x => x.Currency).IsRequired().HasMaxLength(3);
        b.Property(x => x.Notes).HasMaxLength(PurchasedPackage.MaxNotesLength);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        // Maps to Postgres' xmin rather than a column of its own, exactly as Session does.
        b.Property(x => x.Version).IsRowVersion();

        // Computed from TotalSessions and UsedSessions. Without this EF maps it as a column and
        // the balance becomes a third stored number that can disagree with the other two.
        b.Ignore(x => x.RemainingSessions);

        // BR-03 - at most one active package per athlete. A filtered unique index rather than a
        // handler check, because two Admin devices purchasing at the same moment are two
        // transactions, and only the database sees both.
        b.HasIndex(x => x.AthleteProfileId).IsUnique()
            .HasFilter("\"Status\" = 'Active'")
            .HasDatabaseName("IX_PurchasedPackages_OneActivePerAthlete");

        // Package history, newest first.
        b.HasIndex(x => new { x.AthleteProfileId, x.CreatedAtUtc });

        // SetNull, not Cascade: deleting a catalogue entry must never delete the record of what
        // somebody bought. PackageOptionId is provenance, and losing it costs nothing.
        b.HasOne<PackageOption>().WithMany()
            .HasForeignKey(x => x.PackageOptionId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
