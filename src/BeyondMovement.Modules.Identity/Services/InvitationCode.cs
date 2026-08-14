using System.Security.Cryptography;

namespace BeyondMovement.Modules.Identity.Services;

/// <summary>
/// Generates and normalises invitation codes.
/// <para>
/// A human retypes this from an email, so the alphabet excludes characters that are easy to
/// confuse: O/0, I/1/L, U/V. Ten characters from a 26-symbol alphabet is roughly 47 bits of
/// entropy — brute-forcing it online is hopeless, and validation is rate-limited on top.
/// </para>
/// </summary>
public static class InvitationCode
{
    private const string Alphabet = "ABCDEFGHJKMNPQRSTWXYZ23456789";
    private const int Length = 10;

    /// <summary>Formatted with a dash for legibility: <c>ABCDE-FGHJK</c>.</summary>
    public static string Generate()
    {
        var chars = new char[Length];
        for (var i = 0; i < Length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];

        return string.Concat(new string(chars, 0, 5), "-", new string(chars, 5, 5));
    }

    /// <summary>
    /// Case-insensitive after normalisation, and forgiving about the dash and stray spaces,
    /// because people retype these by hand.
    /// </summary>
    public static string Normalize(string code) =>
        new(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
