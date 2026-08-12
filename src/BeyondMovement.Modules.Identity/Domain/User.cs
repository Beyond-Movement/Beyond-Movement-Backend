namespace BeyondMovement.Modules.Identity.Domain;

public sealed class User
{
    public const int MaxFailedLoginAttempts = 5;
    public const int LockoutMinutes = 15;

    public Guid Id { get; private set; } = Guid.NewGuid();
    public UserRole Role { get; private set; }
    public string Email { get; private set; } = null!;
    public string? PasswordHash { get; private set; }
    public string? GoogleSubjectId { get; private set; }
    public string FullName { get; private set; } = null!;
    public string? Phone { get; private set; }
    public UserStatus Status { get; private set; } = UserStatus.Active;
    public string TimeZone { get; private set; } = "UTC";
    public string? UiPreferences { get; private set; }            // jsonb — athlete-list sort order
    public string? NotificationPreferences { get; private set; }  // jsonb
    public Guid CoachId { get; private set; }                     // always the single admin in v1
    public int FailedLoginAttempts { get; private set; }
    public DateTime? LockedOutUntilUtc { get; private set; }
    public DateTime? LastLoginAtUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    private User() { }   // EF Core needs this

    public static User CreateAdmin(string email, string fullName, string passwordHash, DateTime nowUtc)
    {
        var user = new User
        {
            Role = UserRole.Admin,
            Email = email.ToLowerInvariant(),
            FullName = fullName,
            PasswordHash = passwordHash,
            Status = UserStatus.Active,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc
        };
        user.CoachId = user.Id;
        return user;
    }

    public bool IsLockedOut(DateTime nowUtc) => LockedOutUntilUtc is not null && LockedOutUntilUtc > nowUtc;

    public void RecordFailedLogin(DateTime nowUtc)
    {
        FailedLoginAttempts++;
        if (FailedLoginAttempts >= MaxFailedLoginAttempts)
            LockedOutUntilUtc = nowUtc.AddMinutes(LockoutMinutes);
        UpdatedAtUtc = nowUtc;
    }

    public void RecordSuccessfulLogin(DateTime nowUtc)
    {
        FailedLoginAttempts = 0;
        LockedOutUntilUtc = null;
        LastLoginAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    public void SetPasswordHash(string passwordHash, DateTime nowUtc)
    {
        PasswordHash = passwordHash;
        FailedLoginAttempts = 0;
        LockedOutUntilUtc = null;
        UpdatedAtUtc = nowUtc;
    }

    public void Pause(DateTime nowUtc)      { Status = UserStatus.Paused; UpdatedAtUtc = nowUtc; }
    public void Reactivate(DateTime nowUtc) { Status = UserStatus.Active; UpdatedAtUtc = nowUtc; }
}
