using BeyondMovement.Modules.Identity.Contracts;
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
        When(x => !string.IsNullOrWhiteSpace(x.Password), () =>
        {
            RuleFor(x => x.Password!).ApplyPasswordRules();

            RuleFor(x => x.FullName)
                .NotEmpty()
                .WithMessage("Full name is required when registering with a password.")
                .MaximumLength(200);
        });

        RuleFor(x => x.FullName).MaximumLength(200);
    }
}

public sealed class CompleteProfileValidator : AbstractValidator<CompleteProfileRequest>
{
    public CompleteProfileValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Gender).MaximumLength(40);
        RuleFor(x => x.Sport).MaximumLength(100);

        RuleFor(x => x.DateOfBirth)
            .Must(date => date is null || date < DateOnly.FromDateTime(DateTime.UtcNow))
            .WithMessage("Date of birth must be in the past.");
    }
}
