# API contract changelog

Every change to a request or response shape is a breaking change for the Flutter app.
Record it here, regenerate `openapi.yaml`, and tell the mobile developer.

To regenerate: run the API, fetch `GET /openapi/v1.json`, and convert it to YAML.

---

## Phase 8 — Package purchase and manual payment

**Purely additive.** Six new endpoints, three new schemas, three new error codes. **No existing
shape changed** — `PurchasedPackageResponse`, `PackageOptionResponse`, `CatalogueItemResponse`
and `SessionResponse` are all byte-for-byte what they were. Nothing the app already reads moved.

This is the money half of the package model that Phase 4 deferred and Phase 6 left a hole for.
The flow the product actually has: the athlete picks an option and gets a **pending** request
plus the coach's InstaPay details; they pay outside this platform, which never sees the money;
the Admin confirms receipt, and **only then does the package exist**.

### C-01 is closed: `Pending | Paid`

The open decision — three values from the Product Specification (`Unpaid | PartiallyPaid |
Paid`) or two from the UI document (`Pending | Paid`) — is **resolved as two**, on the client's
ruling. `PartiallyPaid` does not exist and cannot be represented: nothing in this product can
record a part payment. There is also **no cancelled state**.

The status lives on the **purchase**, not on the package. A `PurchasedPackage` deliberately did
**not** gain a `paymentStatus` field, because a package is created only when its purchase turns
`Paid` — so the field would read `Paid` on every row that could ever exist, and a field that
never varies is one that drifts. The Athlete Profile's payment badge reads the athlete's latest
purchase.

This supersedes `software-architecture.md` §6.4 (`PaymentStatus: Unpaid, PartiallyPaid, Paid`),
§6.2 (a `PaymentStatus` column on the purchased package) and §4.9 of the specification
("Admin can mark payment as Unpaid, Partially Paid, or Paid"). Those documents have been
amended; where any other copy still disagrees, this changelog is what shipped.

### The endpoints

| Method | Path | Role | Purpose |
|---|---|---|---|
| POST | `/api/v1/me/purchases` | T | Select an option; creates or replaces the pending request |
| GET | `/api/v1/me/purchases/current` | T | The athlete's pending purchase, else their latest |
| GET | `/api/v1/purchases` | A | Every purchase, filterable by `status` and `athleteId` |
| GET | `/api/v1/purchases/{id}` | A | One purchase |
| POST | `/api/v1/purchases/{id}/mark-paid` | A | Confirm payment; creates the package |
| GET | `/api/v1/payments/instapay-instructions` | B | QR code, payment link, instructions |

**These are `/purchases`, not the `/payments` of architecture §14.7.** That section was drawn
for a different model — a separate append-only `Payments` table with the package's payment
status derived from the sum of confirmed payments. With one manual confirmation and two states,
the purchase *is* the payment record, so there is nothing for a `/payments` collection to return
that `/purchases` does not. `PATCH /packages/{id}/payment-status` and `POST
/packages/{id}/payments` from that section **do not exist and will not**. Only
`/payments/instapay-instructions` kept its §14.7 path, unchanged.

**Expenses are not in this phase.** `/expenses` from §14.7 is not built.

### The price is snapshotted, and never sent by the client

`CreatePurchaseRequest` has exactly one field:

```json
{ "packageOptionId": "…" }
```

There is no price, no name, no session count and no feature list in the request, and sending
them anyway does nothing — a test pins that. The server resolves the athlete's effective price
with the **same Phase 4 rule** that produced the number already shown in `GET /api/v1/catalogue`
(custom override → loyalty → default, with loyalty rounded to the nearest tenth of a pound), and
then **copies** the name, session count, ordered features, price and currency onto the purchase.

**Flutter must not calculate loyalty, custom pricing, discounts or rounding.** It does not know
the rule and must not learn it. If a price looks wrong, it is a backend bug.

Because the values are copied rather than looked up, **editing the catalogue option or the
athlete's pricing afterwards cannot change a purchase that already exists**. A test renames,
reprices and re-features an option after selection and asserts the purchase and the package it
later produces are untouched. `packageOptionId` on the response is provenance only, and is
`null` if the option was deleted — the snapshot above it is complete, so the app never needs to
follow it.

Money is an integer count of piastres, as everywhere else. `priceMinor: 400000` is 4,000.00 EGP.
Divide by 100 for display, never for arithmetic.

### One pending purchase per athlete, and how a wrong choice is corrected

An athlete may hold **at most one** pending purchase. Posting a *different* option while one is
pending does not open a second request and is not an error — it **replaces the selection on the
existing one**, keeping its id and re-pricing it under today's rules:

- **201 Created** — a new pending request.
- **200 OK** — the existing pending request, revised. Same `id` as before.

Both return the same `PackagePurchaseResponse`, so the app can treat them identically and simply
render the body; the status code is only there for clients that want to tell the cases apart.

This exists because **there is no Cancel action and no `Cancelled` status**. Without replacement
an athlete who tapped the wrong package would be stuck behind their own request until the coach
intervened. Replacement is allowed **only while `Pending`** — once paid, the snapshot is the
record of what somebody paid for and nothing may edit it.

### `Pending → Paid` is the only transition, and it is idempotent

`POST /api/v1/purchases/{id}/mark-paid` is Admin-only and does all of this in **one
transaction**:

1. Records who confirmed it (`paidByUserId`, from the token) and when (`paidAtUtc`).
2. Creates the `PurchasedPackage` **from the stored snapshot** — the catalogue is not consulted.
3. Links the two (`purchasedPackageId`).
4. Re-checks **BR-03**, one active package per athlete.

The response carries both, as they stand afterwards:

```json
{
  "purchase":    { "...": "PackagePurchaseResponse, now Paid" },
  "package":     { "...": "PurchasedPackageResponse, Active" },
  "alreadyPaid": false
}
```

Both are returned rather than left for the app to re-read, for the same reason Mark as Attended
returns the session and the package together: they changed together, and a re-read can
interleave with another change.

**Repeating the request is safe and is not an error.** A second call returns `200` with the same
purchase, the **same `package.id`**, and `alreadyPaid: true`. It never produces a second package
— not on a double tap, not on a retry after a timeout, and not under genuine concurrency: a test
fires eight simultaneous confirmations and asserts exactly one did the work, all eight named the
same package, and the database holds one.

> **Note the difference from Phase 6.** Repeating `POST /sessions/{id}/attend` is `409
> SESSION_ALREADY_ATTENDED` — do not retry. Repeating `mark-paid` is `200` — safe to retry.
> Attendance consumes something and must never do it twice; confirming a payment is a statement
> of fact that is either already recorded or not. Do not copy the attendance retry logic here.

**There is no way back.** A paid purchase never returns to `Pending`. There is no cancel, no
reopen and no unpay — no such route exists, which a test asserts by calling four of them and
getting 404 each time. Corrections and refunds are outside this scope; today they are a database
operation, not an API call.

**The athlete never activates a package.** There is no athlete-facing endpoint that creates,
activates or pays for one. The only way a package comes into existence is an Admin confirming a
purchase, or an Admin recording one directly.

### BR-03 is enforced twice, and a conflict leaves the purchase Pending

An athlete with an active package cannot buy the next one until it is closed or runs out.

- **At selection** — `POST /me/purchases` returns `409 ACTIVE_PACKAGE_EXISTS`. Early, so the
  athlete is told *before* they are sent to InstaPay rather than after they have paid.
- **At confirmation** — `mark-paid` returns `409 ACTIVE_PACKAGE_EXISTS`, and **the purchase is
  left `Pending`**, with `purchasedPackageId` and `paidAtUtc` still null. Nothing is half-done.
  Close the current package and confirm again; a test walks exactly that recovery.

The second check is not redundant: an Admin can record a package directly while a request sits
pending, which is precisely the window it covers.

### Admin-recorded packages now have payment history

`POST /api/v1/athletes/{athleteId}/packages` — the Phase 6 endpoint where the Admin records a
sale directly — **still exists and is unchanged in request and response**. It now also writes a
`Paid` purchase beside the package it creates, in the same transaction, with
`origin: "AdminDirect"`.

Without this, the payments screen would be blind to every package that did not come through the
app, and "is this athlete paid up?" would have two different answers depending on which screen
asked. Such a purchase is born `Paid` because recording it *is* the confirmation — there is
nothing to await.

`origin` is therefore on every purchase:

| `origin` | Means |
|---|---|
| `Athlete` | Chosen in the app, confirmed by the Admin after an InstaPay transfer |
| `AdminDirect` | Recorded by the Admin — cash, bank transfer, agreed off-app |

**Packages that existed before this phase were backfilled** as `Paid` / `AdminDirect` purchases,
so the payments screen is complete from day one rather than starting empty beside athletes who
are visibly training. Two fields on a backfilled row cannot be recovered and were not invented:

- **`paidByUserId` is `null`.** Which Admin confirmed the money is not recorded anywhere, and
  naming the seeded Admin would be a guess written into an audit trail.
- **`features` is an empty array.** The snapshot must be what the athlete was shown at purchase
  time; the option's features today may have been edited since, so copying them now would
  fabricate a snapshot rather than restore one.

A legacy row is therefore recognisable as `origin: "AdminDirect"` with `paidByUserId: null`.
**The app must tolerate an empty `features` array** on a paid purchase and fall back to the
package name and session count.

### InstaPay is configuration, not code

`GET /api/v1/payments/instapay-instructions` is available to **both roles** — the athlete needs
somewhere to pay, and the Admin needs to see what the athlete is being shown.

```json
{
  "qrImageUrl":      "https://…/instapay-qr.png",
  "paymentUrl":      "https://ipn.eg/S/…",
  "recipientName":   "Beyond Movement",
  "recipientHandle": "beyondmovement@instapay",
  "instructions":    ["Open InstaPay and scan the QR code.", "…"]
}
```

