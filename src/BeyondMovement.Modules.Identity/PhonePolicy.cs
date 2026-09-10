using System.Text.RegularExpressions;
using FluentValidation;

namespace BeyondMovement.Modules.Identity;

/// <summary>
/// The one definition of what a phone number may look like, applied by both the Admin's profile
/// edit and the athlete's. It is the same field on the same column, so the two must not be able
/// to disagree about what is acceptable — the same reasoning as <see cref="PasswordPolicy"/>.
/// <para>
/// Deliberately not a strict format: the coach's athletes are international, numbers are entered
/// as people write them, and a regex that rejects a legitimate number is worse than one that
/// accepts an oddly punctuated one. Nothing dials this — it is displayed and copied.
/// </para>
/// </summary>
public static partial class PhonePolicy
{
    /// <summary>Matches the <c>Users.Phone</c> column, so a number that validates always fits.</summary>
    public const int MaximumLength = 40;

    /// <summary>Digits, spaces and the punctuation real numbers are written with.</summary>
    [GeneratedRegex(@"^[0-9+()\-.\s]+$")]
    private static partial Regex PhoneFormat();

    /// <summary>
    /// Null, <c>""</c> and whitespace all mean "no phone number" and are valid — clearing the
    /// field is not a validation failure, and <c>User.SetPhone</c> stores every one of them as
    /// null. Only a value somebody actually typed is checked against the format.
    /// </summary>
    public static IRuleBuilderOptions<T, string?> ApplyPhoneRules<T>(
        this IRuleBuilder<T, string?> rule) =>
        rule.MaximumLength(MaximumLength)
            .Must(phone => string.IsNullOrWhiteSpace(phone) || PhoneFormat().IsMatch(phone))
            .WithMessage("Enter a phone number using digits and + ( ) - . only.");
}
