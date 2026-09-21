using BeyondMovement.Modules.Scheduling.Contracts;
using BeyondMovement.Modules.Scheduling.Domain;
using BeyondMovement.SharedKernel;
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

        // BR-07. NotNull rather than NotEmpty: NotEmpty on a bool? treats false as empty, and
        // false is the answer the Admin most often means to give.
        RuleFor(x => x.DeductSession).NotNull()
            .WithName("deductSession")
            .WithMessage("deductSession is required: choose whether this observation consumes a session.");

        // Deliberately no rule against a start in the future. An observation may be recorded
        // ahead of time, and only the range below constrains how far apart the two dates sit.
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

/// <summary>
/// The athlete's own request, and the Admin's edit of it. Takes an <see cref="IClock"/> because
/// one rule here is about now — unlike <see cref="CreateObservationValidator"/>, which
/// deliberately has none: an Admin records an observation that has already happened, while an
/// athlete asks for one that has not.
/// </summary>
public sealed class SaveObservationRequestValidator : AbstractValidator<SaveObservationRequestRequest>
{
    public SaveObservationRequestValidator(IClock clock)
    {
        RuleFor(x => x.RequestedStartUtc).Must(x => x.Kind == DateTimeKind.Utc)
            .WithName("requestedStartUtc")
            .WithMessage("requestedStartUtc must be UTC.")

            // Only once the value is known to be UTC. Comparing a local or unspecified kind
            // against a UTC now is the comparison that silently passes or fails by the offset.
            .DependentRules(() =>
                RuleFor(x => x.RequestedStartUtc).GreaterThan(_ => clock.UtcNow)
                    .WithName("requestedStartUtc")
                    .WithMessage("Ask for a time in the future."));

        // Required, unlike the Admin's own observation form. An athlete asking to be observed is
        // asking their coach to come somewhere, and where is the whole question.
        RuleFor(x => x.Location)
            .NotEmpty().WithMessage("Say where the observation is.")
            .MaximumLength(ObservationRequest.MaxLocationLength);

        // Optional. Nothing to say to the coach is a normal thing to have.
        RuleFor(x => x.Details).MaximumLength(ObservationRequest.MaxDetailsLength);

        // Only when one was sent: null means the default, not an invalid value.
        When(x => x.RequestedDurationMinutes is not null, () =>
            RuleFor(x => x.RequestedDurationMinutes!.Value)
                .InclusiveBetween(ObservationRequest.MinDurationMinutes, ObservationRequest.MaxDurationMinutes)
                .WithName("requestedDurationMinutes"));
    }
}

/// <summary>
/// The Admin's decision. Every field but the deduction choice is an override, so each is checked
/// only when it was actually sent — an Admin accepting exactly what was asked for sends one field.
/// <para>
/// Deliberately <b>no rule that the start is in the future.</b> A request that waited in the
/// queue until its date passed may still be accepted, and the session it creates is one the
/// Admin is recording after the fact — which is exactly what
/// <see cref="CreateObservationValidator"/> already permits.
/// </para>
/// </summary>
public sealed class AcceptObservationRequestValidator : AbstractValidator<AcceptObservationRequestRequest>
{
    public AcceptObservationRequestValidator()
    {
        // BR-07. NotNull rather than NotEmpty, for the reason CreateObservationValidator gives:
        // NotEmpty on a bool? treats false as empty, and false is a real answer.
        RuleFor(x => x.DeductSession).NotNull()
            .WithName("deductSession")
            .WithMessage("deductSession is required: choose whether this observation consumes a session.");

        When(x => x.RequestedStartUtc is not null, () =>
            RuleFor(x => x.RequestedStartUtc!.Value).Must(x => x.Kind == DateTimeKind.Utc)
                .WithName("requestedStartUtc")
                .WithMessage("requestedStartUtc must be UTC."));

        // An override that clears the location would leave the session with nowhere to be, so an
        // Admin who sends the field has to put something in it. Omitting it keeps what was asked.
        When(x => x.Location is not null, () =>
            RuleFor(x => x.Location!)
                .NotEmpty().WithMessage("Say where the observation is.")
                .MaximumLength(ObservationRequest.MaxLocationLength));

        RuleFor(x => x.Details).MaximumLength(ObservationRequest.MaxDetailsLength);

        When(x => x.RequestedDurationMinutes is not null, () =>
            RuleFor(x => x.RequestedDurationMinutes!.Value)
                .InclusiveBetween(ObservationRequest.MinDurationMinutes, ObservationRequest.MaxDurationMinutes)
                .WithName("requestedDurationMinutes"));
    }
}

public sealed class SaveSessionNoteValidator : AbstractValidator<SaveSessionNoteRequest>
{
    public SaveSessionNoteValidator()
    {
        // Same shape as the content rule below: blankness first, then length on the trimmed
        // value, so "   " is reported as empty rather than as a one-character title.
        RuleFor(x => x.Title)
            .Must(value => !string.IsNullOrWhiteSpace(value))
            .WithName("title")
            .WithMessage("A note needs a title.")
            .DependentRules(() =>
                RuleFor(x => x.Title.Trim())
                    .MaximumLength(SessionNote.MaxTitleLength)
                    .WithName("title")
                    .WithMessage($"A title can be at most {SessionNote.MaxTitleLength} characters."));

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