Every value comes from configuration (`Payments:InstaPay:*`) and **none of it is hard-coded**.
The destination is the coach's own, it can change without an app release, and a payment
destination baked into a binary is one that cannot be corrected. **The app must read it from
here and must not embed it.**

- Any field may be `null` when not configured; `instructions` is an ordered list and may be
  empty. Render the steps in the order given.
- `qrImageUrl` is an absolute URL served **without authentication**, because an image request
  cannot carry a bearer token.
- Until real values are supplied the endpoint returns **`503 INSTAPAY_NOT_CONFIGURED`**. That is
  a 503 and not a 404 on purpose: the feature exists and will work once the coach's details are
  in. **Show a "contact your coach" state and keep the Pay button** — do not treat it as a
  missing feature and hide it permanently.

The platform never proxies InstaPay, never sees a transaction, and never verifies one
automatically (**BR-14**). It hands the athlete a destination and waits for the Admin.

### Error codes added

| Code | Status | Meaning |
|---|---|---|
| `PURCHASE_NOT_FOUND` | 404 | No such purchase, **or it belongs to another coach** |
| `INSTAPAY_NOT_CONFIGURED` | 503 | Payment details have not been supplied yet |

Only two. There is deliberately **no** code for "this purchase is already paid": no request can
produce that state. Repeating `mark-paid` is an idempotent `200`, and an athlete can only revise
a purchase that is still `Pending`. A 409 no client can receive would only invite handling for a
case that cannot happen, so it is not in the contract.

Codes the catalogue already owns are **reused rather than duplicated** with a payment-flavoured
name — two names for one condition is how clients end up handling only one of them:

| Code | Status | When |
|---|---|---|
| `PACKAGE_OPTION_NOT_FOUND` | 404 | Unknown option, or another coach's |
| `PACKAGE_OPTION_ARCHIVED` | 409 | The option was withdrawn from sale |
| `ACTIVE_PACKAGE_EXISTS` | 409 | BR-03, at selection **and** at confirmation |
| `ATHLETE_NOT_FOUND` | 404 | `?athleteId=` names an athlete this coach does not have |
| `CONCURRENCY_CONFLICT` | 409 | Two selections raced; retry, and the retry revises |
| `VALIDATION_FAILED` | 400 | Missing or empty `packageOptionId` |

### Authorization

- `/api/v1/purchases*` is **Admin only**. An athlete reaching one gets **403**, including for
  their own purchase — listing every purchase and confirming payment are the coach's, and an
  athlete must never mark their own paid. A test asserts all three routes.
- `/api/v1/me/purchases*` is **athlete only**; an Admin gets **403**. It is always scoped to the
  token and takes no athlete id, so an athlete cannot name another's purchase.
- Another coach's purchase is **404, not 403**, so an id cannot be probed for existence.
- `?athleteId=` with an unknown athlete is **404 `ATHLETE_NOT_FOUND`**, not an empty list — an
  empty list is a real answer, and a bad id must not be mistaken for one.

### Mobile integration notes

1. **Regenerate the client.** Additive, but there are three new schemas and two new enums
   (`PurchasePaymentStatus`, `PurchaseOrigin`).
2. **Never compute a price.** Send `packageOptionId` and render `priceMinor` / `currency` from
   the response. Loyalty, overrides and rounding are server-side and are not reproducible in the
   app by design.
3. **Render the purchase from its snapshot, not from the catalogue.** A purchase carries its own
   name, session count and features precisely so it stays correct after the option changes. Do
   not re-fetch the option to fill in a purchase screen.
4. **Selecting again replaces.** Do not add a "cancel my request" affordance — there is no such
   endpoint. Let the athlete pick a different package; the pending request follows. Expect `201`
   the first time and `200` after, with the same id.
5. **`mark-paid` is safe to retry.** On a network timeout, resend it. Use `alreadyPaid` to decide
   whether to show a "payment confirmed" toast a second time. Do **not** copy the Phase 6
   attendance retry logic, which must not be retried.
6. **Handle `409 ACTIVE_PACKAGE_EXISTS` on both screens** — when the athlete selects, and when
   the Admin confirms. On the Admin side the purchase is still pending, so the correct prompt is
   "close the current package first", not an error state.
7. **Payment instructions can be `503`.** Keep the Pay button and show a "contact your coach"
   state. Any field in the payload may be `null`; `instructions` is ordered and may be empty.
8. **Tolerate an empty `features` array** on paid purchases — backfilled legacy rows have one.
9. **The athlete cannot activate a package.** After confirmation, `GET /api/v1/me/package` starts
   returning the new package; that is the athlete's signal, not anything they trigger.
10. **The Athlete Profile payment badge** comes from the athlete's latest purchase
    (`GET /api/v1/purchases?athleteId=…`, newest first), not from a field on the package — there
    is no `paymentStatus` on `PurchasedPackageResponse` and there will not be one.

### Not in this phase, deliberately

Expenses, payment reminder notifications, the financial dashboard, refunds, partial payments,
cancellation, editing a package's price, and any automatic InstaPay verification. **Phase 7
(To-Dos) is deferred** and was not started.

---

## Phase 6E — An observation's deduction becomes the Admin's explicit choice

**Breaking, in both directions.** `CreateObservationRequest` gains a required field,
`SessionResponse` gains one, and `AthleteListItem` gains one. Regenerate the Flutter client
before building against this.

### `AthleteListItem` now carries `athleteProfileId`

`GET /api/v1/athletes` previously returned only `id`, the athlete's **user** id. Creating an
observation needs the **profile** id, so the app had no way to get from "coach picked an athlete"
to `POST /sessions/observations` without a second lookup it should never have needed.

Every row now carries both:

```json
{
  "id": "…",                 // user id — what /athletes/{athleteId} paths take
  "athleteProfileId": "…"    // profile id — what sessions and packages are keyed by
}
```

`athleteProfileId` is a required, non-null UUID on every row. It cannot be absent: the list is a
join over `AthleteProfiles`, so a row without one cannot exist.

**They are different ids and are not interchangeable.** Posting `id` where `athleteProfileId` is
wanted returns 404 — the athlete is looked up by profile, so a user id simply does not match
anything. That is asserted in the integration suite in both directions, because the failure is
otherwise a runtime 404 in the app rather than anything a type checker would catch.

Recording an observation now asks the Admin a question instead of inferring the answer from the
clock. **This replaces the BR-07 duration rule entirely** — how long an observation ran no longer
has any bearing on what it deducts.

### The request

```json
{
  "athleteProfileId": "…",
  "startUtc": "2026-08-30T08:00:00Z",
  "endUtc": "2026-08-30T09:30:00Z",
  "locationOrPlatform": "Tournament venue",
  "deductSession": true
}
```

`deductSession` is **required** and has no default. Omitting it or sending null is 400
`VALIDATION_FAILED` — an unanswered question must not quietly become "no", which is exactly the
failure mode the old duration rule had.

**Creating an observation still deducts nothing**, whichever way the flag is set. The session is
created `Scheduled`, a booking never deducts (BR-04), and the choice is stored and applied later.

**The dates may be in the past or the future.** An observation is arranged directly with the
athlete, so the Admin may record one already carried out or one agreed for next week. This was
always accepted by the validator; it is now guaranteed and under test, and the wording that
described observations as recorded "after the fact" has been corrected throughout.

`startUtc`/`endUtc` validation is otherwise unchanged: both UTC, in order, and no more than
24 hours apart.

### The response

`SessionResponse` gains `observationDeductsSession`:

```json
{ "observationDeductsSession": true }
```

A boolean on every session whose `deliveryType` is `Observation`, and **null** on `Online` and
`FaceToFace`, which follow BR-05 and have no such choice to report. OpenAPI cannot express
"required for this delivery type", so it is nullable in the schema — but a null on an Observation
is a contract violation, not a "no". Surface it rather than guessing.

### What it deducts now

| Case | Consumes | Rule |
|---|---|---|
| Ordinary session, attended | 1 | BR-05, unchanged |
| Observation attended, created with `deductSession: true` | 1 | BR-07, as chosen |
| Observation attended, created with `deductSession: false` | 0 | BR-07, as chosen |
| Observation created, past or future date | 0 | BR-04 — creating never deducts |
| No-show, `deductSession: true` | 1 | Explicit coach decision, unchanged |
| No-show, `deductSession: false` | 0 | Explicit coach decision, unchanged |
| Booking, and cancellation before attendance | 0 | BR-04, BR-06 |

`consumedSessionCount` is still decided server-side and is still authoritative. The app must not
compute it.

### Two fields named `deductSession`

They are not the same field, they are not interchangeable, and neither overrides the other. One
stores an **intent**; the other makes an **immediate decision**:

| Field | What it is | Decided | Applies |
|---|---|---|---|
| `CreateObservationRequest.deductSession` | The observation's stored deduction **intent** | When the observation is recorded | Only if that observation is later marked **Attended** |
| `MarkAttendanceRequest.deductSession` | An **immediate** deduction decision | At the moment of marking | When marking **any** session `NoShow` — observation or not |

So an observation created with `deductSession: false` **can still consume one** if it is
subsequently marked `NoShow` with `deductSession: true`. That is not a conflict being resolved:
the stored intent is scoped to Attended, the observation was never attended, and so the intent is
simply not the question being answered. The stored value is left unchanged by the no-show and is
still reported as `observationDeductsSession: false` afterwards.

Both directions are covered by named integration tests, so neither can be quietly broken.

### One widened error surface

