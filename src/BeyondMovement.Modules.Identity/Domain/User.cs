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

    /// <summary>
    /// Set when the athlete finishes Complete Profile. Null means the app must route them
    /// there instead of Home. The Admin has no such step, so it is set at creation.
    /// </summary>
    public DateTime? ProfileCompletedAtUtc { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public bool ProfileCompleted => ProfileCompletedAtUtc is not null;

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
            UpdatedAtUtc = nowUtc,
            ProfileCompletedAtUtc = nowUtc   // the Admin has no Complete Profile step
        };
        user.CoachId = user.Id;
        return user;
    }

    /// <summary>
    /// Created by redeeming an invitation — the only way an athlete enters the platform (BR-01).
    /// Either a password hash or a Google subject must be supplied; the caller decides which,
    /// because Create Account offers both.
    /// </summary>
    public static User CreateAthlete(
        string email, string? fullName, string? passwordHash, string? googleSubjectId,
        Guid coachId, DateTime nowUtc)
    {
        if (passwordHash is null && googleSubjectId is null)
            throw new ArgumentException("An athlete needs either a password or a Google account.");

        return new User
        {
            Role = UserRole.Athlete,
            Email = email.ToLowerInvariant(),
            // May be blank when registering with Google; Complete Profile requires it.
            FullName = fullName ?? string.Empty,
            PasswordHash = passwordHash,
            GoogleSubjectId = googleSubjectId,
            Status = UserStatus.Active,
            CoachId = coachId,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc
            // ProfileCompletedAtUtc stays null: Complete Profile has not happened yet.
        };
    }

    public void SetFullName(string fullName, DateTime nowUtc)
    {
        FullName = fullName;
        UpdatedAtUtc = nowUtc;
    }

    public void MarkProfileCompleted(DateTime nowUtc)
    {
        ProfileCompletedAtUtc ??= nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>
    /// Links a Google account to this user. Google sign-in authenticates an existing account;
    /// it never creates one (BR-01), so linking is the only thing it may do to the user record.
    /// </summary>
    public void LinkGoogleAccount(string googleSubjectId, DateTime nowUtc)
    {
        GoogleSubjectId = googleSubjectId;
        UpdatedAtUtc = nowUtc;
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
