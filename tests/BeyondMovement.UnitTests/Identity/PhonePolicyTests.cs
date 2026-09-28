using BeyondMovement.Modules.Identity;
using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Identity.Features.Invitations;
using BeyondMovement.Modules.Identity.Features.Profile;

namespace BeyondMovement.UnitTests.Identity;

/// <summary>
/// The one parser both roles' phone numbers go through. Tested directly because the endpoints
/// and the domain all defer to it, so a rule that is wrong here is wrong everywhere at once.
/// </summary>
public class PhonePolicyTests
{
    // ------------------------------------------------------------ accepted

    [Theory]
    // Egyptian, written the ways people actually write their own number.
    [InlineData("010 1234 5678", "+201012345678")]
    [InlineData("01012345678", "+201012345678")]
    [InlineData("0101-234-5678", "+201012345678")]
    [InlineData("+20 10 1234 5678", "+201012345678")]
    [InlineData("+201012345678", "+201012345678")]
    [InlineData("0020 10 1234 5678", "+201012345678")]
    [InlineData("(+20) 10.1234.5678", "+201012345678")]
    [InlineData("  +20 111 222 3333  ", "+201112223333")]
    [InlineData("02 2345 6789", "+20223456789")]                 // a Cairo landline, not a mobile
    // International: judged against its own country's plan, not Egypt's.
    [InlineData("+44 20 7031 3000", "+442070313000")]            // UK landline
    [InlineData("+44 7400 123456", "+447400123456")]             // UK mobile
    [InlineData("+1 (650) 253-0000", "+16502530000")]            // US
    [InlineData("+971 50 123 4567", "+971501234567")]            // UAE
    [InlineData("+33 6 12 34 56 78", "+33612345678")]            // France
    [InlineData("+49 30 123456", "+4930123456")]                 // Germany, a short number
    [InlineData("+966 50 123 4567", "+966501234567")]            // Saudi Arabia
    [InlineData("00 44 20 7031 3000", "+442070313000")]          // 00 is Egypt's way of dialling out
    [InlineData("++20 10 1234 5678", "+201012345678")]           // a doubled +: unambiguous, so tolerated
    public void A_real_number_is_stored_in_E164_however_it_was_written(string typed, string expected)
    {
        Assert.True(PhonePolicy.TryNormalize(typed, out var normalized));
        Assert.Equal(expected, normalized);
        Assert.True(normalized!.Length <= PhonePolicy.MaximumStoredLength);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void The_parser_reads_blank_as_no_number_leaving_requiredness_to_the_validator(string? blank)
    {
        Assert.True(PhonePolicy.TryNormalize(blank, out var normalized));
        Assert.Null(normalized);
        Assert.Null(PhonePolicy.Normalize(blank));
    }

    // ------------------------------------------------------------ refused

    [Theory]
    [InlineData("call me maybe")]                    // not a number at all
    [InlineData("1-800-FLOWERS")]                    // vanity letters: libphonenumber would map them
    [InlineData("+20 10 1234 5678 ext. 12")]         // an extension E.164 would silently drop
    [InlineData("+20 10 1234 5678 #12")]
    [InlineData("12345")]                            // too short for any plan
    [InlineData("010 1234")]                         // an Egyptian mobile missing digits
    [InlineData("010 1234 56789")]                   // ...and one with too many
    [InlineData("0000000000")]
    [InlineData("+")]
    [InlineData("+999 1234 5678")]                   // no such country code
    [InlineData("+44 12")]                           // right country, impossible number
    [InlineData("+1 (123) 456-7890")]                // US area codes never start with 1
    [InlineData("7400 123456")]                      // a UK mobile without its code reads as Egyptian, and is not one
    public void An_obviously_invalid_number_is_refused(string typed)
    {
        Assert.False(PhonePolicy.TryNormalize(typed, out var normalized));
        Assert.Null(normalized);
        Assert.Throws<ArgumentException>(() => PhonePolicy.Normalize(typed));
    }

    [Fact]
    public void Input_longer_than_the_field_allows_is_refused_even_if_the_digits_would_parse()
    {
        var padded = "+20" + new string(' ', PhonePolicy.MaximumInputLength) + "1012345678";

        Assert.False(PhonePolicy.TryNormalize(padded, out _));
    }

    [Fact]
    public void The_example_in_the_error_message_is_itself_accepted()
    {
        // A message telling people to type a number the API then refuses would be its own bug,
        // so the example is read out of the message rather than restated here.
        var example = PhonePolicy.InvalidNumberMessage.Split("for example ")[1].TrimEnd('.');

        Assert.True(PhonePolicy.TryNormalize(example, out _));
    }

    // ------------------------------------------------- both roles, one rule

    public static TheoryData<string?, bool> Inputs => new()
    {
        { "010 1234 5678", true },
        { "+44 20 7031 3000", true },
        { null, false },
        { "", false },
        { "   ", false },
        { "call me maybe", false },
        { "12345", false },
        { "+999 1234 5678", false }
    };

    /// <summary>
    /// The Admin's form and the athlete's write the same column. They must not be able to
    /// disagree about a single input, which is only guaranteed while both call the same rule.
    /// </summary>
    [Theory]
    [MemberData(nameof(Inputs))]
    public void The_admin_and_athlete_validators_agree_on_every_number(string? phone, bool valid)
    {
        // null! because the contract says non-null, and a JSON body can still bind null into it.
        var admin = new UpdateAdminProfileValidator()
            .Validate(new UpdateAdminProfileRequest("Coach", phone!));

        var athlete = new CompleteProfileValidator(new SharedKernel.SystemClock())
            .Validate(new CompleteProfileRequest(
                "Athlete", new DateOnly(2001, 4, 17), SharedKernel.Gender.Female,
                    Modules.Athletes.Domain.SportCatalogue.Tennis, phone!));

        Assert.Equal(valid, admin.IsValid);
        Assert.Equal(valid, athlete.IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("call me maybe")]
    [InlineData("12345")]
    public void A_refusal_reports_one_message_against_the_phone_field(string? phone)
    {
        var result = new UpdateAdminProfileValidator()
            .Validate(new UpdateAdminProfileRequest("Coach", phone!));

        // Cascade stops at the first failure, so the app shows one reason, not two.
        var error = Assert.Single(result.Errors);
        Assert.Equal(nameof(UpdateAdminProfileRequest.Phone), error.PropertyName);
    }
}
