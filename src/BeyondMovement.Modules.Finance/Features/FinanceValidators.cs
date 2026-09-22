using BeyondMovement.Modules.Finance.Contracts;
using BeyondMovement.Modules.Finance.Domain;
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

/// <summary>
/// Shared by create and edit, which validate identically — the same arrangement
/// <c>PackageOptionRules</c> uses, and for the same reason: a rule that lived in one but not the
/// other would let an expense be edited into a state it could never have been created in.
/// </summary>
public sealed class SaveExpenseValidator : AbstractValidator<SaveExpenseRequest>
{
    public SaveExpenseValidator()
    {
        RuleFor(x => x.Title)
            .Must(value => !string.IsNullOrWhiteSpace(value))
            .WithName("title")
            .WithMessage("Say what the money went on.")
            .DependentRules(() =>
                RuleFor(x => x.Title.Trim())
                    .MaximumLength(Expense.MaxTitleLength)
                    .WithName("title")
                    .WithMessage($"A title can be at most {Expense.MaxTitleLength} characters."));

        // Strictly positive, unlike a package price, where zero is a real decision. A zero-value
        // expense is a typo that would quietly distort every summary it appeared in.
        RuleFor(x => x.AmountMinor)
            .InclusiveBetween(1, Expense.MaxAmountMinor)
            .WithName("amountMinor")
            .WithMessage("An amount must be a positive number of piastres, and no more than "
                         + $"{Expense.MaxAmountMinor / 100:N0} EGP.");

        RuleFor(x => x.Note)
            .MaximumLength(Expense.MaxNoteLength)
            .WithName("note")
            .WithMessage($"A note can be at most {Expense.MaxNoteLength} characters.");

        // No bound on IncurredOn. A coach entering last year's receipts is doing bookkeeping
        // correctly, and one recording a cost they have already committed to for next month is
        // not making a mistake either - so neither a past nor a future limit would be a rule
        // about money rather than a guess about how somebody works.
    }
}
