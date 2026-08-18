using BeyondMovement.Modules.Identity.Services;

namespace BeyondMovement.UnitTests.Identity;

/// <summary>
/// The code is retyped by a person from an email, so its shape is part of the contract: the
/// mobile app sizes its input from these rules, and a six-box field would silently truncate.
/// </summary>
public class InvitationCodeTests
{
    [Fact]
    public void A_generated_code_is_ten_characters_formatted_five_dash_five()
    {
        var code = InvitationCode.Generate();

        Assert.Equal(11, code.Length);          // ten characters plus the dash
        Assert.Equal('-', code[5]);
        Assert.Equal(10, InvitationCode.Normalize(code).Length);
    }

    [Fact]
    public void The_alphabet_excludes_characters_that_are_misread_when_retyped()
    {
        // O/0, I/1, L/1 and U/V are the pairs people confuse. Generated over many codes so a
        // single lucky draw cannot pass this.
        var everything = string.Concat(Enumerable.Range(0, 200).Select(_ => InvitationCode.Generate()));

        foreach (var forbidden in "OILUV01")
            Assert.DoesNotContain(forbidden, everything);
    }

    [Theory]
    [InlineData("MRPZB-AXZYY")]
    [InlineData("MRPZBAXZYY")]      // the dash is optional
    [InlineData("mrpzb-axzyy")]     // case does not matter
    [InlineData("  MRPZB AXZYY  ")] // nor do spaces, however the athlete types them
    [InlineData("MRPZB–AXZYY")]     // an en dash, which some mail clients substitute
    public void Every_way_an_athlete_might_type_a_code_normalises_the_same(string typed)
    {
        Assert.Equal("MRPZBAXZYY", InvitationCode.Normalize(typed));
    }

    [Fact]
    public void Two_generated_codes_do_not_collide()
    {
        var codes = Enumerable.Range(0, 500).Select(_ => InvitationCode.Generate()).ToList();

        Assert.Equal(codes.Count, codes.Distinct().Count());
    }
}
