using BeyondMovement.Modules.Finance.Contracts;
using FluentValidation;

namespace BeyondMovement.Modules.Finance.Features;

/// <summary>
/// There is exactly one field to validate. Everything else about a purchase — the name, the
/// session count, the features, the price — is resolved server-side, so there is nothing else
/// a client could get wrong.
/// </summary>
public sealed class CreatePurchaseValidator : AbstractValidator<CreatePurchaseRequest>
{
    public CreatePurchaseValidator() =>
        RuleFor(x => x.PackageOptionId)
            .NotEmpty()
            .WithName("packageOptionId")
            .WithMessage("Choose a package option.");
}
