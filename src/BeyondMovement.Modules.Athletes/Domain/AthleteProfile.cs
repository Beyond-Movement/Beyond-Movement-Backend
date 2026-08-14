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
    public string? Gender { get; private set; }
    public DateOnly? DateOfBirth { get; private set; }
    public string? Notes { get; private set; }

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

    public void CompleteProfile(DateOnly? dateOfBirth, string? gender, string? sport, DateTime nowUtc)
    {
        DateOfBirth = dateOfBirth;
        Gender = gender;
        Sport = sport;
        UpdatedAtUtc = nowUtc;
    }
}
