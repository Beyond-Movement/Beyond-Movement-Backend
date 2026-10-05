namespace BeyondMovement.Modules.Identity.Domain;

public sealed class RefreshToken
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; } = null!;   // never store the raw token
    public Guid FamilyId { get; private set; }               // for reuse detection
    public string? DeviceId { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? UsedAtUtc { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    private RefreshToken() { }

    public static RefreshToken Issue(Guid userId, string tokenHash, Guid familyId,
                                     string? deviceId, DateTime nowUtc, int lifetimeDays = 30) => new()
    {
        UserId = userId,
        TokenHash = tokenHash,
        FamilyId = familyId,
        DeviceId = deviceId,
        ExpiresAtUtc = nowUtc.AddDays(lifetimeDays),
        CreatedAtUtc = nowUtc
    };

    /// <summary>
    /// How long after a rotation a second presentation of the spent token counts as the same
    /// client's duplicate - two tabs, a retried request, two calls racing - rather than a replay.
    /// <para>
    /// Ten seconds covers requests already in flight when the first one rotated, and nothing more.
    /// A replay inside the window is refused and issued nothing; the same token presented after it
    /// revokes the family exactly as before.
    /// </para>
    /// <para>
    /// The one thing given up: if a thief spends a stolen token first and the real client presents
    /// it within these seconds, the family is not revoked - the server cannot tell that apart from
    /// the client's own duplicate. The thief needs the token and a race won by seconds; the window
    /// is kept this short so that stays the only case.
    /// </para>
    /// </summary>
    public static readonly TimeSpan SupersededGrace = TimeSpan.FromSeconds(10);

    public bool IsActive(DateTime nowUtc) =>
        RevokedAtUtc is null && UsedAtUtc is null && ExpiresAtUtc > nowUtc;

    /// <summary>
    /// Spent by a rotation within <see cref="SupersededGrace"/>, and not revoked since. A clock
    /// slightly behind the one that rotated it (another API instance) still counts as inside.
    /// </summary>
    public bool WasJustRotated(DateTime nowUtc) =>
        UsedAtUtc is { } usedAt && RevokedAtUtc is null && nowUtc - usedAt <= SupersededGrace;

    public void MarkUsed(DateTime nowUtc) => UsedAtUtc = nowUtc;

    public void Revoke(DateTime nowUtc)
    {
        RevokedAtUtc ??= nowUtc;
    }
}