A short observation can now deduct, so marking one attended can now return 409
`ACTIVE_PACKAGE_NOT_FOUND` or 409 `NO_SESSIONS_REMAINING` — outcomes an observation of 60 minutes
or less could never previously produce. Everything else about attendance is untouched: the same
single transaction, the same exactly-once guarantee, the same `CONCURRENCY_CONFLICT` on a race,
and an unchanged `AttendanceResponse`.

### Migration

`AddObservationDeductsSession` adds a nullable `ObservationDeductsSession` column to `Sessions`,
backfills existing observations from the rule that was in force when they were recorded
(`DurationMinutes > 60`), and then adds a check constraint holding the column non-null exactly
when `DeliveryType = 'Observation'`. The backfill runs **before** the constraint on purpose, and
it is what keeps already-attended observations agreeing with the package balance they actually
moved.

---

## Phase 6 — Attendance, session notes and purchased packages

> **Superseded in part by Phase 6E above.** The observation duration rule described in this
> section no longer applies; an observation deducts according to the Admin's explicit choice.
> `SessionResponse` and `CreateObservationRequest` have each gained a field since this was
> written, so read Phase 6E for their current shape.

Marking a session attended is the only thing in this product that consumes something the athlete
paid for, so this phase is mostly about making that happen **exactly once**. Everything else here
exists to support it.

**Nothing from Phase 5 changed shape.** `SessionResponse` is byte-for-byte what it was, and no
existing field moved or was removed. The two changes to things the app already reads are both
additive and are listed under "What changed in existing shapes" below.

### Purchased packages now exist

Phase 4 shipped a **catalogue** — `PackageOption`, per-athlete prices, loyalty. It deliberately
did not ship the thing an athlete *owns*, and attendance has nothing to deduct from without one,
so the purchase model lands here.

| Method | Path | Role | Purpose |
|---|---|---|---|
| POST | `/api/v1/athletes/{athleteId}/packages` | A | Record a purchase |
| GET | `/api/v1/athletes/{athleteId}/packages` | A | Package history, newest first |
| GET | `/api/v1/athletes/{athleteId}/packages/active` | A | Current package and balance |
| GET | `/api/v1/packages/{id}` | A | One package |
| POST | `/api/v1/packages/{id}/close` | A | End a package early |
| GET | `/api/v1/me/package` | T | The athlete's own active package |

`athleteId` is the athlete's **user** id, matching every other `/athletes/{athleteId}` route.
`athleteProfileId` on the response is the **profile** id, which is what the session endpoints use.
They are different ids and the app needs both.

**The price is not in the request and cannot be.** It is computed server-side from the option's
default price, the athlete's loyalty flag and any override — the same `PackagePricing` rule that
produced the number the athlete was already shown — and then **copied onto the package as paid**.
Renaming, repricing or archiving the catalogue option afterwards never reaches a purchase. An
Admin who could send a price could send a different one from the one the athlete was quoted.

`remainingSessions` is sent even though it is `totalSessions − usedSessions`, so the number the app
displays and the number the server deducts against are the same arithmetic done once. It can
legitimately be `0`; the UI shows **"New sessions pending"** for that (architecture C-04), but the
field stays a number.

**BR-03 — one active package per athlete** — is now enforced, by a partial unique index rather
than a check in a handler, because two Admin devices purchasing at the same moment are two
transactions and only the database sees both. A second purchase while one is active is
409 `ACTIVE_PACKAGE_EXISTS`; close the current one first. A package that runs out becomes
`Completed` on its own, which is what frees the athlete to buy the next one.

**Not here, deliberately:** a package has no payment status and there is no endpoint to edit its
price. Payment status is derived from confirmed payments, which are Phase 8 and do not exist, and
which values it takes is still open decision C-01.

### Mark as Attended

`POST /api/v1/sessions/{id}/attend` — Admin only. The request now uses the dedicated
`AttendanceOutcome` enum (`Attended | NoShow`) rather than the broader `SessionStatus` enum.
`outcome` still defaults to `Attended`; `Cancelled` is not representable in this request because
cancelling also has to reach Calendly and has its own endpoint.

No-show deduction is now an explicit per-session decision:

```json
{ "outcome": "NoShow", "deductSession": true }
```

`deductSession` is required and must be a boolean for `NoShow`; `true` consumes exactly one
session and `false` consumes none. It must be omitted for `Attended`, whose deduction remains a
server-side consequence of the ordinary-session and observation-duration rules. Supplying it for
`Attended`, omitting it for `NoShow`, or sending it as null returns 400 `VALIDATION_FAILED`.

> Since Phase 6E, an attended **observation** follows the `deductSession` choice made when it was
> recorded, not a duration rule. An ordinary attended session still follows BR-05.

The response carries the session **and** the package, both as they now stand after one
transaction:

```json
{
  "session":  { "...": "the same SessionResponse shape as everywhere else" },
  "consumedSessionCount": 1,
  "package":  { "...": "PurchasedPackageResponse, or null" },
  "progress": { "packageId": "…", "sessionNumber": 7, "totalSessions": 12, "remainingSessions": 5 }
}
```

Both are returned rather than left for the client to re-read, because they changed together and
sending them together is the only way the app can show a state that actually existed — a re-read
can interleave with another change. `package` and `progress` are null when the session consumed
nothing and the athlete has no active package.

**How much it deducts is decided server-side** and reported as `consumedSessionCount`. The app
must not compute it:

| Case | Consumes | Rule |
|---|---|---|
| Ordinary session, attended | 1 | BR-05 |
| Observation attended, created with `deductSession: true` | 1 | BR-07 — see Phase 6E |
| Observation attended, created with `deductSession: false` | 0 | BR-07 — see Phase 6E |
| No-show, `deductSession: true` | 1 | Explicit coach decision for this session |
| No-show, `deductSession: false` | 0 | Explicit coach decision for this session |
| Booking, and cancellation before attendance | 0 | BR-04, BR-06 |

**Exactly-once is what the error codes are for.** These are not failures to retry past:

| Code | Status | Means |
|---|---|---|
| `SESSION_ALREADY_ATTENDED` | 409 | The deduction has already happened, once. Re-read, do not retry. |
| `SESSION_ALREADY_RESOLVED` | 409 | Already marked a no-show. |
| `SESSION_CANCELLED` | 409 | BR-06 — a cancelled session can never be attended. |
| `ACTIVE_PACKAGE_NOT_FOUND` | 409 | Nothing to deduct from. Sell a package. |
| `NO_SESSIONS_REMAINING` | 409 | The package is exhausted. Renew. |
| `CONCURRENCY_CONFLICT` | 409 | Two requests raced; this one deducted nothing. |

The last one is the double-tap case: two simultaneous requests produce one success and one 409,
never two deductions. Per the architecture this action is **online-only** — never queue it, and
disable the button when offline.

**A session that has been attended can no longer be cancelled** (409 `SESSION_ALREADY_ATTENDED`
from `POST /sessions/{id}/cancel`). Consumed value is never given back silently. If Calendly
cancels it out of order through a webhook, the session is left Attended and the deduction stands.

### Session position — "Session 7 of 12"

`GET /api/v1/sessions/{id}/package-progress` — the Admin Session Details header. Deliberately a
separate endpoint rather than a field added to `GET /sessions/{id}`, so the Phase 5 session shape
the app already reads does not change.

`sessionNumber` is the session's own position once attended, and the position it *would* take if
it has not been resolved yet — which is what the screen wants to show before the coach taps Mark
as Attended. It is **null** when no position exists to state: a cancelled session, or a short
observation that will never consume one. 404 `PACKAGE_NOT_FOUND` when the athlete has no package,
which is a normal state.

### Observations

`POST /api/v1/sessions/observations` — Admin only. The one kind of session this API creates
itself. Observations are arranged in person and never appear on a Calendly booking page, so the
coach records one directly (architecture A-03). Everything else still comes from Calendly
and cannot be created here.

It is created `Scheduled` and deducts nothing yet — a booking never deducts (BR-04) — then marked
attended like any other session, at which point BR-07 decides. `startUtc` and `endUtc` must both
be UTC and in order and may not span more than a day. `athleteProfileId` is the **profile** id.

> Phase 6E added the required `deductSession` field to this request and
> `observationDeductsSession` to the response, and confirmed that the dates may be in the future.

### Session notes

`GET`, `POST` `/api/v1/sessions/{sessionId}/notes`, and `PUT`, `DELETE`
`/api/v1/sessions/{sessionId}/notes/{noteId}`. **Admin only** — the UI/UX document places these on
Session Details (Admin View), and nothing in it shows a coach's session notes to the athlete.
Opening them up later is additive; having shown them by mistake is not undoable.

A session holds **many** notes rather than one editable block, because the screen offers add as
well as edit and a record that can only be overwritten loses what was written last time. Editing
leaves `createdAtUtc` where it is so the history keeps its order, and moves `updatedAtUtc`. Notes
can be added to a session in any status — they are usually written up afterwards.

### What changed in existing shapes

Two additive changes, both to things the app already reads:

- **`SessionStatus` gained `Attended` and `NoShow`**, and is now `Scheduled | Attended | Cancelled
  | NoShow` — all four the specification requires (architecture C-03). Any client that switches on
  session status needs arms for the two new values.
- **`ApiProblemDetails.errorCode` gained** `ACTIVE_PACKAGE_EXISTS`, `ACTIVE_PACKAGE_NOT_FOUND`,
  `NO_SESSIONS_REMAINING`, `PACKAGE_ALREADY_CLOSED`, `PACKAGE_NOT_ACTIVE`, `PACKAGE_NOT_FOUND`,
  `SESSION_ALREADY_ATTENDED`, `SESSION_ALREADY_RESOLVED`, `SESSION_CANCELLED`,
  `SESSION_NOTE_NOT_FOUND` and `OBSERVATION_RANGE_INVALID`.

