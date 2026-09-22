using BeyondMovement.Modules.Finance.Domain;
using BeyondMovement.SharedKernel;
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

        // The feature snapshot, as child rows rather than the array column it used to be: a
        // feature now carries a recognised code as well as its text, and two parallel arrays that
        // have to stay the same length is exactly the trap the rest of this codebase avoids. A
        // field-only navigation, so nothing outside the entity can rewrite the list that records
        // what somebody bought.
        b.HasMany<PackagePurchaseFeature>(PackagePurchase.FeaturesNavigation)
            .WithOne()
            .HasForeignKey(f => f.PackagePurchaseId)
            .OnDelete(DeleteBehavior.Cascade);

        // Both are computed views over that navigation. Left alone, EF maps them as columns and
        // the snapshot becomes several lists that can disagree - the same trap
        // PackageOption.OrderedFeatures hit.
        b.Ignore(x => x.Features);
        b.Ignore(x => x.FeatureCodes);

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

public sealed class PackagePurchaseFeatureConfiguration
    : IEntityTypeConfiguration<PackagePurchaseFeature>
{
    public void Configure(EntityTypeBuilder<PackagePurchaseFeature> b)
    {
        b.ToTable("PackagePurchaseFeatures");
        b.HasKey(x => x.Id);

        b.Property(x => x.Text).IsRequired().HasMaxLength(PackagePurchase.MaxFeatureLength);
        b.Property(x => x.Position).IsRequired();

        // Nullable, and null is the ordinary case. Stored as the enum name, like every other enum
        // in this database.
        b.Property(x => x.Code)
            .HasConversion<string>()
            .HasMaxLength(PackagePurchase.MaxFeatureCodeLength);

        // Order is meaning here, so two features cannot occupy one position even under a race.
        b.HasIndex(x => new { x.PackagePurchaseId, x.Position }).IsUnique();
    }
}

public sealed class ExpenseConfiguration : IEntityTypeConfiguration<Expense>
{
    public void Configure(EntityTypeBuilder<Expense> b)
    {
        b.ToTable("Expenses", t =>
            // Strictly positive, where CK_PackagePurchases_Price is >= 0. A package may
            // legitimately cost nothing - a comped athlete - but an expense of zero is a typo,
            // and one that slipped through would quietly distort every summary it appeared in.
            t.HasCheckConstraint("CK_Expenses_Amount", "\"AmountMinor\" > 0"));

        b.HasKey(x => x.Id);

        b.Property(x => x.Title).IsRequired().HasMaxLength(Expense.MaxTitleLength);
        b.Property(x => x.AmountMinor).IsRequired();
        b.Property(x => x.Currency).IsRequired().HasMaxLength(3);
        b.Property(x => x.IncurredOn).IsRequired();
        b.Property(x => x.Note).HasMaxLength(Expense.MaxNoteLength);

        // Maps to Postgres' xmin rather than a column of its own, as PackagePurchase does.
        b.Property(x => x.Version).IsRowVersion();

        // Serves both reads there are: the Admin's list, which is this coach's expenses in date
        // order, and the summary, which sums this coach's expenses between two dates. Leading
        // with CoachId because every query is scoped to one coach before it is anything else.
        b.HasIndex(x => new { x.CoachId, x.IncurredOn });

        // No relationship to Users is declared. CoachId is carried as a bare id, exactly as it
        // is on PackagePurchase and PurchasedPackage, because a module may not reference another
        // module (CLAUDE.md section 4).
    }
}
