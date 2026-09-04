namespace BeyondMovement.Modules.Identity.Services;

/// <summary>
/// The one place a time-zone id is checked before it reaches <c>Users.TimeZone</c>.
/// <para>
/// It exists because the only reader of that column — <c>DashboardPeriods.Resolve</c> — falls
/// back to UTC <em>silently</em> when the stored value does not resolve. An unvalidated write
/// would therefore not fail anywhere: the dashboard would simply report UTC periods, and
/// late-evening sessions would land in the wrong day, with nothing to say why. Rejecting at the
/// boundary is what keeps that fallback a safety net rather than a bug.
/// </para>
/// </summary>
public static class TimeZoneId
{
    /// <summary>The column's width, and so the longest id that can be stored.</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// True when this server can resolve <paramref name="candidate"/> to a real zone.
    /// <para>
    /// .NET resolves both IANA ("Africa/Cairo") and Windows ("Egypt Standard Time") ids on
    /// either platform through ICU, so a value from a mobile device and one from a Windows
    /// server are both accepted.
    /// </para>
    /// </summary>
    public static bool IsValid(string? candidate) => TryNormalize(candidate, out _);

    /// <summary>
    /// Validates <paramref name="candidate"/> and yields the value to store.
    /// </summary>
    /// <param name="normalized">
    /// The caller's own id, trimmed — deliberately <b>not</b> <see cref="TimeZoneInfo.Id"/>.
    /// <para>
    /// On Windows, <see cref="TimeZoneInfo.FindSystemTimeZoneById"/> accepts an IANA id but
    /// hands back a zone whose <c>Id</c> is the Windows equivalent, so storing that would make
    /// the persisted value depend on the server's operating system: a device sending
    /// "Africa/Cairo" would read back "Egypt Standard Time" from <c>/auth/me</c>, never match,
    /// and re-sync on every single app launch. Preserving what the caller sent is what makes
    /// the mobile compare-then-write flow settle after one write.
    /// </para>
    /// </summary>
    public static bool TryNormalize(string? candidate, out string normalized)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        var trimmed = candidate.Trim();

        // Checked before the lookup: an over-long id cannot be stored whether or not it
        // resolves, and the database is the wrong place to discover that.
        if (trimmed.Length > MaxLength)
            return false;

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(trimmed);
        }
        catch (Exception exception) when (
            exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }

        normalized = trimmed;
        return true;
    }
}