`PACKAGE_NOT_FOUND` is a package somebody bought; `PACKAGE_OPTION_NOT_FOUND` is an entry in the
catalogue. They are different screens and the codes are kept apart on purpose.

### Configuration

`Features__NoShowDeducts` remains in configuration as a possible future/default preference, but
the attendance endpoint no longer reads it. Mobile must send the coach's explicit
`deductSession` choice for each no-show.

---

## Phase 5 — Scheduling, Calendly and sessions

Booking runs on Calendly. This API owns the athlete's side of it: which session types can be
booked, which times are free, and the sessions that came out of it. The first two are asked of
Calendly live on every call, so nothing is served stale; the sessions themselves are stored here
and kept in step by webhooks and a background reconciliation.

### Every Phase 5 response now has a schema

These endpoints shipped with their successful responses described as `OK` and nothing more,
which left the app to guess the payload. Each one now names a type:

| Endpoint | Success | Body |
|---|---|---|
| `GET /api/v1/scheduling/session-types` | 200 | `BookableSessionType[]` |
| `GET /api/v1/scheduling/session-types/{eventTypeId}/availability` | 200 | `AvailableSlot[]` |
| `POST /api/v1/scheduling/bookings` | 201 | `SessionResponse` |
| `POST /api/v1/scheduling/refresh` | 202 | *empty — deliberately* |
| `GET /api/v1/sessions` | 200 | `SessionPage` |
| `GET /api/v1/sessions/upcoming` | 200 | `SessionPage` |
| `GET /api/v1/sessions/{id}` | 200 | `SessionResponse` |
| `POST /api/v1/sessions/{id}/cancel` | 200 | `SessionResponse` |
| `GET /api/v1/sessions/{id}/reschedule` | 200 | `RescheduleUrlResponse` |

Every failure is the same `ApiProblemDetails` in `application/problem+json` the rest of the API
uses, and each endpoint now declares the statuses it can actually produce rather than leaving
them to be discovered.

### `SessionResponse` carries `athleteName`

Added for the Admin schedule and upcoming-session cards, which need to show who a session is
with. `athleteProfileId` alone would have meant a second request per row, or a client-side join
against a list the schedule screen has no reason to have loaded.

**Required and never null**, on every endpoint that returns a session and for both Admin and
Athlete callers — an athlete sees their own name on their own sessions, so one model serves both.

It is the athlete's `fullName`, **falling back to their email address** when that is null. A name
is null until an athlete completes their profile, and a session can exist before that: booking
through this API requires a name, but a booking made on Calendly's own page does not. Rather than
publish a nullable field that is populated in almost every case — the shape most likely to reach
production untested — the API guarantees something renderable and says here what it can be. An
email on a schedule card is the same fallback `AthleteListItem` already leaves to the client,
resolved server-side this time.

The name is read from the account at request time, not stored on the session, so an athlete who
corrects their name sees it corrected on sessions they booked beforehand.

### Availability is asked for a week at a time

`fromUtc` and `toUtc` must be UTC, in the future, in order, and **at most 7 days apart**. Wider
is `400 AVAILABILITY_RANGE_INVALID`.

Seven is Calendly's limit on an availability query, not a preference of ours, which is why the
API refuses rather than clamps: a clamped range would quietly return a week of slots for a month
that was asked for, and the calendar would look empty from the eighth day on. To fill a month,
make four or five calls and cache them — the slot list is a snapshot in any case, since a time
can be taken between reading it and booking it.

### The shapes

| Schema | Fields |
|---|---|
| `BookableSessionType` | `id`, `name`, `durationMinutes`, `deliveryType`, `locations[]` |
| `BookableLocation` | `kind`, `location` (nullable) |
| `AvailableSlot` | `startUtc`, `endUtc` |
| `SessionResponse` | `id`, `athleteProfileId`, `athleteName`, `startUtc`, `endUtc`, `durationMinutes`, `deliveryType`, `status`, `locationOrPlatform`, `meetingUrl`, `rescheduleUrl` |
| `SessionPage` | `items[]`, `nextCursor` |
| `RescheduleUrlResponse` | `url` |

A nullable field is still in the schema's `required` list, as everywhere else in this contract:
the key is always present, and the value may be `null`. The client handles a null, never a
missing key.

`deliveryType` is `Online | FaceToFace | Observation`; `status` is `Scheduled | Cancelled`.
`meetingUrl` is filled in for an online session and null otherwise, `locationOrPlatform` is
whatever Calendly recorded, and `rescheduleUrl` is the same link `GET /sessions/{id}/reschedule`
returns.

`id` on `BookableSessionType` is the Calendly event type's trailing identifier, not a database
id. It is what `/availability` and `POST /bookings` take, and it changes if the event type is
renamed in a way that changes its slug.

### `GET /sessions/{id}/reschedule` returns a named object

It was returning an anonymous `{ "url": ... }`, which no contract could name. It is now
`RescheduleUrlResponse`. **The JSON is byte-for-byte what it was** — one `url` property — but
there is now a schema to generate a model from. It stays an object rather than a bare string so
a second field can be added later without breaking the client.

### Booking: `Idempotency-Key` is a declared parameter

The header was read by the endpoint without appearing in the contract. It is now part of the
operation: `in: header`, `required: true`, `maxLength: 100`, which is exactly what the handler
enforces. Missing, blank or over-long is `400 IDEMPOTENCY_KEY_REQUIRED`.

The key is remembered per athlete. Replaying it returns the session that key already created
rather than booking a second one; while the first attempt is still in flight the replay is
`409 BOOKING_IN_PROGRESS` with `retryAfterSeconds`. Generate one key per booking attempt and
keep it across retries — a new key per retry defeats the whole mechanism.

`locationKind` and `location` remain optional on `BookSessionRequest`, and the record now
declares them with defaults so a regeneration keeps them that way instead of silently promoting
them into `required`. Whether a location is needed at all is a fact about the Calendly event
type, not something the schema can state: send `locationKind` when `locations` has more than one
entry, and expect `400 LOCATION_REQUIRED` or `400 LOCATION_INVALID` to say so if it is wrong.

### `POST /scheduling/refresh` is 202 with an empty body

Admin-only, and an operational escape hatch for a missed webhook rather than something the
athlete app calls. It queues background work, so there is nothing for the response to say and
no model for the client to write: **202 means queued, not done.** Re-read `/sessions` afterwards
to see the result.

### Paging `/sessions`

Ordered by start time, earliest first. `limit` is clamped to 1–100 and `0` means 30 (10 on
`/upcoming`). When `nextCursor` is non-null there is at least one more session — pass it back
unchanged as `cursor` and page until it is null, rather than stopping on a short page.

An athlete sees only their own sessions and `athleteProfileId` is ignored for them; an Admin
sees the coach's sessions and can narrow to one athlete with it. An athlete with no profile gets
an empty page, not an error. `/sessions/upcoming` is exactly `/sessions` with `fromUtc` set to
now and `status` set to `Scheduled`.

### Cancelling returns the session

`POST /sessions/{id}/cancel` cancels in Calendly first and only then here, so the two cannot
disagree: if Calendly refuses, the session is left scheduled and the call is `503` with nothing
changed. On success the updated session comes back with `status: "Cancelled"`, so the client can
replace its copy from the response without re-reading. Cancelling an already-cancelled session
succeeds and returns it unchanged, which makes a repeated tap safe.

### Error codes added

| Code | Status | Meaning |
|---|---|---|
| `AVAILABILITY_RANGE_INVALID` | 400 | Range is not UTC, not in the future, out of order, or spans more than 7 days |
| `TIME_ZONE_INVALID` | 400 | `timeZone` is not a recognised IANA name |
| `LOCATION_REQUIRED` | 400 | The session type needs a location choice and none was sent |
| `LOCATION_INVALID` | 400 | `locationKind` is not one this session type offers |
| `IDEMPOTENCY_KEY_REQUIRED` | 400 | Header missing, blank, or over 100 characters |
| `EVENT_TYPE_INVALID` | 404 | Unknown or unmapped `eventTypeId` |
| `SESSION_NOT_FOUND` | 404 | No such session, **or it belongs to another athlete** |
| `SLOT_UNAVAILABLE` | 409 | The time is no longer free, or was never a bookable start |
| `BOOKING_IN_PROGRESS` | 409 | The same key is still being processed; carries `retryAfterSeconds` |
| `CALENDLY_UNAVAILABLE` | 503 | Calendly is not configured or is not answering |
| `CALENDLY_RATE_LIMITED` | 503 | Calendly rate-limited us; carries `retryAfterSeconds` |
| `CALENDLY_SIGNATURE_INVALID` | 401 | Webhook endpoint only; never reaches the app |

All of these were already being returned; none of them were in `errorCode` in the contract, so a
generated client had no case for them. `DUPLICATE_BOOKING` is also in the list but is not
returned by any endpoint today — treat it as reserved.

A 503 from any of these is transient. An empty session-type list because Calendly is down must
not be shown as "no sessions available"; that condition is the 503, not an empty array.

### Enums now carry `type: string`

`UserRole`, `UserStatus`, `InvitationStatus`, `AthleteStatusFilter` and the new `DeliveryType`
and `SessionStatus` were emitted as a list of values with no `type`, purely because none of them
happened to be used nullably anywhere — `Gender` and `AthleteListSort`, which are, already said
`type: string`. Every enum in this API is serialised by name, so all of them say so now. Additive
only: no value changed.

### Known gaps

- **`limit` is a required query parameter** on `/sessions` and `/sessions/upcoming`. Send it
  explicitly; `0` selects the default rather than being rejected.
- **`nextCursor` is a start time, compared strictly.** Two sessions with the identical start
  time can fall either side of a page boundary and one be skipped. Not reachable with one
  athlete's own sessions; possible on an Admin's coach-wide list.
