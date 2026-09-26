using BeyondMovement.Modules.Identity.Contracts;
using FluentValidation;

namespace BeyondMovement.Modules.Identity.Features.Profile;

public sealed class UpdateAdminProfileValidator : AbstractValidator<UpdateAdminProfileRequest>
{
    public UpdateAdminProfileValidator()
    {
        // Matches the Users.FullName column, so a name that validates always fits.
        RuleFor(x => x.FullName)
            .NotEmpty().WithMessage("Enter your full name.")
            .MaximumLength(200);

        // Shared with the athlete's profile edit — one parser, one set of rules, in PhonePolicy.
        RuleFor(x => x.Phone).ApplyPhoneRules();
    }
}
