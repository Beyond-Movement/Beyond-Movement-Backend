namespace BeyondMovement.Modules.Identity.Domain;

public sealed class PasswordResetToken
{
    public const int LifetimeHours = 1;

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; } = null!;   // never store the raw token
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? UsedAtUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    private PasswordResetToken() { }

    public static PasswordResetToken Issue(Guid userId, string tokenHash, DateTime nowUtc) => new()
    {
        UserId = userId,
        TokenHash = tokenHash,
        ExpiresAtUtc = nowUtc.AddHours(LifetimeHours),
        CreatedAtUtc = nowUtc
    };

    public bool IsUsable(DateTime nowUtc) => UsedAtUtc is null && ExpiresAtUtc > nowUtc;

    public void MarkUsed(DateTime nowUtc) => UsedAtUtc = nowUtc;
}