- **`packageId` is not exposed.** A session is stored with the package it belongs to, but
  nothing in `SessionResponse` reports it — remaining-session counting is later payment work.
- **`POST /api/v1/webhooks/calendly` is Calendly-facing.** It is in the contract because it is a
  route, not because the app should ever call it.
- **A change made inside Calendly can take up to 15 minutes to show here.** Webhooks are a paid
  Calendly feature (Standard and above); until the account is on one, `Calendly:WebhookSigningKeys`
  is empty, nothing calls the webhook route, and the background reconciliation sweep is the only
  thing that notices a reschedule or cancellation made on Calendly's own pages. Anything done
  through *this* API — booking, cancelling — is immediate and comes back in the response. This
  affects freshness, not correctness: re-read the session after sending the athlete to
  `rescheduleUrl` rather than assuming the new time is already stored.

---

## Phase 4 — Package options, loyalty and custom pricing

The catalogue only. **Purchasing, InstaPay, pending purchases, payment confirmation, package
activation, remaining-session tracking and purchase history are all excluded** and remain part
of the later payment work, as specified.

> **This supersedes the current spec documents, which have not been updated yet.**
> `product-specification.md` §4.5, `software-architecture.md` §14.3 and `development-roadmap.md`
> Phase 4 all describe Phase 4 as the *purchased* package — one active package per athlete,
> BR-03, remaining sessions. That model is not gone; it has moved to the later phase, and none
> of it is implemented here. Until those documents are amended they describe a Phase 4 the API
> does not have.

### Money is an integer count of piastres

Every price field ends in `Minor` and is a **64-bit integer number of piastres**, 100 to the
Egyptian pound. `defaultPriceMinor: 400000` is 4,000.00 EGP.

This was the delegated decision, and it is deliberate. A decimal price serialised as a JSON
number is parsed into a Dart `double`, and doubles cannot represent most decimal fractions
exactly. On one price the error is invisible; it shows up the first time prices are summed.
Dart's `int` is exact 64-bit, so an integer of piastres has no such failure mode anywhere along
the chain — C#, Postgres, JSON, Dart.

To display: `priceMinor / 100` with two decimal places. Never do arithmetic on the divided value.

`currency` is returned beside every price and is always `"EGP"`. It is there so the client never
assumes, and so a second currency later is a value change rather than a contract change.

### The 15% loyalty rounding rule

**Price × 0.85, rounded to the nearest tenth of a pound (10 piastres), halves away from zero.**

Fifteen percent of an arbitrary price lands on fractions of a piastre — 999.99 becomes 849.9915 —
and a catalogue full of prices like that reads as a bug rather than a discount. Rounding to a
tenth keeps every loyalty price something a person would write down.

| Default | ×0.85 | Charged |
|---|---|---|
| 4,000.00 | 3,400.00 | **3,400.00** |
| 999.99 | 849.9915 | **850.00** |
| 33.33 | 28.3305 | **28.30** |
| 1.00 | 0.85 | **0.90** (a midpoint, so away from zero) |

Two things this rule does **not** do:

- **Default prices and custom overrides are never rounded.** They are numbers a person chose,
  and rounding a deliberate 1,234.56 to 1,234.60 would be the API overruling the coach. Only the
  *computed* loyalty price is rounded.
- **A discount can never exceed the original price.** Rounding to the nearest tenth rounds up as
  often as down, and below about a pound that could land above the undiscounted price — 0.06
  would "discount" to 0.10. The result is clamped. No coaching package costs 6 piastres; the
  guard exists so the rule cannot misbehave rather than because it was likely to.

### Effective-price precedence

Exactly as specified, and calculated **server-side only**:

1. Athlete/package custom price
2. 15% loyalty discount
3. Package default price

**A custom price is not discounted again for a loyal athlete.** It is an agreed price for that
athlete, not a starting point; compounding the two would make the number the coach typed not the
number the athlete pays. A custom price of **0 is a real override**, not an absent one — a test
pins this, because `if (price)` in almost any language would fall through to the default.

