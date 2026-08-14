using BeyondMovement.Modules.Identity.Contracts;
using FluentValidation;

namespace BeyondMovement.Modules.Identity.Features.ResetPassword;

public sealed class ResetPasswordValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordValidator()
    {
        RuleFor(x => x.Token).NotEmpty();
        RuleFor(x => x.NewPassword).ApplyPasswordRules();
    }
}
