namespace BeyondMovement.Modules.Packages.Domain;

/// <summary>
/// Which rule decided an athlete's price — the Admin's answer to "why is it this number?".
/// <para>
/// Produced only by <see cref="PackagePricing.Resolve"/>, so it cannot drift from the price it
/// explains. Deliberately absent from the athlete's own catalogue: an athlete is shown a price,
/// not the coach's pricing policy.
/// </para>
/// </summary>
public enum PricingSource
{
    /// <summary>The package option's default price, unmodified.</summary>
    Default,

    /// <summary>The default price less the loyalty discount, because this athlete is marked loyal.</summary>
    Loyalty,

    /// <summary>An agreed per-athlete override, which beats loyalty outright and is never discounted further.</summary>
    Custom
}
