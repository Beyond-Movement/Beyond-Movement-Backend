using BeyondMovement.Modules.Finance.Domain;
using BeyondMovement.Modules.Packages.Domain;
using BeyondMovement.SharedKernel;

// Folder is Pricing, not Packages: the repository .gitignore carries the standard NuGet rule
// **/[Pp]ackages/*, which would silently keep this file out of git and out of CI while it went
// on passing locally. Same reason PricingSourceTests lives here.
namespace BeyondMovement.UnitTests.Pricing;

/// <summary>
/// Recognised package features, at the level the database is not involved in: that a feature's
/// <c>Code</c> is optional and independent of its text, that editing rewrites both, and that what
/// a purchase and a purchased package freeze is the code rather than the wording.
/// <para>
/// The rule these all serve is that <b>eligibility is never decided by display text</b>. Several
/// of them would pass just as happily if it were, so the ones that would not — a feature whose
/// text says "Observations" and grants nothing, and a feature that grants observations while
/// saying something else entirely — are the load-bearing tests here.
/// </para>
/// </summary>
public class PackageFeatureTests
{
    private static readonly DateTime Now = new(2026, 9, 13, 10, 0, 0, DateTimeKind.Utc);

    private static PackageOption Option(params PackageFeature[] features) =>
        PackageOption.Create(Guid.NewGuid(), "8 Sessions", 8, 400_000, features, Now);

    // --- the feature itself --------------------------------------------------

    [Fact]
    public void A_feature_with_no_code_is_an_ordinary_feature()
    {
        var option = Option(new PackageFeature("Weekly video call"));

        var feature = Assert.Single(option.OrderedFeatures);

        Assert.Equal("Weekly video call", feature.Text);
        Assert.Null(feature.Code);
        Assert.Empty(option.FeatureCodes);
    }

    [Fact]
    public void Text_is_trimmed_and_order_is_kept()
    {
        var option = Option(
            new PackageFeature("  Third  "),
            new PackageFeature("First"),
            new PackageFeature("Second"));

        Assert.Equal(
            ["Third", "First", "Second"],
            option.OrderedFeatures.Select(f => f.Text));

        Assert.Equal([0, 1, 2], option.OrderedFeatures.Select(f => f.Position));
    }

    /// <summary>
    /// The wording and the code are independent. This is the whole design: the coach writes
    /// whatever the athlete should read, and the backend reads the code.
    /// </summary>
    [Fact]
    public void The_code_is_independent_of_the_text()
    {
        var option = Option(
            new PackageFeature("Observations"),                                        // says it, is not it
            new PackageFeature("I come and watch you compete", PackageFeatureCode.Observations));

        Assert.Null(option.OrderedFeatures[0].Code);
        Assert.Equal(PackageFeatureCode.Observations, option.OrderedFeatures[1].Code);

        // One code, from the feature that carries it - not from the one that spells it out.
        Assert.Equal([PackageFeatureCode.Observations], option.FeatureCodes);
    }

    [Fact]
    public void Editing_rewrites_the_code_as_well_as_the_text()
    {
        var option = Option(new PackageFeature("Plain"));

        var added = option.Edit("8 Sessions", 8, 400_000,
            [new PackageFeature("Observations", PackageFeatureCode.Observations)], Now);

        Assert.True(added.IsSuccess);
        Assert.Equal([PackageFeatureCode.Observations], option.FeatureCodes);

        // The rows are rewritten in place, so a code that is dropped clears rather than lingering
        // on the row it used to be on.
        var removed = option.Edit("8 Sessions", 8, 400_000,
            [new PackageFeature("Observations")], Now);

        Assert.True(removed.IsSuccess);
        Assert.Empty(option.FeatureCodes);
        Assert.Null(Assert.Single(option.OrderedFeatures).Code);
    }

    [Fact]
    public void Shortening_the_list_drops_the_features_the_new_one_does_not_use()
    {
        var option = Option(
            new PackageFeature("One"),
            new PackageFeature("Two", PackageFeatureCode.Observations),
            new PackageFeature("Three"));

        option.Edit("8 Sessions", 8, 400_000, [new PackageFeature("Only")], Now);

        Assert.Equal(["Only"], option.OrderedFeatures.Select(f => f.Text));
        Assert.Empty(option.FeatureCodes);
    }

