# API contract changelog

Every change to a request or response shape is a breaking change for the Flutter app.
Record it here, regenerate `openapi.yaml`, and tell the mobile developer.

To regenerate: run the API, fetch `GET /openapi/v1.json`, and convert it to YAML.

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
| `SessionResponse` | `id`, `athleteProfileId`, `startUtc`, `endUtc`, `durationMinutes`, `deliveryType`, `status`, `locationOrPlatform`, `meetingUrl`, `rescheduleUrl` |
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
