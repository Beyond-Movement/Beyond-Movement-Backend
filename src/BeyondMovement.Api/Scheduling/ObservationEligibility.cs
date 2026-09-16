using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Packages.Domain;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Api.Scheduling;

public static class ObservationEligibilityErrors
{
    public const string ObservationsNotIncludedCode = "OBSERVATIONS_NOT_INCLUDED";

    /// <summary>
    /// The athlete's package does not let them ask to be observed — either they have no active
    /// package at all, or the one they have was not sold with
    /// <see cref="PackageFeatureCode.Observations"/>.
    /// <para>
    /// <b>One code for both cases, deliberately.</b> The athlete's next step is the same either
    /// way — talk to their coach about a package that includes observations — and two codes for
    /// one condition is how a client ends up handling only one of them (the reasoning behind
    /// <c>OBSERVATION_REQUEST_NOT_PENDING</c> being one code for four transitions).
    /// </para>
    /// <para>
    /// 403 rather than 409: nothing about the request conflicts with the current state of
    /// anything, and there is no version to re-read that would make it succeed. The athlete is
    /// simply not permitted this action. It is <b>not</b> a role failure — the caller is a
    /// perfectly valid athlete — so the app should not treat it as a sign-in problem.
    /// </para>
    /// </summary>
    public static readonly Error ObservationsNotIncluded = new(ObservationsNotIncludedCode,
        "Your package does not include observations. Ask your coach about a package that does.",
        403);

    public static readonly string[] AllCodes = [ObservationsNotIncludedCode];
}

/// <summary>
/// Whether an athlete may ask to be observed.
/// <para>
/// This lives in the composition root because it spans modules: the question is about Scheduling
/// and the answer is in Packages, and a module may not reference another (CLAUDE.md section 4).
/// It is the same reason <c>AttendanceErrors.ACTIVE_PACKAGE_NOT_FOUND</c> is declared in
/// <c>Api/Attendance</c> rather than inside either module.
/// </para>
/// <para>
/// <b>What it reads, and what it must never read.</b> The answer comes from the athlete's active
/// <see cref="PurchasedPackage"/> and its snapshot of recognised feature codes. Not the catalogue
/// option, which the coach may have edited since — an athlete who bought observations keeps them,
/// and one who did not does not gain them when the template changes. Not the Finance purchase
/// record, whose feature snapshot is empty for every package predating Phase 8. And never the
/// display text of a feature, which is the coach's to reword in any language.
/// </para>
/// <para>
/// <b>Where it applies.</b> Only to an athlete creating a request. An Admin recording an
/// observation directly (<c>POST /sessions/observations</c>, A-03) and an Admin accepting a
/// request that already exists are both untouched: the coach decides what the coach observes, and
/// a package feature is a rule about what the athlete may ask for. Revising or cancelling a
/// request that is already Pending is untouched too — the asking already happened, and a package
/// that lapses in the meantime is the coach's call to decline, not a reason to freeze the
/// athlete's own request.
/// </para>
/// </summary>
public sealed class ObservationEligibility(AppDbContext db)
{
    /// <summary>
    /// Success when this athlete's active package includes
    /// <see cref="PackageFeatureCode.Observations"/>; otherwise
    /// <see cref="ObservationEligibilityErrors.ObservationsNotIncluded"/>.
    /// <para>
    /// Read outside the create transaction, which is deliberate: eligibility is a fact about a
    /// package that lasts weeks, not a balance two taps can race over, and there is nothing to
    /// serialise. A package closed in the same instant as a request being filed leaves a Pending
    /// request the Admin can decline — the one outcome, and a harmless one.
    /// </para>
    /// </summary>
    public async Task<Result> MayRequestAsync(Guid athleteProfileId, CancellationToken ct)
    {
        // The whole row rather than a projection of the snapshot column: IncludedFeatures is a
        // computed view over a field-only collection and is not something LINQ can select, and
        // asking the entity through Includes keeps the containment test in the domain.
        var package = await db.PurchasedPackages.AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.AthleteProfileId == athleteProfileId
                     && x.Status == PurchasedPackageStatus.Active, ct);

        // No active package and an active package that was not sold with observations are the
        // same answer to the athlete, and the same error code.
        return package?.Includes(PackageFeatureCode.Observations) == true
            ? Result.Success()
            : Result.Failure(ObservationEligibilityErrors.ObservationsNotIncluded);
    }
}
