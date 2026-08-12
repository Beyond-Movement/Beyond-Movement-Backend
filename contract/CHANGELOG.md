# API contract changelog

Every change to a request or response shape is a breaking change for the Flutter app.
Record it here, regenerate `openapi.yaml`, and tell the mobile developer.

To regenerate: run the API, fetch `GET /openapi/v1.json`, and convert it to YAML.

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
| `GET` | `/api/v1/me` | **bearer** | The caller's identity, read from the token |
| `GET` | `/api/v1/ping` | anonymous | Liveness check |

**Security scheme:** `bearerAuth` (HTTP bearer, JWT). Send the `accessToken` from
`/auth/login` as `Authorization: Bearer <token>`. Access tokens last 15 minutes;
refresh tokens last 30 days.

### Error codes introduced

The app is expected to switch on `errorCode`, not on the message text. Every error
response is RFC 7807 Problem Details carrying `errorCode` and `correlationId`.

| `errorCode` | Status | Meaning |
|---|---|---|
| `INVALID_CREDENTIALS` | 401 | Wrong password **or** unknown address — deliberately indistinguishable |
| `ACCOUNT_LOCKED` | 423 | Five failed attempts; locked for 15 minutes |
| `ACCOUNT_PAUSED` | 403 | The account is paused. Can arrive on *any* authenticated request, not just login |
| `INVALID_REFRESH_TOKEN` | 401 | Expired, revoked, or replayed. Sign the user out |
| `INVALID_RESET_TOKEN` | 400 | The reset link is expired or already used |
| `VALIDATION_FAILED` | 400 | Request shape rejected; see the `errors` object for per-field detail |

### Behaviour the client must handle

- **Refresh rotation is single-use.** Each refresh returns a *new* refresh token; store it
  and discard the old one. Replaying a spent token revokes the entire token family, so a
  client that retries with a stale token logs the user out for real.
- **`ACCOUNT_PAUSED` can appear mid-session.** An access token stays cryptographically valid
  for its full 15 minutes after an admin pauses the account, so the server re-checks status
  on every authenticated request. Treat a 403 with this code as an immediate sign-out.
- **`/auth/forgot-password` always returns 200**, whether or not the address exists. Do not
  show "no account found" — the endpoint deliberately cannot tell you.
- **Password rule:** minimum 8 characters, rejected if it appears on a common-password list.
  No composition rules.

### Not yet in the contract

Athlete accounts, invitations, and Google sign-in arrive in phase 3. There is no
registration endpoint by design (BR-01) — accounts are created by invitation only.
