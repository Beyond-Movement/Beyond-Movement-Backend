namespace BeyondMovement.SharedKernel;

/// <summary>
/// An athlete's gender, as collected on Complete Profile.
/// <para>
/// It lives in SharedKernel because both modules need it and neither may reference the other
/// (CLAUDE.md section 4): Identity owns the request shape, Athletes owns the stored value.
/// </para>
/// <para>
/// Serialised as its name — "Female" or "Male" — never as an integer, so the generated client
/// and the database rows read the same and reordering the members cannot silently remap
/// existing data (CLAUDE.md section 7). Anything else is a VALIDATION_FAILED.
/// </para>
/// </summary>
public enum Gender
{
    Female,
    Male
}
