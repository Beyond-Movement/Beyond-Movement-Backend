# API contract changelog

Every change to a request or response shape is a breaking change for the Flutter app.
Record it here, regenerate `openapi.yaml`, and tell the mobile developer.

To regenerate: run the API, fetch `GET /openapi/v1.json`, and convert it to YAML.

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
- **Google registration must match the invited address.** A verified Google email different from
  the invitation returns `GOOGLE_EMAIL_MISMATCH`. No password or name is required on that path —
  Google's display name is used as an editable prefill.
- **`termsAccepted` must be true**, or `TERMS_NOT_ACCEPTED`.
- **Register returns the same token pair as login**, so the athlete is signed in immediately —
  but `profileCompleted` is false. Route to **Complete Profile**, not Home.
- **Resending replaces the code.** The previously emailed code stops working at once.
- **Validate is rate-limited per IP** (10/minute by default). Exceeding it returns
  `429 TOO_MANY_REQUESTS` with `retryAfterSeconds` and a `Retry-After` header.

### Error codes added

| `errorCode` | Status | Meaning |
|---|---|---|
| `INVITATION_INVALID` | 400 | No such code |
| `INVITATION_EXPIRED` | 400 | Past its expiry (14 days by default) |
| `INVITATION_USED` | 400 | Already redeemed |
| `INVITATION_REVOKED` | 400 | Cancelled by the coach |
| `REGISTRATION_TOKEN_INVALID` | 400 | Registration session expired — re-enter the code |
| `GOOGLE_EMAIL_MISMATCH` | 400 | Google account's email is not the invited address |
| `TERMS_NOT_ACCEPTED` | 400 | `termsAccepted` was false |
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
