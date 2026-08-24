using BeyondMovement.Modules.Packages.Contracts;
using BeyondMovement.Modules.Packages.Domain;
using FluentValidation;

namespace BeyondMovement.Modules.Packages.Features;

/// <summary>
/// Shared by create and edit, which validate identically — the only difference between them is
/// the version field, and a rule that lived in one but not the other would let a package be
/// edited into a state it could never have been created in.
/// </summary>
internal static class PackageOptionRules
{
    public static void ApplyTo<T>(
        AbstractValidator<T> validator,
        Func<T, string> name,
        Func<T, int> sessions,
        Func<T, long> priceMinor,
        Func<T, IReadOnlyList<string>> features)
    {
        validator.RuleFor(x => name(x))
            .Must(value => !string.IsNullOrWhiteSpace(value))
            .WithName("name")
            .WithMessage("Name is required.")
            .DependentRules(() =>
                validator.RuleFor(x => name(x).Trim())
                    .MaximumLength(PackageOption.MaxNameLength)
                    .WithName("name"));

        validator.RuleFor(x => sessions(x))
            .InclusiveBetween(PackageOption.MinSessions, PackageOption.MaxSessions)
            .WithName("sessions")
            .WithMessage($"Sessions must be a whole number from {PackageOption.MinSessions} to {PackageOption.MaxSessions}.");

        validator.RuleFor(x => priceMinor(x))
            .InclusiveBetween(0, PackagePricing.MaxPriceMinor)
            .WithName("defaultPriceMinor")
            .WithMessage("Price must be a non-negative amount in piastres, and no more than "
                         + $"{PackagePricing.MaxPriceMinor / 100:N0} EGP.");

        validator.RuleFor(x => features(x))
            .NotNull()
            .WithName("features")
            .WithMessage("At least one feature is required.")
            .DependentRules(() =>
            {
                validator.RuleFor(x => features(x).Count)
                    .InclusiveBetween(PackageOption.MinFeatures, PackageOption.MaxFeatures)
                    .WithName("features")
                    .WithMessage($"A package needs {PackageOption.MinFeatures} to {PackageOption.MaxFeatures} features.");

                validator.RuleFor(x => features(x))
                    .Must(list => list.All(f => !string.IsNullOrWhiteSpace(f)))
                    .WithName("features")
                    .WithMessage("A feature cannot be blank.")
                    .Must(list => list.All(f => (f ?? string.Empty).Trim().Length <= PackageOptionFeature.MaxTextLength))
                    .WithName("features")
                    .WithMessage($"A feature can be at most {PackageOptionFeature.MaxTextLength} characters.");
            });
    }
}

public sealed class SavePackageOptionValidator : AbstractValidator<SavePackageOptionRequest>
{
    public SavePackageOptionValidator() =>
        PackageOptionRules.ApplyTo(this, x => x.Name, x => x.Sessions, x => x.DefaultPriceMinor, x => x.Features);
}

public sealed class EditPackageOptionValidator : AbstractValidator<EditPackageOptionRequest>
{
    public EditPackageOptionValidator()
    {
        PackageOptionRules.ApplyTo(this, x => x.Name, x => x.Sessions, x => x.DefaultPriceMinor, x => x.Features);

        RuleFor(x => x.Version)
            .GreaterThan(0)
            .WithMessage("Send the version you last read, so a concurrent edit is not overwritten.");
    }
}

public sealed class PackageOptionVersionValidator : AbstractValidator<PackageOptionVersionRequest>
{
    public PackageOptionVersionValidator() => RuleFor(x => x.Version).GreaterThan(0);
}

public sealed class SetCustomPriceValidator : AbstractValidator<SetCustomPriceRequest>
{
    public SetCustomPriceValidator() =>
        RuleFor(x => x.PriceMinor)
            .InclusiveBetween(0, PackagePricing.MaxPriceMinor)
            .WithMessage("A custom price must be a non-negative amount in piastres.");
}

public sealed class PurchasePackageValidator : AbstractValidator<PurchasePackageRequest>
{
    public PurchasePackageValidator()
    {
        RuleFor(x => x.PackageOptionId).NotEmpty();

        RuleFor(x => x.EndDate)
            .GreaterThanOrEqualTo(x => x.StartDate!.Value)
            .When(x => x.StartDate is not null && x.EndDate is not null)
            .WithName("endDate")
            .WithMessage("End date cannot be before the start date.");

        RuleFor(x => x.Notes).MaximumLength(PurchasedPackage.MaxNotesLength);
    }
}
