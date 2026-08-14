using BeyondMovement.Modules.Identity.Contracts;
using FluentValidation;

namespace BeyondMovement.Modules.Identity.Features.ChangePassword;

public sealed class ChangePasswordValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty();
        RuleFor(x => x.NewPassword).ApplyPasswordRules();

        RuleFor(x => x.NewPassword)
            .NotEqual(x => x.CurrentPassword)
            .WithMessage("The new password must be different from the current one.");
    }
}