    // --- what a purchased package freezes -----------------------------------

    [Fact]
    public void A_package_is_bought_with_the_codes_it_was_sold_with()
    {
        var package = Purchase([PackageFeatureCode.Observations]);

        Assert.Equal([PackageFeatureCode.Observations], package.IncludedFeatures);
        Assert.True(package.Includes(PackageFeatureCode.Observations));
    }

    [Fact]
    public void A_package_sold_without_a_code_does_not_include_it()
    {
        var package = Purchase([]);

        Assert.Empty(package.IncludedFeatures);
        Assert.False(package.Includes(PackageFeatureCode.Observations));
    }

    /// <summary>
    /// The code is the identity, so being handed it twice means the package grants it, not that it
    /// grants it twice. A filtered unique index stops a duplicate reaching here at all; this is
    /// what happens if one ever does.
    /// </summary>
    [Fact]
    public void A_repeated_code_is_stored_once()
    {
        var package = Purchase([PackageFeatureCode.Observations, PackageFeatureCode.Observations]);

        Assert.Equal([PackageFeatureCode.Observations], package.IncludedFeatures);
    }

    // --- what a purchase freezes --------------------------------------------

    [Fact]
    public void A_purchase_snapshots_the_features_in_order_with_their_codes()
    {
        var purchase = Select(
            new PackageFeature("Weekly video call"),
            new PackageFeature("Coach attends your competitions", PackageFeatureCode.Observations));

        Assert.Equal(
            ["Weekly video call", "Coach attends your competitions"],
            purchase.Features.Select(f => f.Text));

        Assert.Equal([null, PackageFeatureCode.Observations], purchase.Features.Select(f => f.Code));

        // The codes alone, which is what confirming the payment puts on the package.
        Assert.Equal([PackageFeatureCode.Observations], purchase.FeatureCodes);
    }

    [Fact]
    public void Revising_a_pending_purchase_replaces_the_snapshot_and_its_codes()
    {
        var purchase = Select(new PackageFeature("Weekly video call"));

        Assert.Empty(purchase.FeatureCodes);

        var revised = purchase.ReviseSelection(
            Guid.NewGuid(), "12 Sessions", 12,
            [new PackageFeature("Observations", PackageFeatureCode.Observations)],
            600_000, "EGP", Now);

        Assert.True(revised.IsSuccess);
        Assert.Equal(["Observations"], purchase.Features.Select(f => f.Text));
        Assert.Equal([PackageFeatureCode.Observations], purchase.FeatureCodes);
    }

    /// <summary>
    /// A paid purchase is the record of what somebody paid for, so nobody may edit it — the codes
    /// included, since they are what the package it produced was built from.
    /// </summary>
    [Fact]
    public void A_paid_purchase_cannot_be_re_snapshotted()
    {
        var purchase = Select(new PackageFeature("Weekly video call"));
        purchase.MarkPaid(Guid.NewGuid(), Guid.NewGuid(), Now);

        var revised = purchase.ReviseSelection(
            Guid.NewGuid(), "12 Sessions", 12,
            [new PackageFeature("Observations", PackageFeatureCode.Observations)],
            600_000, "EGP", Now);

        Assert.True(revised.IsFailure);
        Assert.Empty(purchase.FeatureCodes);
    }

    [Fact]
    public void An_admin_direct_sale_snapshots_the_features_too()
    {
        var purchase = PackagePurchase.RecordAdminSale(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "8 Sessions", 8,
            [new PackageFeature("Observations", PackageFeatureCode.Observations)],
            400_000, "EGP", Guid.NewGuid(), Guid.NewGuid(), Now);

        Assert.Equal([PackageFeatureCode.Observations], purchase.FeatureCodes);
    }

    // --- helpers ------------------------------------------------------------

    private static PurchasedPackage Purchase(PackageFeatureCode[] includedFeatures) =>
        PurchasedPackage.Purchase(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "8 Sessions", 8, includedFeatures,
            400_000, new DateOnly(2026, 9, 13), endDate: null, notes: null, Now);

    private static PackagePurchase Select(params PackageFeature[] features) =>
        PackagePurchase.Select(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "8 Sessions", 8,
            features, 400_000, "EGP", Now);
}