### Admin endpoints

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/v1/package-options` | Active options |
| `GET` | `/api/v1/package-options?archived=true` | Archived options |
| `GET` | `/api/v1/package-options/{id}` | One option |
| `POST` | `/api/v1/package-options` | Create |
| `PUT` | `/api/v1/package-options/{id}` | Edit |
| `POST` | `/api/v1/package-options/{id}/archive` | Archive |
| `POST` | `/api/v1/package-options/{id}/restore` | Restore |
| `PUT` | `/api/v1/athletes/{athleteId}/loyalty` | Mark or unmark loyal |
| `GET` | `/api/v1/athletes/{athleteId}/custom-prices` | This athlete's overrides |
| `PUT` | `/api/v1/athletes/{athleteId}/custom-prices/{packageOptionId}` | Set an override |
| `DELETE` | `/api/v1/athletes/{athleteId}/custom-prices/{packageOptionId}` | Remove an override |
| `GET` | `/api/v1/athletes/{athleteId}/catalogue` | Preview what this athlete will pay |

The catalogue preview is there so the coach can check a price **without the client reproducing
the precedence rule**.

### Athlete endpoint

`GET /api/v1/catalogue` — athlete-only, always the caller's own, from the token.

Each item: `id`, `name`, `sessions`, `features` (ordered), `priceMinor`, `currency`. Archived
options are excluded. Ordered cheapest first.

`priceMinor` is the **final** price. There is deliberately **no** `defaultPriceMinor`, no
`isLoyal`, and no field saying which rule applied — a test asserts their absence. A "was 4,000"
the athlete never agreed to would be an invention, and revealing that a discount applied invites
"why not me?" between athletes.

### Validation

| Field | Rule |
|---|---|
| `name` | Required, trimmed, ≤100 chars, **case-insensitively unique across the whole catalogue** |
| `sessions` | Whole number, 1–1000 |
| `defaultPriceMinor` | Integer ≥ 0, ≤ 1,000,000,000 piastres (10,000,000 EGP) |
| `features` | 1–10 entries, each non-blank and ≤100 chars, **order preserved** |

Order is stored explicitly, so the order sent is the order stored and returned — not sorted, not
whatever order the database returns rows in.

**These limits are now in `openapi.yaml` itself** — `maxLength`, `minimum`, `maximum`, `minItems`,
`maxItems`, including `maxLength` on each feature string — so a generated client can reject a bad
value without a round trip, rather than the bounds living only in prose.

### Every pricing endpoint 404s on an unknown athlete

`GET /athletes/{id}/custom-prices`, `GET /athletes/{id}/catalogue`, both `custom-prices/{optionId}`
methods and `PUT /athletes/{id}/loyalty` all return **404 `ATHLETE_NOT_FOUND`** for an athlete id
that is unknown or belongs to another coach. The list endpoints previously returned an empty
list, which is wrong: **an empty list is a real answer** — most athletes have no overrides, and a
coach with no package options has an empty catalogue — so a bad id was indistinguishable from a
screen that merely looks empty.

Setting and removing a custom price also verify the athlete before writing anything. That check
was missing entirely: an override could be attached to an athlete id belonging to another coach.
Low impact with one coach, but it was a real hole.

**Uniqueness spans archived options too.** Archiving does not free a name for reuse — that was
my earlier call and it was wrong, because it allowed exactly the sequence the review found:
reuse an archived name, restore the archived option, end up with two active packages called the
same thing. Enforced by a unique index on `(CoachId, lower(Name))`, so it holds even when two
Admin devices race.

A consequence worth having: **restore can no longer fail on a name collision**, because nothing
can have taken the name while the option was archived. The coach can always recover an option.

### Archive behaviour

Options are **never deleted**. Archiving hides an option from the athlete catalogue, leaves it
visible to the Admin under `archived=true`, and touches nothing an athlete has already bought.
Archived options **cannot be edited** until restored — `409 PACKAGE_OPTION_ARCHIVED`.

Restore always succeeds on a name basis — see the uniqueness rule above — so the only way it
fails is `PACKAGE_OPTION_NOT_ARCHIVED` or a stale `version`.

### Concurrency

Every option carries a `version`, an integer that increases on every successful change. Send the
version you last read on edit, archive and restore. A stale version returns
`409 CONCURRENCY_CONFLICT` rather than silently overwriting — the coach may have the catalogue
open on a phone and a tablet, and without this the second save wins invisibly.

### Error codes

| Code | Status | Meaning |
|---|---|---|
| `PACKAGE_OPTION_NOT_FOUND` | 404 | No such option, **or it belongs to another coach** |
| `PACKAGE_NAME_CONFLICT` | 409 | Another active option has that name |
| `PACKAGE_OPTION_ARCHIVED` | 409 | Editing or re-archiving an archived option |
| `PACKAGE_OPTION_NOT_ARCHIVED` | 409 | Restoring one that is not archived |
| `CONCURRENCY_CONFLICT` | 409 | Stale `version` |
| `CUSTOM_PRICE_NOT_FOUND` | 404 | Removing an override that does not exist |

**Three suggested codes were deliberately not adopted**, since the invitation was to publish
authoritative names:

- `PACKAGE_OPTION_VALIDATION_FAILED` and `CUSTOM_PRICE_INVALID` → both are **`VALIDATION_FAILED`**,
  which every endpoint already returns with per-field detail in `errors`. Two names for one
  condition is how clients end up handling only one of them.
- `PACKAGE_OPTION_CONFLICT` → **`CONCURRENCY_CONFLICT`**, which is not package-specific. Sessions
  will raise the identical condition later, and one code per entity multiplies without telling
  the client anything new.

`PACKAGE_OPTION_NOT_ARCHIVED` was added; restore needed a distinct failure.

### Athlete list and profile

`AthleteListItem` and `AthleteDetail` both gain **`isLoyal`**. Loyalty is athlete-level, and
marking it is idempotent — marking an already-loyal athlete loyal does not reset how long they
have been loyal.

### Authorization

Every Admin endpoint is `AdminOnly`; an athlete reaching one gets **403**. A resource belonging
to another coach is **404, not 403**, so the API never confirms that an id it will not serve
exists. `GET /api/v1/catalogue` is athlete-only and always scoped to the token.

### Not decided here

`sport` on the athlete profile is still required free text — unchanged. Documentation updates to
`product-specification.md`, `ui-ux-design-decisions.md`, `software-architecture.md` and
`development-roadmap.md` are still outstanding, and the recommendation to wait for the
loyalty/custom-pricing UI before writing them stands.

---

## Phase 3.1 — Password reset rate limiting

**Not a shape change.** `POST /auth/forgot-password` still returns `200` on the happy path with
no body. It can now also return **`429`**, which it could not before, so a client that treats
any non-2xx as a generic failure will show the wrong message.

### There was no rate limiting on this endpoint before now

Confirmed by inspection: `/invitations/validate` was the only rate-limited route in the API.
`forgot-password` was open, which made it a free mail-bomb against any known address. The
architecture (section 12.5) had specified a limit; it had simply never been implemented.

### The limits

| Axis | Limit | Window | Configurable via |
|---|---|---|---|
| Per email address | **3** | 1 hour, fixed | `RateLimits:PasswordResetPerEmailPerHour` |
| Per IP address | **10** | 1 hour, fixed | `RateLimits:PasswordResetPerIpPerHour` |

**Both apply.** Whichever trips first returns the 429. The per-email limit stops one address
being mail-bombed; the per-IP limit catches the attack per-email cannot see — one machine
walking a list of addresses, three requests each, never tripping any single address's counter.

The per-email figure is the architecture's. **The per-IP figure is not in any source document** —
it was chosen here and is open to revision. Ten an hour is far more than a household or a small
office needs, since forgetting a password is rare, while a list-walking script hits it at once.

The email is normalised before counting — trimmed and lower-cased — so `A@b.com`, `a@b.com` and
`  a@b.com  ` share one allowance rather than getting three.

Windows are **fixed, not sliding**: the allowance resets at the top of the hour-long window
rather than rolling. `retryAfterSeconds` always reports the true remaining time.

### The 429 response

Identical in shape to every other error in the API:

```json
{
  "type": "https://tools.ietf.org/html/rfc6585#section-4",
  "title": "Too many password reset requests for this address. Try again later.",
  "status": 429,
  "errorCode": "TOO_MANY_REQUESTS",
  "correlationId": "0HN7...",
  "retryAfterSeconds": 3542
}
```

- **`errorCode`** is `TOO_MANY_REQUESTS`, already in the error-code enum.
- **`retryAfterSeconds`** is in the body, and the **`Retry-After`** header carries the same
  number of seconds. A test asserts the two agree, so honour either.
- Both can be **up to 3600**. Render minutes, not seconds — "try again in 59 minutes", not
  "try again in 3542 seconds".
- `title` differs slightly between the per-email and per-IP limits. Branch on `errorCode`, never
  on the title.

### Account-enumeration protection is preserved, deliberately

This was the delicate part. **The per-email counter is keyed on the address that was submitted,
before any database lookup**, so a 429 arrives on the fourth request whether or not an account
exists. Counting only real accounts would have made the 429 mean *"this address is registered"* —
turning the very feature meant to prevent enumeration into the oracle it was designed to avoid.

A test asserts that a registered address and an unknown one return byte-identical 429 bodies,
apart from the per-request `correlationId`.

**So on mobile, treat 429 exactly like 200:** *"If that address is registered, we have sent a
link."* Never surface anything that distinguishes them, and never show "no account found".

### The trade-off, stated plainly

Per-email limiting means someone can deliberately spend a victim's three attempts and stop them
resetting their password for the rest of the hour. That is inherent to per-email limiting, not a
flaw in this implementation, and the architecture asks for it — the alternative is letting one
address be mail-bombed indefinitely. The window is short. Worth knowing it exists.

### Note for mobile

The client-side 60-second cooldown is still worth keeping — it gives immediate feedback without
a round trip. It is now a UX affordance rather than the enforcement, which is the right split.
The two do not need to agree: 60 seconds client-side and 3-per-hour server-side simply means a
user tapping every 61 seconds is stopped by the server on the fourth attempt.

---

## Phase 3 — Onboarding alignment

All six points from the mobile review, the removal of `termsAccepted`, and `email` on the
athlete list. **Six breaking changes.**

### Breaking · `POST /auth/register` no longer takes `fullName`

Registration establishes authentication and nothing else. The field is gone from
`RegisterRequest`. Sending it anyway is harmless — unknown properties are ignored — but it is
not stored, so nothing is silently kept.

On the Google path the account's display name is still kept as a **prefill**, so Complete
Profile can show it pre-filled for the athlete to confirm. The password path has no name to
offer and does not ask for one.

### Breaking · `fullName` is nullable until the profile is completed

Nullable in `UserSummary` (inside every `AuthResponse`), in `CurrentUserResponse`
(`GET /auth/me`), and in `AthleteListItem` and `AthleteDetail` (the coach's list and detail).

**The invariant, which the backend guarantees:**

> whenever `profileCompleted` is `true`, `fullName` is non-null and non-blank.

It is enforced in the domain, not by the endpoint: `User.MarkProfileCompleted` refuses to run
without a name, so no future code path can produce a completed profile without one. Unit tests
cover the guard directly. The app may treat the pair as an invariant and skip re-checking.

The field is **always present** in the JSON, carrying `null` — never omitted.

Two consequences for the coach's screens, both deliberate:

- An athlete who has registered but not completed their profile **still appears** in
  `GET /athletes` with `fullName: null`. Show the email until a name exists.
- **Search now also matches the email**, so such an athlete is findable. Name-sorted lists put
  unnamed athletes **last**, the same way `sort=Sport` puts athletes with no sport last.

### Breaking · `POST /athletes/me/profile` requires all four fields

`fullName`, `dateOfBirth`, `gender` and `sport` are all required in `CompleteProfileRequest` and
enforced server-side. Anything missing is `400 VALIDATION_FAILED`, and the profile stays
incomplete — there is no partial save.

`dateOfBirth` must be in the past and within the last 120 years.

### Breaking · `gender` is an enum

`Gender`, with exactly two values: **`Female`** and **`Male`**. Anything else is
`400 VALIDATION_FAILED`.

Values are read **case-insensitively** (`"female"` is accepted) and always **written
canonically** (`"Female"`), so responses never carry two spellings. Stored as the name, never an
ordinal, so reordering the enum can never remap existing rows.

It is nullable everywhere it is *read* — an athlete who has not completed their profile has no
gender — and required in the request that sets it.

### Confirmed · no photo field

`CompleteProfileRequest` has no photo field and will not gain one before phase 13, which brings
file storage. Initials as the fallback is right.

### Confirmed · invitation validation is **10 per hour per IP**

The architecture (section 12.5) is the source of truth and says per **hour**; the implementation
had drifted to per minute. The implementation now matches the architecture.

`429 TOO_MANY_REQUESTS` still carries `retryAfterSeconds` in the body and a `Retry-After`
header, both in seconds. With an hour-long window those values are now up to `3600`, so a
countdown UI should render minutes, not seconds.

> **Worth a decision before launch.** Ten attempts an hour is generous for an athlete typing one
> code out of an email, but the window is per IP, so everyone behind one office or campus NAT
> shares the budget. If that turns out to bite, the limit is configurable per environment via
> `RateLimits:InvitationValidationPerHour` — no code change.

### Breaking · `termsAccepted` removed from `POST /auth/register`

The field is gone from `RegisterRequest`, and **`TERMS_NOT_ACCEPTED` is removed from the
error-code enum**. Sending `termsAccepted` is now ignored rather than validated. Registration
can no longer fail for this reason.

**This supersedes the phase 1.2 rule** that `termsAccepted` must be true, and it also
supersedes `software-architecture.md` §891 ("Both methods require acceptance of the Terms of
Service and Privacy Policy"), its registration sequence diagram in §876, and
`ui-ux-design-decisions.md` §809. Those documents now describe a requirement the API does not
enforce, and should be amended so the spec and the contract agree.

**Decided by the client:** the app will ship with no Terms of Service and no Privacy Policy, so
there is nothing to accept and no consent to record.

> **Open, and not a backend concern:** the Apple App Store and Google Play both require a
> privacy policy URL at submission for any app that collects personal data. This one collects
> email, full name, date of birth and gender. Nothing in the API blocks release; the requirement
> lands at store submission and is noted here only so it is not a surprise then.
> `product-specification.md` §978 and §1093 also still assume a policy exists before launch.

### Search now matches email as well

`GET /athletes?search=` matches **full name, email or sport**. Email was added because an
athlete who has registered but not completed their profile has no name, and would otherwise be
unfindable in the coach's own list. This supersedes the phase 2 table, which says "full name or
sport".

### Breaking · `AthleteListItem` gains `email`

Non-nullable, always present. The agreed behaviour is that a row with `fullName: null` shows the
athlete's email instead — but the list response had no email to show, so the rule could not
actually be implemented. Added, and a test now asserts every row carries one.

`AthleteDetail` already had it; this only closes the gap on the list.

### Invitation code format

Asked and answered, because the assumption in the mobile review was wrong in both halves:

- **Ten characters, not six.** Alphabet `ABCDEFGHJKMNPQRSTWXYZ23456789` — 29 symbols, no `O`,
  `I`, `L`, `U` or `V`, because those are misread when retyped from an email.
- **Emailed formatted five-dash-five**, for example `MRPZB-AXZYY`.
- **`/invitations/validate` accepts either form.** The code is normalised before lookup: every
  non-alphanumeric character is stripped and the rest upper-cased. `MRPZB-AXZYY`, `MRPZBAXZYY`,
  `mrpzb-axzyy` and `mrpzb axzyy` all resolve to the same invitation.

So a six-box code input will not work — it needs ten characters, and it can accept the dash or
not, as suits the design. The endpoint description in the contract now states all of this.

### Decisions closed

| Question | Decision |
|---|---|
| Should `sport` become an enum? | **No.** Required free text for v1 — the mobile UI already takes text entry. Revisit only if a fixed list is agreed with the client. |
| Admin athlete-edit endpoint? | **Deferred.** Outside phase 3. Athletes still edit their own profile by re-posting `POST /athletes/me/profile`. |
| Hide athletes with incomplete profiles from the coach's list? | **No, keep them visible** with `fullName: null`. Mobile shows the email instead. The coach invited them and needs to see they have not finished. |

### Note on the path

The contract lives at **`contract/openapi.yaml`**, singular — not `contracts/`.

---

## Phase 2 — Athlete management (Admin)

**One breaking change**, then additions.

### Breaking

`UserSummary` gains **`athleteListSort`** (nullable). It appears inside `AuthResponse` on every
authentication response, and on `GET /auth/me`. Null for athletes, and null for a coach who has
not chosen a sort.

### Added

| Method | Path | Auth | Purpose |
|---|---|---|---|
| `GET` | `/api/v1/athletes` | **Admin** | Athlete list — search, filter, sort, page |
| `GET` | `/api/v1/athletes/{id}` | **Admin** | One athlete, read-only |
| `POST` | `/api/v1/athletes/{id}/pause` | **Admin** | Suspend access |
| `POST` | `/api/v1/athletes/{id}/reactivate` | **Admin** | Restore access |
| `PUT` | `/api/v1/auth/me/preferences` | bearer | Save the athlete-list sort |

### The list

Query parameters, all optional, each with a documented default in the contract:

| Parameter | Values | Default |
|---|---|---|
| `search` | free text — matches **full name or sport** | none |
| `status` | `All` \| `Active` \| `Paused` | `All` |
| `sort` | `NameAsc` \| `NameDesc` \| `Sport` \| `NewestFirst` \| `OldestFirst` | `NameAsc` |
| `page` | 1-based | `1` |
| `pageSize` | 1–100 | `20` |

> **Superseded by phase 3:** `search` also matches **email**, so an athlete who has not
> completed their profile — and therefore has no name — is still findable. The table is left as
> written, to keep the record of what phase 2 actually shipped.

- **Search is trimmed, case-insensitive, and matches any part of the value.** `%` and `_` are
  matched literally, so a search for `%` finds nothing rather than everything.
- **`page` and `pageSize` are clamped, not rejected.** `pageSize=5000` returns 100;
  `page=0` returns page 1. No 400 for out-of-range paging.
- **`sort=Sport` places athletes with no sport last**, and every sort breaks ties on id, so a row
  cannot swap pages between requests and appear twice or vanish.
- **Paused athletes always appear.** Pausing hides an athlete from themselves, never from
  their coach — filter with `status` to exclude them.

Response is `PagedResultOfAthleteListItem`: `items`, `page`, `pageSize`, `totalCount`,
`totalPages`, `hasNextPage`, `hasPreviousPage`.

### `status` is account status, not package status

`Active`/`Paused` here means **whether the athlete can sign in**. It is *not* the
Active/Inactive filter in the product specification, which means "has an active package" and is
derived from package data. That arrives in phase 4 as a **separate** parameter. Merging them
would make a paused athlete and an athlete between packages indistinguishable.

Sessions remaining and "no active package" are likewise phase 4 and absent from `AthleteListItem`.

### Pause and reactivate

- **Pause** sets status to `Paused` and **revokes every refresh token** the athlete holds. Their
  current access token stays cryptographically valid for its remaining minutes, but each request
  re-checks status, so the next call returns `403 ACCOUNT_PAUSED`. Login returns the same.
- **Reactivate** sets status back to `Active` and **issues no tokens** — the athlete signs in
  again. Tokens revoked at pause stay revoked.
- **Both are idempotent.** Pausing an already-paused athlete succeeds and changes nothing, so a
  retry after a dropped connection is safe.
- Both return `AthleteStatusResponse` (`id`, `status`), so a list row can update without a refetch.

### Not found, not forbidden

An unknown id, **another coach's athlete**, a deleted athlete, and the Admin's own id all return
`404 ATHLETE_NOT_FOUND`. A 403 would confirm the record exists.

### Sort preference

Stored server-side in `Users.UiPreferences`, so it survives a restart and follows the coach to
another device. `PUT /api/v1/auth/me/preferences` with `{"athleteListSort":"NewestFirst"}` saves
it; the value comes back on **every authentication response and on `/auth/me`**, so the app never
needs an extra call to apply it.

An unrecognised enum value now returns **`400 VALIDATION_FAILED`**. It previously returned 500 —
a malformed body is the caller's fault, and that applies to every endpoint, not only this one.

### Error codes added

| `errorCode` | Status | Meaning |
|---|---|---|
| `ATHLETE_NOT_FOUND` | 404 | Unknown, foreign, or deleted athlete |

### Known gaps

- **`phone` is always null.** The field is in `AthleteDetail` as the specification requires, but
  no screen collects a phone number yet — Complete Profile does not ask for one.
- **Device tokens are not revoked on pause.** Only refresh tokens are. The `DeviceTokens` table
  arrives with notifications in phase 10; until then there is nothing to revoke.
- **Profile photo** is absent pending file storage (phase 13).
- **No edit endpoint.** See the note below — this is a deliberate deviation from the product
  specification and needs the client's confirmation.

---

## Phase 1.3 — `profileCompleted` on every authentication response

**Additive.** One new field; nothing was renamed or removed.

`UserSummary` gains **`profileCompleted`** (boolean, required), so it now appears inside
`AuthResponse` from **all four** authentication endpoints:

- `POST /auth/login`
- `POST /auth/google`
- `POST /auth/refresh`
- `POST /auth/register`

**Why:** the app can now route straight from any successful authentication without a
follow-up `GET /auth/me`. It also fixes a real inconsistency — the register endpoint's
documentation promised `profileCompleted: false`, but its response schema had no field
that could carry it.

```jsonc
// POST /auth/login  →  200
{
  "accessToken": "…", "refreshToken": "…",
  "expiresInSeconds": 900, "refreshExpiresInSeconds": 2592000,
  "user": {
    "id": "…", "role": "Athlete", "status": "Active",
    "fullName": "Robin Vale", "email": "robin@example.com",
    "profileCompleted": false        // ← route to Complete Profile, not Home
  }
}
```

`true` for the Admin always — there is no Complete Profile step for that role.

**`GET /auth/me` is unchanged and still the right call for** restoring a stored session on
app start, picking up role or status changed server-side since the token was issued,
detecting a pause mid-session, and `minimumSupportedAppVersion`. The new field removes the
extra request after *login*, not the endpoint.

---

## Phase 1.2 — Invitations, registration, profile completion

**Additive only.** Nothing existing changed shape, so the Increment 3 work already underway
is unaffected.

### The invited-athlete journey

```
Admin: POST /invitations {email}          → code emailed to the athlete, never returned in the response
Athlete: GET /invitations/validate?code=  → {email, expiresAtUtc, registrationToken}
Athlete: POST /auth/register              → account created, invitation redeemed, tokens returned
Athlete: POST /athletes/me/profile        → profileCompleted becomes true
```

| Method | Path | Auth | Purpose |
|---|---|---|---|
| `POST` | `/api/v1/invitations` | **Admin** | Invite an athlete by email |
| `GET` | `/api/v1/invitations` | **Admin** | List invitations, newest first, `?status=` optional |
| `POST` | `/api/v1/invitations/{id}/resend` | **Admin** | Issue a fresh code and email it again |
| `DELETE` | `/api/v1/invitations/{id}` | **Admin** | Revoke a pending invitation |
| `GET` | `/api/v1/invitations/validate` | anonymous | Check a code, get a registration token |
| `POST` | `/api/v1/auth/register` | anonymous | Create the account and redeem the invitation |
| `POST` | `/api/v1/athletes/me/profile` | **Athlete** | Complete or edit the athlete's own profile |

### Behaviour the client must handle

- **Validation does not consume the invitation.** The athlete can validate a code, leave Create
  Account, and come back. The invitation is redeemed only when registration succeeds.
- **The email is already verified.** Only the invited inbox received the code, so Create Account
  shows `email` from the validate response as **read-only**. It cannot be substituted.
- **`registrationToken` is short-lived — 30 minutes** (`registrationTokenExpiresInSeconds`). If
  the athlete takes longer, registration returns `REGISTRATION_TOKEN_INVALID` and they must
  enter the code again. The code itself is not posted to `/auth/register`.
- **Register takes exactly one credential:** `password` (plus `fullName`) **or** `googleIdToken`.
  Both, or neither, is a validation error.
  *(Superseded by phase 3: `fullName` was removed from register.)*
- **Google registration must match the invited address.** A verified Google email different from
  the invitation returns `GOOGLE_EMAIL_MISMATCH`. No password or name is required on that path —
  Google's display name is used as an editable prefill.
- **`termsAccepted` must be true**, or `TERMS_NOT_ACCEPTED`.
  *(Superseded by phase 3: the field and the error code are both removed. The app ships with
  no Terms of Service and no Privacy Policy, so there is nothing to accept.)*
- **Register returns the same token pair as login**, so the athlete is signed in immediately —
  but `profileCompleted` is false. Route to **Complete Profile**, not Home.
- **Resending replaces the code.** The previously emailed code stops working at once.
- **Validate is rate-limited per IP** (10/minute by default). Exceeding it returns
  `429 TOO_MANY_REQUESTS` with `retryAfterSeconds` and a `Retry-After` header.
  *(Superseded by phase 3: the limit is 10 per **hour**, matching the architecture. This entry
  was the drift.)*

### Error codes added

| `errorCode` | Status | Meaning |
|---|---|---|
| `INVITATION_INVALID` | 400 | No such code |
| `INVITATION_EXPIRED` | 400 | Past its expiry (14 days by default) |
| `INVITATION_USED` | 400 | Already redeemed |
| `INVITATION_REVOKED` | 400 | Cancelled by the coach |
| `REGISTRATION_TOKEN_INVALID` | 400 | Registration session expired — re-enter the code |
| `GOOGLE_EMAIL_MISMATCH` | 400 | Google account's email is not the invited address |
| `TERMS_NOT_ACCEPTED` | 400 | `termsAccepted` was false — **removed in phase 3** |
| `EMAIL_ALREADY_REGISTERED` | 409 | An account already exists for that address |
| `PROFILE_ALREADY_COMPLETED` | 409 | Reserved; profile edits are currently allowed |
| `TOO_MANY_REQUESTS` | 429 | Rate limit hit |

Four codes rather than one for invitations, so the Invitation Error screen can show the right
message and the right next action for each case.

### Known gaps

- **Profile photo is not accepted yet.** `POST /athletes/me/profile` takes `fullName`,
  `dateOfBirth`, `gender` and `sport`. Photo upload needs file storage, which is phase 13.
- **`gender` and `sport` are free strings.** The UI shows a dropdown and a searchable field, but
  no allowed value list exists in any source document. Constraining them later to enums **is a
  contract change** — agree the lists with the client before the mobile screens harden.
  *(Closed in phase 3 for `gender`, now the `Female`/`Male` enum. `sport` is still free text:
  required, but with no agreed list.)*

---

## Phase 1.1 — Contract hardening, Google sign-in, change password

**Breaking.** Read this before regenerating the client.

### Breaking changes

| Change | Was | Now |
|---|---|---|
| Current-user path | `GET /api/v1/me` | **`GET /api/v1/auth/me`** (matches architecture §14.1) |
| Logout auth | anonymous | **requires a bearer token** as well as the refresh token in the body (architecture §14.1 marks logout `B`) |
| `UserSummary.role` | free string | **`UserRole` enum** — `Admin` \| `Athlete` |
| `UserSummary` | no status | **`status` added** — `UserStatus`: `Active` \| `Paused` \| `Deleted` |
| Error body | framework `ProblemDetails` | **`ApiProblemDetails`** with `errorCode` and `correlationId` declared |
| Numeric fields | `type: [integer, string]` | **`type: integer`** — the union was a generator artefact; the API only ever wrote numbers |
| Reset link | `https://app.beyondmovement.com/...` | **`beyondmovement://reset-password?token=…`** |

