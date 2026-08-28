using BeyondMovement.Modules.Packages.Contracts;
using BeyondMovement.Modules.Scheduling.Contracts;
using BeyondMovement.SharedKernel;
using System.Text.Json.Serialization;

namespace BeyondMovement.Api.Attendance;

/// <summary>
/// The only outcomes accepted by the attendance command. SessionStatus is deliberately not used
/// in the request because Scheduled and Cancelled are not attendance decisions.
/// </summary>
public enum AttendanceOutcome { Attended, NoShow }

public sealed class MarkAttendanceRequest
{
    private bool? _deductSession;

    /// <summary>Attended or NoShow. Defaults to Attended.</summary>
    public AttendanceOutcome Outcome { get; init; } = AttendanceOutcome.Attended;

    /// <summary>
    /// Required for NoShow: true consumes exactly one package session and false consumes none.
    /// Must be omitted for Attended, whose deduction follows the attendance/observation rules.
    /// </summary>
    public bool? DeductSession
    {
        get => _deductSession;
        init
        {
            _deductSession = value;
            HasDeductSession = true;
        }
    }

    /// <summary>
    /// Distinguishes an omitted property from an explicitly supplied null. Both are invalid for
    /// NoShow, while any supplied value (including null) is invalid for Attended.
    /// </summary>
    [JsonIgnore]
    public bool HasDeductSession { get; private init; }
}

/// <summary>
/// Where a session sits in the package it belongs to — the "Session 7 of 12" the Session Details
/// screen shows.
/// </summary>
/// <param name="SessionNumber">
/// One-based. For a session that has been attended and consumed one, this is its own position in
/// the order the package was used. For a session that has not been resolved yet, it is the
/// position it <em>would</em> take — the used count plus one — which is what the screen wants to
/// show before the coach taps Mark as Attended. It is null for a session that consumed nothing
/// and never will, such as a non-deducting observation or a cancelled session, where no position exists
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
/// observation, or a no-show explicitly marked not to deduct. A session that <em>does</em> consume
/// always has one, because it cannot be recorded without it.
/// </param>
public sealed record AttendanceResponse(
    SessionResponse Session,
    int ConsumedSessionCount,
    PurchasedPackageResponse? Package,
    SessionPackageProgress? Progress);

/// <summary>
/// Retained as a possible future/default preference. The attendance API currently requires the
/// coach's explicit per-session choice for every no-show and does not read this setting.
/// </summary>
public sealed class FeatureOptions
{
    public const string SectionName = "Features";

    /// <summary>
    /// A future/default preference for whether a no-show consumes a session.
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
