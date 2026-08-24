using BeyondMovement.Modules.Packages.Contracts;
using BeyondMovement.Modules.Scheduling.Contracts;
using BeyondMovement.Modules.Scheduling.Domain;
using BeyondMovement.SharedKernel;

namespace BeyondMovement.Api.Attendance;

/// <summary>
/// The outcome the Admin is recording. Only these two: Scheduled is where a session starts and
/// Cancelled has its own endpoint, because cancelling also has to reach Calendly.
/// </summary>
/// <param name="Outcome">
/// <c>Attended</c> or <c>NoShow</c>. Sending anything else is VALIDATION_FAILED.
/// </param>
public sealed record MarkAttendanceRequest(SessionStatus Outcome = SessionStatus.Attended);

/// <summary>
/// Where a session sits in the package it belongs to — the "Session 7 of 12" the Session Details
/// screen shows.
/// </summary>
/// <param name="SessionNumber">
/// One-based. For a session that has been attended and consumed one, this is its own position in
/// the order the package was used. For a session that has not been resolved yet, it is the
/// position it <em>would</em> take — the used count plus one — which is what the screen wants to
/// show before the coach taps Mark as Attended. It is null for a session that consumed nothing
/// and never will, such as a short observation or a cancelled session, where no position exists
/// to state.
/// </param>
public sealed record SessionPackageProgress(
    Guid PackageId,
    string PackageName,
    int? SessionNumber,
    int TotalSessions,
    int UsedSessions,
    int RemainingSessions);

/// <summary>
/// What Mark as Attended returns: the session as it now stands, and the package balance as it
/// now stands.
/// <para>
/// The balance is returned rather than left for the client to re-read, because the two facts
/// changed together in one transaction and sending them together is the only way the app can
/// show a state that actually existed. A re-read can interleave with another change.
/// </para>
/// </summary>
/// <param name="Package">
/// Null when the session consumed nothing and the athlete has no active package — a short
/// observation, or a no-show under the default policy. A session that <em>does</em> consume
/// always has one, because it cannot be recorded without it.
/// </param>
public sealed record AttendanceResponse(
    SessionResponse Session,
    int ConsumedSessionCount,
    PurchasedPackageResponse? Package,
    SessionPackageProgress? Progress);

/// <summary>
/// Deployment policy that is not per athlete (architecture A-04). Bound from the
/// <c>Features</c> configuration section, so <c>Features__NoShowDeducts</c> sets it in the
/// environment exactly as the architecture document lists it.
/// </summary>
public sealed class FeatureOptions
{
    public const string SectionName = "Features";

    /// <summary>
    /// Whether a no-show consumes a session. <b>False by default</b>, which is the specified
    /// default: the athlete who did not turn up has not had coaching, and charging them for it
    /// is a decision the coach makes deliberately rather than one the software assumes.
    /// </summary>
    public bool NoShowDeducts { get; set; }
}

public static class AttendanceErrors
{
    public const string ActivePackageNotFoundCode = "ACTIVE_PACKAGE_NOT_FOUND";
    public const string ConcurrencyConflictCode = "CONCURRENCY_CONFLICT";

    /// <summary>
    /// Distinct from NO_SESSIONS_REMAINING, because the coach's next action is different: this
    /// athlete has never had a package, or their last one is closed, so one must be sold before
    /// attendance can be recorded. NO_SESSIONS_REMAINING means renew.
    /// </summary>
    public static readonly Error ActivePackageNotFound = new(ActivePackageNotFoundCode,
        "This athlete has no active package to deduct a session from.", 409);

    /// <summary>
    /// Two Mark as Attended requests raced and this one lost. Nothing was deducted for it — the
    /// other request's deduction stands, which is the whole point of exactly-once. Re-read the
    /// session rather than retrying blindly.
    /// </summary>
    public static readonly Error ConcurrencyConflict = new(ConcurrencyConflictCode,
        "This session changed while you were marking it. Reload and try again.", 409);

    public static readonly string[] AllCodes = [ActivePackageNotFoundCode];
}
