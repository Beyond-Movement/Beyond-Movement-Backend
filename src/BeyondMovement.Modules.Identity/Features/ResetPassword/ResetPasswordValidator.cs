using BeyondMovement.Modules.Identity.Contracts;
using FluentValidation;

namespace BeyondMovement.Modules.Identity.Features.ResetPassword;

/// <summary>
/// Software architecture section 7.2: minimum length 8, checked against a common-password
/// list, and deliberately no composition rules — arbitrary complexity requirements produce
/// worse passwords (current NIST guidance).
/// </summary>
public sealed class ResetPasswordValidator : AbstractValidator<ResetPasswordRequest>
{
    public const int MinimumPasswordLength = 8;

    // The short head of the common-password lists. Worth replacing with a fuller
    // list (e.g. the Pwned Passwords range API) before release.
    private static readonly HashSet<string> CommonPasswords = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "password1", "password123", "12345678", "123456789", "1234567890",
        "qwerty123", "qwertyuiop", "letmein", "welcome", "welcome1", "admin123",
        "iloveyou", "sunshine", "princess", "football", "baseball", "monkey123",
        "abc12345", "passw0rd", "trustno1", "dragon123", "superman", "starwars",
        "michael1", "shadow123", "master123", "changeme", "secret123", "computer"
    };

    public ResetPasswordValidator()
    {
        RuleFor(x => x.Token).NotEmpty();

        RuleFor(x => x.NewPassword)
            .NotEmpty()
            .MinimumLength(MinimumPasswordLength)
                .WithMessage($"Password must be at least {MinimumPasswordLength} characters.")
            .MaximumLength(256)
            .Must(p => !CommonPasswords.Contains(p))
                .WithMessage("That password is too common. Choose something less predictable.");
    }
}
