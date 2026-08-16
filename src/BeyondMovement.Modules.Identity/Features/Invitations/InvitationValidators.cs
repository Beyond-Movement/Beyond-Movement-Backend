using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.SharedKernel;
using FluentValidation;

namespace BeyondMovement.Modules.Identity.Features.Invitations;

public sealed class CreateInvitationValidator : AbstractValidator<CreateInvitationRequest>
{
    public CreateInvitationValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
    }
}

public sealed class RegisterValidator : AbstractValidator<RegisterRequest>
{
    public RegisterValidator()
    {
        RuleFor(x => x.RegistrationToken).NotEmpty();

        // Password rules apply only on the password path; Google registration has none.
        // Nothing else is validated here: registration establishes authentication, and the
        // athlete's details are Complete Profile's business.
        When(x => !string.IsNullOrWhiteSpace(x.Password), () =>
            RuleFor(x => x.Password!).ApplyPasswordRules());
    }
}

/// <summary>
/// Every field is required, enforced here rather than trusted from the app: the mobile client
/// is not the only thing that can reach this endpoint, and <c>profileCompleted: true</c> is a
/// promise the rest of the system reads.
/// </summary>
public sealed class CompleteProfileValidator : AbstractValidator<CompleteProfileRequest>
{
    /// <summary>Older than the oldest verified human, so a typo is caught but nobody real is refused.</summary>
    private const int MaxAgeYears = 120;

    public CompleteProfileValidator(IClock clock)
    {
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Sport).NotEmpty().MaximumLength(100);

        // A value outside the enum never binds, so this catches the one case that does reach
        // us: default(Gender) from a caller that omitted the field entirely.
        RuleFor(x => x.Gender)
            .IsInEnum()
            .WithMessage("Gender must be one of: Female, Male.");

        var today = DateOnly.FromDateTime(clock.UtcNow);

        RuleFor(x => x.DateOfBirth)
            .NotEqual(default(DateOnly))
            .WithMessage("Date of birth is required.")
            .LessThan(today)
            .WithMessage("Date of birth must be in the past.")
            .GreaterThan(today.AddYears(-MaxAgeYears))
            .WithMessage($"Date of birth must be within the last {MaxAgeYears} years.");
    }
}