### Added

| Method | Path | Auth | Purpose |
|---|---|---|---|
| `POST` | `/api/v1/auth/google` | anonymous | Google ID-token sign-in |
| `POST` | `/api/v1/auth/change-password` | **bearer** | Change password while signed in |

New response fields:

- `AuthResponse.refreshExpiresInSeconds` — refresh-token lifetime (30 days), alongside the
  existing 15-minute `expiresInSeconds` for the access token.
- `CurrentUserResponse` — the now-documented `200` from `/auth/me`, carrying `id`, `role`,
  `status`, `fullName`, `email`, `coachId`, `profileCompleted` and `minimumSupportedAppVersion`.
- `ApiProblemDetails.retryAfterSeconds` — present on `ACCOUNT_LOCKED`, mirrored in the
  `Retry-After` header, so the app can show a real countdown.
- `ApiProblemDetails.errors` — per-field validation messages, present on `VALIDATION_FAILED`.

### Error codes

Declared as an enum on `ApiProblemDetails.errorCode`, so the generated client gets a checkable
set. Switch on this — never on `title`, which is human-readable text that may change.

| `errorCode` | Status | Meaning |
|---|---|---|
| `VALIDATION_FAILED` | 400 | Request shape rejected; see `errors` for per-field detail |
| `INVALID_CREDENTIALS` | 401 | Wrong password **or** unknown address — deliberately indistinguishable |
| `ACCOUNT_LOCKED` | 423 | Five failed attempts; see `retryAfterSeconds` |
| `ACCOUNT_PAUSED` | 403 | Paused account. Can arrive on *any* authenticated request |
| `INVALID_TOKEN` | 401 | The access token no longer maps to a live user |
| `INVALID_REFRESH_TOKEN` | 401 | Expired, revoked, or replayed. Sign the user out |
| `INVALID_RESET_TOKEN` | 400 | Reset link expired or already used |
| `INVALID_GOOGLE_TOKEN` | 401 | Google ID token failed verification, or its email is unverified |
| `INVITATION_REQUIRED` | 403 | Google sign-in with no matching account. **Not** a registration path |
| `PASSWORD_NOT_SET` | 400 | Google-only account has no password to change |

