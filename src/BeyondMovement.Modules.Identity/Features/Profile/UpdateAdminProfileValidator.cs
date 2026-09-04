using BeyondMovement.Modules.Identity.Contracts;
using FluentValidation;

namespace BeyondMovement.Modules.Identity.Features.Profile;

public sealed class UpdateAdminProfileValidator : AbstractValidator<UpdateAdminProfileRequest>
{
    /// <summary>
    /// Digits, spaces and the punctuation real numbers are written with. Deliberately not a
    /// strict format: the coach's clients are international, numbers are entered as people write
    /// them, and a regex that rejects a legitimate number is worse than one that accepts an
    /// oddly punctuated one. Nothing dials this — it is displayed and copied.
    /// </summary>
    private const string PhonePattern = @"^[0-9+()\-.\s]+$";

    public UpdateAdminProfileValidator()
    {
        // Matches the Users.FullName column, so a name that validates always fits.
        RuleFor(x => x.FullName)
            .NotEmpty().WithMessage("Enter your full name.")
            .MaximumLength(200);

        // Only when one was given. Null and "" both mean "no phone number" and are valid.
        When(x => !string.IsNullOrWhiteSpace(x.Phone), () =>
            RuleFor(x => x.Phone)
                .MaximumLength(40)
                .Matches(PhonePattern)
                .WithMessage("Enter a phone number using digits and + ( ) - . only."));
    }
}
