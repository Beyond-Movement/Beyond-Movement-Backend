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

        // Case-insensitive uniqueness per coach, enforced by the database rather than by a
        // read-then-write in the handler, which two Admin devices can interleave. Archived rows
        // are excluded: a name withdrawn from the catalogue should be reusable.
        b.HasIndex(x => new { x.CoachId, x.Name })
            .IsUnique()
            .HasFilter("\"IsArchived\" = false")
            .HasDatabaseName("IX_PackageOptions_CoachId_Name_Active");

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
