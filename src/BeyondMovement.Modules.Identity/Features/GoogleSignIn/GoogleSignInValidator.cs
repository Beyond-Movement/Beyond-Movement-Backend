using BeyondMovement.Modules.Identity.Contracts;
using FluentValidation;

namespace BeyondMovement.Modules.Identity.Features.GoogleSignIn;

public sealed class GoogleSignInValidator : AbstractValidator<GoogleSignInRequest>
{
    public GoogleSignInValidator()
    {
        RuleFor(x => x.IdToken).NotEmpty();
    }
}
