using BeyondMovement.Modules.Finance.Contracts;
using BeyondMovement.Modules.Packages.Contracts;

namespace BeyondMovement.Api.Finance;

/// <summary>
/// The result of confirming a payment: the purchase and the package it produced, as they both
/// stand after one transaction.
/// <para>
/// Both are returned rather than left for the app to re-read, for the same reason Mark as
/// Attended returns the session and the package together — they changed together, and a re-read
/// can interleave with another change, so sending them together is the only way the app can show
/// a state that actually existed.
/// </para>
/// </summary>
/// <param name="AlreadyPaid">
/// True when this request found the purchase already paid and changed nothing. The endpoint is
/// idempotent, so a repeat is a success rather than an error, and <see cref="Package"/> is the
/// package the first request created — never a second one. A client that wants to show
/// "Payment confirmed" only once can branch on this; one that does not can ignore it.
/// </param>
public sealed record MarkPurchasePaidResponse(
    PackagePurchaseResponse Purchase,
    PurchasedPackageResponse Package,
    bool AlreadyPaid);

/// <summary>
/// The result of an athlete selecting an option.
/// </summary>
/// <param name="Created">
/// True when this opened a new pending request (201), false when it revised the one the athlete
/// already had (200). Not part of the JSON body — the endpoint turns it into the status code.
/// </param>
public sealed record PurchaseSelectionResult(PackagePurchaseResponse Purchase, bool Created);
