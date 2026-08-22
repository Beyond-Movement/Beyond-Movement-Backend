using BeyondMovement.SharedKernel;

namespace BeyondMovement.Modules.Athletes.Domain;

/// <summary>
/// Athlete-specific data, one-to-one with a User of role Athlete (architecture section 6).
/// The Admin has no profile.
/// </summary>
public sealed class AthleteProfile
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid UserId { get; private set; }
    public Guid CoachId { get; private set; }

    public string? Sport { get; private set; }
    public Gender? Gender { get; private set; }
    public DateOnly? DateOfBirth { get; private set; }
    public string? Notes { get; private set; }

    /// <summary>
    /// Whether the coach has marked this athlete as loyal, which earns a standing discount on
    /// every package's default price.
    /// <para>
    /// It lives on the athlete rather than on a package because that is what it describes: a
    /// relationship with the coach, not a property of any one package. The discount itself is
    /// the Packages module's business — this flag only records the fact.
    /// </para>
    /// </summary>
    public bool IsLoyal { get; private set; }

    public DateTime? LoyalSinceUtc { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    /// <summary>Set when the athlete is removed. A-07 decides hard delete vs anonymise.</summary>
    public DateTime? DeletedAtUtc { get; private set; }
    public DateTime? AnonymizedAtUtc { get; private set; }

    private AthleteProfile() { }

    /// <summary>
    /// Created empty during registration. The athlete fills it in on Complete Profile,
    /// which is why every detail is nullable here.
    /// </summary>
    public static AthleteProfile CreateEmpty(Guid userId, Guid coachId, DateTime nowUtc) => new()
    {
        UserId = userId,
        CoachId = coachId,
        CreatedAtUtc = nowUtc,
        UpdatedAtUtc = nowUtc
    };

    /// <summary>
    /// Idempotent: marking a loyal athlete loyal again keeps the original date, so the coach
    /// tapping twice does not reset how long they have been loyal.
    /// </summary>
    public void SetLoyalty(bool isLoyal, DateTime nowUtc)
    {
        if (IsLoyal == isLoyal)
            return;

        IsLoyal = isLoyal;
        LoyalSinceUtc = isLoyal ? nowUtc : null;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>
    /// Every detail is required here even though the columns are nullable: the columns allow
    /// the gap between registering and completing, this method closes it.
    /// </summary>
    public void CompleteProfile(DateOnly dateOfBirth, Gender gender, string sport, DateTime nowUtc)
    {
        DateOfBirth = dateOfBirth;
        Gender = gender;
        Sport = sport;
        UpdatedAtUtc = nowUtc;
    }
}
