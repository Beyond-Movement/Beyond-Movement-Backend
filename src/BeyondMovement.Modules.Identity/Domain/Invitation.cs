namespace BeyondMovement.Modules.Identity.Domain;

public enum InvitationStatus { Pending, Redeemed, Revoked }

/// <summary>
/// An email-bound, single-use invitation. BR-01: no one enters the platform without one.
/// <para>
/// The raw code is never stored — only its hash, exactly like a password. A database leak
/// yields no usable invitations. Because the backend emails the code directly to the intended
/// address, a successful validation also proves the holder controls that inbox, which is why
/// Create Account can show the email as read-only.
/// </para>
/// </summary>
public sealed class Invitation
{
    public const int DefaultLifetimeDays = 14;

    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Email { get; private set; } = null!;
    public string CodeHash { get; private set; } = null!;
    public InvitationStatus Status { get; private set; } = InvitationStatus.Pending;

    public Guid CoachId { get; private set; }
    public Guid CreatedByUserId { get; private set; }

    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? LastValidatedAtUtc { get; private set; }
    public DateTime? RedeemedAtUtc { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }
    public DateTime? LastSentAtUtc { get; private set; }
    public int SendCount { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    private Invitation() { }

    public static Invitation Create(
        string email, string codeHash, Guid coachId, Guid createdByUserId,
        DateTime nowUtc, int lifetimeDays = DefaultLifetimeDays) => new()
        {
            Email = email.ToLowerInvariant(),
            CodeHash = codeHash,
            CoachId = coachId,
            CreatedByUserId = createdByUserId,
            ExpiresAtUtc = nowUtc.AddDays(lifetimeDays),
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
            LastSentAtUtc = nowUtc,
            SendCount = 1
        };

    public bool IsExpired(DateTime nowUtc) => ExpiresAtUtc <= nowUtc;

    public bool IsUsable(DateTime nowUtc) => Status == InvitationStatus.Pending && !IsExpired(nowUtc);

    /// <summary>
    /// Validation deliberately does not consume the invitation — the athlete may validate,
    /// abandon Create Account, and come back. Redemption happens only when the account is
    /// actually created.
    /// </summary>
    public void RecordValidated(DateTime nowUtc)
    {
        LastValidatedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    public void Redeem(DateTime nowUtc)
    {
        Status = InvitationStatus.Redeemed;
        RedeemedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    public void Revoke(DateTime nowUtc)
    {
        Status = InvitationStatus.Revoked;
        RevokedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Replaces the code on resend, so the previous email stops working.</summary>
    public void ReplaceCode(string codeHash, DateTime nowUtc, int lifetimeDays = DefaultLifetimeDays)
    {
        CodeHash = codeHash;
        ExpiresAtUtc = nowUtc.AddDays(lifetimeDays);
        LastSentAtUtc = nowUtc;
        SendCount++;
        UpdatedAtUtc = nowUtc;
    }
}