### Behaviour the client must handle

- **Refresh rotation is single-use.** Each refresh returns a *new* refresh token; store it and
  discard the old one. Replaying a spent token revokes the entire family, so a client that
  retries with a stale token logs the user out for real.
- **`ACCOUNT_PAUSED` can appear mid-session.** An access token stays cryptographically valid for
  its full 15 minutes after an admin pauses the account, so the server re-checks status on every
  authenticated request. Now explicitly documented as a `403` on login, refresh and `/auth/me`.
- **`profileCompleted: false` means route to Complete Profile, not Home.** An athlete who has
  created an account but not finished the profile step comes back this way.
- **Google sign-in never registers.** Unknown Google account with no matching user →
  `403 INVITATION_REQUIRED` (BR-01). If a password account exists with the same *verified*
  Google email, the Google identity is linked to it and tokens are returned.
- **Change-password revokes every refresh token,** including the calling device's. The app must
  sign in again afterwards.
- **`/auth/forgot-password` always returns 200**, whether or not the address exists. Never show
  "no account found".
- **Password rule:** minimum 8 characters, rejected if on a common-password list. No composition
  rules (architecture §7.2).

### Password reset deep link

The email contains:

```
beyondmovement://reset-password?token=<url-encoded token>
```

The token is **URL-encoded** in the link — decode it before posting to `/auth/reset-password`.
It is **single-use** and expires after **one hour**. A used, unknown, or expired token returns
`400 INVALID_RESET_TOKEN`. On success every refresh token for that user is revoked.

Configured server-side via `App:PasswordResetUrlTemplate`, so switching to an HTTPS App Link
later is a configuration change, not a contract change.

### Not yet in the contract

Invitations (`POST /invitations`, `GET /invitations/validate`), `POST /auth/register`, and
athlete profile completion arrive next. There is no registration endpoint by design (BR-01) —
accounts are created by invitation only, and Google sign-in does not bypass that.

---

## Phase 1 — Authentication & Access

**Added — the first published endpoints.**

| Method | Path | Auth | Purpose |
|---|---|---|---|
| `POST` | `/api/v1/auth/login` | anonymous | Email + password to an access token and a refresh token |
| `POST` | `/api/v1/auth/refresh` | anonymous | Rotate a refresh token for a new pair |
| `POST` | `/api/v1/auth/logout` | anonymous | Revoke the presented refresh token |
| `POST` | `/api/v1/auth/forgot-password` | anonymous | Request a reset link |
| `POST` | `/api/v1/auth/reset-password` | anonymous | Set a new password using a reset token |
| `GET` | `/api/v1/me` | bearer | The caller's identity, read from the token |
| `GET` | `/api/v1/ping` | anonymous | Liveness check |

**Security scheme:** `bearerAuth` (HTTP bearer, JWT). Send the `accessToken` from
`/auth/login` as `Authorization: Bearer <token>`. Access tokens last 15 minutes;
refresh tokens last 30 days.
# Phase 5 — Scheduling & Calendly

Added the backend-first native Calendly scheduling contract:

| Method | Path | Auth | Purpose |
|---|---|---|---|
| `GET` | `/api/v1/scheduling/session-types` | Athlete | Explicitly mapped bookable event types |
| `GET` | `/api/v1/scheduling/session-types/{eventTypeId}/availability` | Athlete | Normalized UTC Calendly slots (maximum 31-day range) |
| `POST` | `/api/v1/scheduling/bookings` | Athlete | Native booking; identity comes from the bearer token |
| `GET` | `/api/v1/sessions` | Both | Ownership-scoped, cursor-paginated schedule |
| `GET` | `/api/v1/sessions/upcoming` | Both | Upcoming scheduled sessions |
| `GET` | `/api/v1/sessions/{id}` | Both | Ownership-scoped session details |
| `POST` | `/api/v1/sessions/{id}/cancel` | Both | Native Calendly cancellation, retained in history |
| `GET` | `/api/v1/sessions/{id}/reschedule` | Both | Calendly reschedule-flow URL |
| `POST` | `/api/v1/webhooks/calendly` | Signed anonymous | Fast, idempotent raw webhook receipt |
| `POST` | `/api/v1/scheduling/refresh` | Admin | Enqueue an on-demand reconciliation run |

Calendly provider DTOs and credentials are not part of the mobile contract. Dates are UTC.
Booking requires an `Idempotency-Key` request header and never alters package balance; attendance remains
Phase 6. New stable scheduling errors include `CALENDLY_UNAVAILABLE`, `EVENT_TYPE_INVALID`,
`SLOT_UNAVAILABLE`, `BOOKING_IN_PROGRESS`, `CALENDLY_RATE_LIMITED`, location/time-zone validation,
and `SESSION_NOT_FOUND`.

---
