namespace BeyondMovement.Modules.Identity.Domain;

public enum UserRole { Admin, Athlete }

public enum UserStatus { Active, Paused, Deleted }

/// <summary>
/// How the coach has chosen to order their athlete list. Stored server-side rather than on the
/// device, because the UI/UX document requires the choice to survive a restart and a coach may
/// use more than one device (architecture section 6).
/// </summary>
public enum AthleteListSort
{
    NameAsc,
    NameDesc,
    Sport,
    NewestFirst,
    OldestFirst
}
