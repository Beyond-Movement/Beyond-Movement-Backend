using BeyondMovement.Modules.Packages.Domain;

// Folder is Pricing, not Packages: the repository .gitignore carries the standard NuGet rule
// **/[Pp]ackages/*, which would silently keep this file out of git and out of CI while it went
// on passing locally.
namespace BeyondMovement.UnitTests.Pricing;

/// <summary>
/// <see cref="PackagePricing.Resolve"/> — the price and the reason for it, decided together.
/// <para>
/// The Admin pricing screen shows a number and a label explaining it. If those came from two
/// separate decisions they could disagree, and the screen would confidently explain a price it
/// was not showing. These pin that they are one decision.
/// </para>
/// </summary>
public class PricingSourceTests
{
    private const long Default = 400_000;   // 4,000.00 EGP

    // --- precedence ---------------------------------------------------------

    [Fact]
    public void With_no_loyalty_and_no_override_the_default_price_stands()
    {
        var (price, source) = PackagePricing.Resolve(Default, isLoyal: false, customPriceMinor: null);

        Assert.Equal(Default, price);
        Assert.Equal(PricingSource.Default, source);
    }

    [Fact]
    public void Loyalty_discounts_the_default_and_says_so()
    {
        var (price, source) = PackagePricing.Resolve(Default, isLoyal: true, customPriceMinor: null);

        Assert.Equal(340_000, price);   // 15% off 4,000.00
        Assert.Equal(PricingSource.Loyalty, source);
    }

    [Fact]
    public void An_override_wins_outright()
    {
        var (price, source) = PackagePricing.Resolve(Default, isLoyal: false, customPriceMinor: 250_000);

        Assert.Equal(250_000, price);
        Assert.Equal(PricingSource.Custom, source);
    }

    /// <summary>
    /// The rule most likely to be got wrong by reimplementing it on a client: an override is an
    /// agreed price, not a starting point, so a loyal athlete does not get 15% off it as well.
    /// </summary>
    [Fact]
    public void An_override_is_not_discounted_further_for_a_loyal_athlete()
    {
        var (price, source) = PackagePricing.Resolve(Default, isLoyal: true, customPriceMinor: 250_000);

        Assert.Equal(250_000, price);              // not 212,500
        Assert.Equal(PricingSource.Custom, source);
    }

    [Fact]
    public void An_override_equal_to_the_default_is_still_an_override()
    {
        // The screen offers Remove Custom Price on this row, so the source has to say Custom
        // even though the number is indistinguishable from the default.
        var (price, source) = PackagePricing.Resolve(Default, isLoyal: false, customPriceMinor: Default);

        Assert.Equal(Default, price);
        Assert.Equal(PricingSource.Custom, source);
    }

    [Fact]
    public void A_free_override_is_an_override_rather_than_a_missing_one()
    {
        // Zero is a legitimate agreed price and must not be read as "no override set" - the
        // trap a nullable long guards against and a plain long would not.
        var (price, source) = PackagePricing.Resolve(Default, isLoyal: true, customPriceMinor: 0);

        Assert.Equal(0, price);
        Assert.Equal(PricingSource.Custom, source);
    }

    // --- the two entry points cannot drift ----------------------------------

    [Theory]
    [InlineData(400_000, false, null)]
    [InlineData(400_000, true, null)]
    [InlineData(400_000, false, 250_000L)]
    [InlineData(400_000, true, 250_000L)]
    [InlineData(99_999, true, null)]
    [InlineData(0, true, null)]
    public void Effective_is_exactly_the_price_half_of_resolve(
        long defaultPriceMinor, bool isLoyal, long? customPriceMinor)
    {
        // Effective delegates to Resolve, and this is what keeps that true: the athlete's quoted
        // price and the Admin's explained price are the same arithmetic, not two copies of it.
        Assert.Equal(
            PackagePricing.Resolve(defaultPriceMinor, isLoyal, customPriceMinor).PriceMinor,
            PackagePricing.Effective(defaultPriceMinor, isLoyal, customPriceMinor));
    }

    [Fact]
    public void The_loyalty_source_always_carries_the_rounded_loyalty_price()
    {
        // 999.99 EGP discounted is 849.9915, which rounds to 850.00 rather than reading as a bug.
        var (price, source) = PackagePricing.Resolve(99_999, isLoyal: true, customPriceMinor: null);

        Assert.Equal(PackagePricing.ApplyLoyaltyDiscount(99_999), price);
        Assert.Equal(85_000, price);
        Assert.Equal(PricingSource.Loyalty, source);
    }
}
