namespace BeyondMovement.IntegrationTests;

/// <summary>
/// Builds the wire shape of a package option's features, which is an ordered list of
/// <c>{ text, code }</c> objects rather than the bare strings it was before recognised feature
/// codes existed.
/// <para>
/// Kept in one place so the shape is stated once for every suite that sells a package, and so a
/// test reads as what it is about: <see cref="Open"/> for the ordinary features that make up
/// nearly every package, and <see cref="One"/> with a code for the few the backend acts on.
/// </para>
/// </summary>
internal static class Features
{
    /// <summary>The code of the one recognised feature this product has so far.</summary>
    public const string Observations = "Observations";

    /// <summary>
    /// Ordinary features — free text and no code, which is what a feature was before codes
    /// existed and what nearly all of them still are.
    /// </summary>
    public static object[] Open(params string[] texts) =>
        [.. texts.Select(text => One(text))];

    /// <summary>
    /// One feature. <paramref name="code"/> is null for an ordinary feature and a
    /// <c>PackageFeatureCode</c> name for a recognised one.
    /// </summary>
    public static object One(string text, string? code = null) => new { text, code };

    /// <summary>An ordered list mixing ordinary and recognised features.</summary>
    public static object[] List(params object[] features) => features;

    /// <summary>A package that lets the athlete ask to be observed, plus an ordinary feature.</summary>
    public static object[] WithObservations() =>
        List(One("Weekly video call"), One("Observations", Observations));
}
