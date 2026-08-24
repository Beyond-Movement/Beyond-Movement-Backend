using BeyondMovement.Modules.Scheduling.Contracts;
using BeyondMovement.Modules.Scheduling.Domain;
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

public sealed class CreateObservationValidator : AbstractValidator<CreateObservationRequest>
{
    /// <summary>
    /// A day. An observation is a competition or a training session, not a training camp, and a
    /// range longer than this is a date typed into the wrong field — which would otherwise be
    /// stored as a session of several thousand minutes.
    /// </summary>
    public const int MaxObservationHours = 24;

    public CreateObservationValidator()
    {
        RuleFor(x => x.AthleteProfileId).NotEmpty();

        RuleFor(x => x.StartUtc).Must(x => x.Kind == DateTimeKind.Utc)
            .WithMessage("StartUtc must be UTC.");

        RuleFor(x => x.EndUtc).Must(x => x.Kind == DateTimeKind.Utc)
            .WithMessage("EndUtc must be UTC.");

        RuleFor(x => x.EndUtc).GreaterThan(x => x.StartUtc)
            .WithMessage("EndUtc must be after StartUtc.");

        RuleFor(x => x).Must(x => (x.EndUtc - x.StartUtc).TotalHours <= MaxObservationHours)
            .WithName("endUtc")
            .WithMessage($"An observation may not be longer than {MaxObservationHours} hours.");

        RuleFor(x => x.LocationOrPlatform).MaximumLength(500);
    }
}

public sealed class SaveSessionNoteValidator : AbstractValidator<SaveSessionNoteRequest>
{
    public SaveSessionNoteValidator()
    {
        RuleFor(x => x.Content)
            .Must(value => !string.IsNullOrWhiteSpace(value))
            .WithName("content")
            .WithMessage("A note cannot be empty.")
            .DependentRules(() =>
                RuleFor(x => x.Content.Trim())
                    .MaximumLength(SessionNote.MaxContentLength)
                    .WithName("content"));
    }
}
