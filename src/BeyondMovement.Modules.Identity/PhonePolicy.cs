using System.Text.RegularExpressions;
using FluentValidation;
using PhoneNumbers;

namespace BeyondMovement.Modules.Identity;

/// <summary>
/// The one definition of what a phone number is, applied by both the Admin's profile edit and
/// the athlete's, and by <c>User.SetPhone</c> itself. It is the same field on the same column, so
/// the two must not be able to disagree about what is acceptable — the same reasoning as
/// <see cref="PasswordPolicy"/>.
/// <para>
/// Numbers are accepted as people write them — spaces, dashes, brackets, a <c>00</c> or <c>+</c>
/// prefix — then parsed with libphonenumber (Google's numbering-plan metadata) and stored in
/// <b>E.164</b>: <c>+201012345678</c>, never <c>010 1234 5678</c>. One canonical form is what
/// lets a later feature compare, dial or message a number without re-parsing display strings.
/// </para>
/// <para>
/// Validation is international, not Egypt-only: any number that carries its country code is
/// judged against its own country's plan. <see cref="DefaultRegion"/> only decides how a number
/// <em>without</em> one is read.
/// </para>
/// <para>
/// This is contact data, not an identity: it is not unique, not verified and not a login.
/// </para>
/// </summary>
public static partial class PhonePolicy
{
    /// <summary>
    /// How long a number may be <em>as typed</em>, formatting included. Unchanged from before
    /// normalization existed, so nothing a client already sends can newly fail on length.
    /// </summary>
    public const int MaximumInputLength = 40;

    /// <summary>
    /// The <c>Users.Phone</c> column: E.164 is a <c>+</c> and at most 15 digits, so every
    /// number <see cref="TryNormalize"/> accepts fits.
    /// </summary>
    public const int MaximumStoredLength = 16;

    /// <summary>
    /// The region a number with no country code is read as. The coach and nearly every athlete
    /// are in Egypt, where people write <c>010 1234 5678</c> rather than <c>+20 10…</c>, and
    /// refusing the way they write their own number would be a worse failure than assuming.
    /// A number with a <c>+</c> or <c>00</c> prefix ignores this entirely.
    /// </summary>
    public const string DefaultRegion = "EG";

    /// <summary>The rule as both profile endpoints state it in the contract.</summary>
    public const string ContractDescription =
        "phone is REQUIRED on every save: missing, null, an empty string or whitespace is " +
        "400 VALIDATION_FAILED under errors.Phone. An account that has none yet - one created " +
        "before phone numbers were collected - still signs in and reads its profile with phone " +
        "null, and must supply one the next time it saves. " +
        "phone is accepted as people write it - digits, spaces and + ( ) - . up to 40 " +
        "characters - and must be a real number for its country, or 400 VALIDATION_FAILED. " +
        "A number without a country code (+ or 00) is read as Egyptian; any other country's " +
        "number needs its code. It is STORED AND RETURNED IN E.164 (+201012345678), not as " +
        "typed. It is contact data only: not unique, not verified, and not a sign-in method.";

    private static readonly PhoneNumberUtil Util = PhoneNumberUtil.GetInstance();

    /// <summary>
    /// Digits, spaces and the punctuation real numbers are written with. Checked before parsing
    /// because libphonenumber is lenient in ways this field should not be: it reads letters as a
    /// vanity keypad and splits off an extension, and E.164 has no room for either — the
    /// extension would be dropped without anyone being told.
    /// </summary>
    [GeneratedRegex(@"^[0-9+()\-.\s]+$")]
    private static partial Regex PhoneCharacters();

    /// <summary>
    /// Null, <c>""</c> and whitespace all mean "no phone number" and normalize to null
    /// successfully — whether a number is <em>required</em> is the validator's question, not
    /// the parser's. Anything else succeeds only if it is a valid number for its region, and
    /// comes back in E.164.
    /// </summary>
    public static bool TryNormalize(string? input, out string? normalized)
    {
        normalized = null;

        if (string.IsNullOrWhiteSpace(input))
            return true;

        if (input.Length > MaximumInputLength || !PhoneCharacters().IsMatch(input))
            return false;

        PhoneNumber number;
        try
        {
            number = Util.Parse(input, DefaultRegion);
        }
        catch (NumberParseException)
        {
            return false;
        }

        // IsValidNumber, not IsPossibleNumber: "possible" is only a length check, and would
        // accept a number in a range no carrier has ever been allocated.
        if (!Util.IsValidNumber(number))
            return false;

        normalized = Util.Format(number, PhoneNumberFormat.E164);
        return true;
    }

    /// <summary>
    /// For the domain, where an invalid number reaching here is a caller that skipped the
    /// validator — a bug, not bad input.
    /// </summary>
    public static string? Normalize(string? input) =>
        TryNormalize(input, out var normalized)
            ? normalized
            : throw new ArgumentException("Not a valid phone number.", nameof(input));

    /// <summary>
    /// The rule for every profile save, Admin and athlete alike: a phone number is <b>required</b>,
    /// and must be a real one. Missing, null, <c>""</c> and whitespace are all refused here.
    /// <para>
    /// Required at this boundary only. The column and the domain still allow null, because
    /// accounts created before phone numbers were collected — the seeded Admin among them — have
    /// none, and inventing one is worse than none. Those accounts sign in and read their profile
    /// as before, and supply a number the next time they save it.
    /// </para>
    /// </summary>
    public static IRuleBuilderOptions<T, string> ApplyPhoneRules<T>(
        this IRuleBuilderInitial<T, string> rule) =>
        rule.Cascade(CascadeMode.Stop)
            .NotEmpty()
                .WithMessage("Enter your phone number.")
            .MaximumLength(MaximumInputLength)
            .Must(phone => PhoneCharacters().IsMatch(phone))
                .WithMessage("Enter a phone number using digits and + ( ) - . only.")
            .Must(phone => TryNormalize(phone, out _))
                .WithMessage(InvalidNumberMessage);

    /// <summary>Public so a test can prove the example it gives is itself accepted.</summary>
    public const string InvalidNumberMessage =
        "Enter a valid phone number. Numbers outside Egypt need their country code, " +
        "for example +44 20 7031 3000.";
}
