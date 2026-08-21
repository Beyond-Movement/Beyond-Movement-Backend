namespace BeyondMovement.Modules.Packages.Domain;

/// <summary>
/// The single place an athlete's price for a package option is decided.
/// <para>
/// Money is held as <b>minor units</b> — piastres, of which there are 100 to the Egyptian
/// pound — never as a fractional amount. A decimal price sent as a JSON number is parsed into a
/// Dart <c>double</c> on the client, and doubles cannot represent most decimal fractions
/// exactly; the error is invisible on one price and shows up the first time prices are summed.
/// A 64-bit integer count of piastres has no such failure mode, in C#, in JSON, or in Dart.
/// </para>
/// <para>
/// The rule lives here rather than in a handler because the athlete catalogue, the Admin
/// preview and any future receipt must agree to the piastre. The mobile app deliberately does
/// not reproduce it (see the Phase 4 handoff): the server sends the final price.
/// </para>
/// </summary>
public static class PackagePricing
{
    /// <summary>Loyal athletes pay 85% of the default price.</summary>
    public const int LoyaltyDiscountPercent = 15;

    /// <summary>
    /// Ten million pounds. Not a business rule — a guard against a fat-fingered price
    /// overflowing later arithmetic, and far above anything a coaching package will cost.
    /// </summary>
    public const long MaxPriceMinor = 1_000_000_000;

    /// <summary>
    /// The precedence the client specified, in order: an athlete-specific override wins
    /// outright; otherwise loyalty applies; otherwise the default price stands.
    /// <para>
    /// An override is deliberately <em>not</em> discounted further for a loyal athlete. It is an
    /// agreed price for that athlete, not a starting point, and compounding the two would make
    /// the number the coach typed not the number the athlete sees.
    /// </para>
    /// </summary>
    public static long Effective(long defaultPriceMinor, bool isLoyal, long? customPriceMinor) =>
        customPriceMinor
        ?? (isLoyal ? ApplyLoyaltyDiscount(defaultPriceMinor) : defaultPriceMinor);

    /// <summary>
    /// The rounding step for a discounted price: one tenth of a pound, which is ten piastres.
    /// </summary>
    public const long RoundingStepMinor = 10;

    /// <summary>
    /// 15% off, rounded to the nearest <b>tenth of a pound</b>, with halves going away from zero.
    /// <para>
    /// Fifteen percent of an arbitrary price lands on fractions of a piastre — 999.99 EGP
    /// becomes 849.9915 — and a catalogue full of prices like that reads as a bug rather than a
    /// discount. Rounding to a tenth keeps every loyalty price something a person would write
    /// down: 850.00, not 849.99. Halves go away from zero because that is the rounding anyone
    /// checking the arithmetic on paper will use; banker's rounding surprises them.
    /// </para>
    /// <para>
    /// Only the <em>computed</em> loyalty price is rounded. A default price and a custom override
    /// are stored exactly as the coach entered them — those are numbers a person chose, and
    /// rounding somebody's deliberate 1,234.56 to 1,234.60 would be the API overruling them.
    /// </para>
    /// <para>
    /// The arithmetic is done in <see cref="decimal"/>, which is exact for these values, so the
    /// result never depends on binary floating point.
    /// </para>
    /// </summary>
    public static long ApplyLoyaltyDiscount(long priceMinor)
    {
        var discounted = priceMinor * (100m - LoyaltyDiscountPercent) / 100m;

        var steps = Math.Round(discounted / RoundingStepMinor, MidpointRounding.AwayFromZero);

        var rounded = (long)steps * RoundingStepMinor;

        // Rounding to the nearest tenth rounds up as often as down, and for a price below about
        // a pound that can land ABOVE the original - 0.06 EGP discounted becomes 0.10. No such
        // price is realistic for a coaching package, but a discount that charges more than the
        // undiscounted price is absurd enough to be worth making impossible rather than unlikely.
        return Math.Min(rounded, priceMinor);
    }
}
