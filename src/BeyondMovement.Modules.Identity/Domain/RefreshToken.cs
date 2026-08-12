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

    public bool IsActive(DateTime nowUtc) =>
        RevokedAtUtc is null && UsedAtUtc is null && ExpiresAtUtc > nowUtc;

    public void MarkUsed(DateTime nowUtc) => UsedAtUtc = nowUtc;

    public void Revoke(DateTime nowUtc)
    {
        RevokedAtUtc ??= nowUtc;
    }
}
