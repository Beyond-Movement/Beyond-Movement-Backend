using FluentValidation;

namespace BeyondMovement.Modules.Identity;

/// <summary>
/// Software architecture section 7.2: minimum length 8, checked against a common-password
/// list, and deliberately no composition rules — arbitrary complexity requirements produce
/// worse passwords (current NIST guidance).
/// <para>One definition, applied by both reset-password and change-password.</para>
/// </summary>
public static class PasswordPolicy
{
    public const int MinimumLength = 8;
    public const int MaximumLength = 256;

    // The short head of the common-password lists. Worth replacing with a fuller
    // source (e.g. the Pwned Passwords range API) before release.
    private static readonly HashSet<string> CommonPasswords = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "password1", "password123", "12345678", "123456789", "1234567890",
        "qwerty123", "qwertyuiop", "letmein", "welcome", "welcome1", "admin123",
        "iloveyou", "sunshine", "princess", "football", "baseball", "monkey123",
        "abc12345", "passw0rd", "trustno1", "dragon123", "superman", "starwars",
        "michael1", "shadow123", "master123", "changeme", "secret123", "computer"
    };

    public static bool IsCommon(string password) => CommonPasswords.Contains(password);

    public static IRuleBuilderOptions<T, string> ApplyPasswordRules<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty()
            .MinimumLength(MinimumLength)
                .WithMessage($"Password must be at least {MinimumLength} characters.")
            .MaximumLength(MaximumLength)
            .Must(password => !IsCommon(password))
                .WithMessage("That password is too common. Choose something less predictable.");
}
