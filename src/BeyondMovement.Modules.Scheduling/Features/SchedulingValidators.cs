using BeyondMovement.Modules.Scheduling.Contracts;
using FluentValidation;

namespace BeyondMovement.Modules.Scheduling.Features;

public sealed class BookSessionValidator : AbstractValidator<BookSessionRequest>
{
    public BookSessionValidator()
    {
        RuleFor(x => x.EventTypeId).NotEmpty().MaximumLength(100);
        RuleFor(x => x.StartUtc).Must(x => x.Kind == DateTimeKind.Utc).WithMessage("StartUtc must be UTC.");
        RuleFor(x => x.TimeZone).NotEmpty().MaximumLength(100);
        RuleFor(x => x.LocationKind).MaximumLength(100);
        RuleFor(x => x.Location).MaximumLength(500);
    }
}
