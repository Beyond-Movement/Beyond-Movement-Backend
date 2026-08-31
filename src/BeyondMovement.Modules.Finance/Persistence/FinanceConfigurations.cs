using BeyondMovement.Modules.Finance.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BeyondMovement.Modules.Finance.Persistence;

public sealed class PackagePurchaseConfiguration : IEntityTypeConfiguration<PackagePurchase>
{
    public void Configure(EntityTypeBuilder<PackagePurchase> b)
    {
        b.ToTable("PackagePurchases", t =>
        {
            t.HasCheckConstraint("CK_PackagePurchases_SessionCount", "\"SessionCount\" > 0");
            t.HasCheckConstraint("CK_PackagePurchases_Price", "\"PriceMinor\" >= 0");

            // Paid is not a flag that can be set on its own: it has to carry the moment it
            // happened and the package it produced, or payment history has rows that say money
            // arrived and cannot say what it bought. Pending must carry neither.
            t.HasCheckConstraint(
                "CK_PackagePurchases_PaidConsistency",
                "(\"Status\" = 'Paid' AND \"PaidAtUtc\" IS NOT NULL AND \"PurchasedPackageId\" IS NOT NULL) " +
                "OR (\"Status\" = 'Pending' AND \"PaidAtUtc\" IS NULL AND \"PurchasedPackageId\" IS NULL)");
        });

        b.HasKey(x => x.Id);

        b.Property(x => x.PackageName).IsRequired()
            .HasMaxLength(PackagePurchase.MaxPackageNameLength);
        b.Property(x => x.SessionCount).IsRequired();
        b.Property(x => x.PriceMinor).IsRequired();
        b.Property(x => x.Currency).IsRequired().HasMaxLength(3);

        // Stored as strings, like every other enum in this database: readable during support,
        // and immune to the reordering mistake that renumbers an integer enum.
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(x => x.Origin).HasConversion<string>().HasMaxLength(20).IsRequired();

        // The feature snapshot. A primitive collection reached through the backing field, so
        // nothing outside the entity can rewrite the list that records what somebody bought.
        // Ordered, because the order is what the athlete read down the card.
        b.PrimitiveCollection<List<string>>(PackagePurchase.FeaturesField)
            .HasColumnName("Features")
            .IsRequired()
            .ElementType()
            .HasMaxLength(PackagePurchase.MaxFeatureLength);

        // Computed view over the field above. Left alone, EF maps it as a second column and the
        // snapshot becomes two lists that can disagree - the same trap PackageOption.OrderedFeatures hit.
        b.Ignore(x => x.Features);

        // Maps to Postgres' xmin rather than a column of its own, as PurchasedPackage does.
        b.Property(x => x.Version).IsRowVersion();

        // One pending purchase per athlete. The client's rule, and the reason selecting a
        // different option revises the existing request instead of opening a second one. A
        // filtered unique index rather than a handler check, because two taps from two devices
        // are two transactions and only the database sees both.
        b.HasIndex(x => x.AthleteProfileId).IsUnique()
            .HasFilter("\"Status\" = 'Pending'")
            .HasDatabaseName("IX_PackagePurchases_OnePendingPerAthlete");

        // Exactly one purchase per package, which is the half of "repeating mark-paid produces
        // exactly one package" that survives a bug in the handler. Filtered, because every
        // pending purchase has a null here and nulls would otherwise collide.
        b.HasIndex(x => x.PurchasedPackageId).IsUnique()
            .HasFilter("\"PurchasedPackageId\" IS NOT NULL")
            .HasDatabaseName("IX_PackagePurchases_OnePurchasePerPackage");

        // The Admin list: this coach's purchases, filtered by status, newest first.
        b.HasIndex(x => new { x.CoachId, x.Status, x.CreatedAtUtc });

        // The athlete's own current/latest purchase, and the Admin list filtered to one athlete.
        b.HasIndex(x => new { x.AthleteUserId, x.CreatedAtUtc });

        // The relationships to PackageOption, PurchasedPackage and AthleteProfile are declared in
        // AppDbContext, not here. A module may not reference another module, so this file cannot
        // name those types - the composition root is the only place that sees the whole graph.
    }
}
