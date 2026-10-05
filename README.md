# Beyond Movement Backend

ASP.NET Core 10 modular monolith on PostgreSQL. The Flutter app lives in a separate
repository; the two are connected only by [`contract/openapi.yaml`](contract/openapi.yaml).

- **Conventions, flows and traps — read this first** → `skills/CLAUDE (1).md`
- **What the API actually does today** → [`contract/CHANGELOG.md`](contract/CHANGELOG.md)
- **Exact request/response shapes** → [`contract/openapi.yaml`](contract/openapi.yaml)
- **What the product must do** → `skills/product-specification (1).md`
- **How the system is built** → `skills/software-architecture (1).md`
- **What we build next** → `skills/development-roadmap (1).md`

> **The four `skills/` documents are the original brief and lag behind the code** wherever the
> client has changed direction. Packages is the live example: Phase 4 shipped a *catalogue*,
> while the specification still describes *purchased* packages. Both documents now carry a
> warning at the relevant section. Read them for intent; read the changelog for behaviour.

---

## What is built

| Area | State |
|---|---|
| Auth — login, JWT, rotating refresh with family revocation, lockout, paused checks | ✅ |
| Google sign-in — authenticates only, never creates an account | ✅ |
| Password reset and change password, rate limited | ✅ |
| Invitations — create, validate, resend, revoke, redeem | ✅ |
| Registration and Complete Profile | ✅ |
| Athlete list — search, filter, sort, paging, pause, reactivate | ✅ |
| Package catalogue — options, features, archive/restore, loyalty, per-athlete prices | ✅ |
| Scheduling — Calendly booking, availability, webhooks, reconciliation, sessions | ✅ |
| Purchased packages — purchase at the quoted price, balance, history, close, one active each | ✅ |
| Attendance — mark attended or no-show, exactly-once deduction, observations, session notes | ✅ |
| Session note images — private S3, pre-signed two-phase upload, verified on completion | ✅ |
| To-dos, finance, chat, notifications | Not started |

**223 tests** — `dotnet test` needs Docker for the integration suite.

## Working on this with someone else

- **Each developer has their own user secrets and their own local database.** The values do not
  need to match. Nothing secret is committed; `.env` and user secrets are both gitignored.
- **Regenerate `contract/openapi.yaml` whenever an endpoint changes**, and write what changed in
  `contract/CHANGELOG.md`. The mobile developer reads those two files and nothing else — a change
  that is not in them does not exist as far as the app is concerned.
- **One migration per logical change**, named descriptively. Never edit a migration that has been
  applied — add a new one. Two people generating migrations at once will conflict on
  `AppDbContextModelSnapshot.cs`; regenerate rather than hand-merging it.
- **Modules never reference each other.** If a feature needs two, it goes in the Api project —
  `AthleteDirectory` and `CatalogueReader` are the pattern to copy.
- `dotnet build` treats warnings as errors. A build that is not clean will not pass CI.

---

## Running it locally

### 1. Install

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) — `dotnet --version` should print `10.x`
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) — must show "Engine running"

### 2. Database

```bash
cp .env.example .env      # then edit the passwords if you like
docker compose up -d
```

`.env` is gitignored and only Docker Compose reads it. If port 5432 is already taken on
your machine — a locally installed PostgreSQL will take it — change `POSTGRES_HOST_PORT`
in `.env` and the connection string below to match.

### 3. Secrets

Two values are required, and neither may live in a committed file.

```bash
dotnet user-secrets init --project src/BeyondMovement.Api

# Signing key for JWTs. Generate a random one - it is not obtained from anywhere.
dotnet user-secrets set "Jwt:SigningKey" "<paste 64 random characters>" --project src/BeyondMovement.Api

# Password for the Admin account that is seeded on first run.
dotnet user-secrets set "Seed:AdminPassword" "<choose one>" --project src/BeyondMovement.Api

# Connection string, if your Postgres is not on the default port.
dotnet user-secrets set "ConnectionStrings:Postgres" \
  "Host=localhost;Port=5432;Database=mentalcoaching;Username=mc;Password=<from .env>" \
  --project src/BeyondMovement.Api
```

Generate a key on Windows:

```powershell
[Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Max 256 }))
```

The app refuses to start without a signing key, and tells you this exact command.

### 4. Run

```bash
dotnet run --project src/BeyondMovement.Api
```

In Development it applies migrations and seeds the Admin automatically, so there is no
separate database setup step. The API listens on **http://localhost:5229**.

Check it worked:

| URL | Expect |
|---|---|
| http://localhost:5229/health | `Healthy` — proves the database connection |
| http://localhost:5229/api/v1/ping | `{"message":"pong"}` |
| http://localhost:5229/scalar/v1 | Browsable API, for trying endpoints by hand |
| http://localhost:5229/openapi/v1.json | The contract, as served |

Sign in as the seeded Admin with `Seed:AdminEmail` from `appsettings.json` and the
password you chose above. [`requests.http`](requests.http) walks the whole auth flow if
you use the VS Code REST Client extension.

---

## Looking at the database

`docker compose up -d` also starts **pgAdmin** on <http://localhost:5050>. Log in with the
`PGADMIN_DEFAULT_EMAIL` / `PGADMIN_DEFAULT_PASSWORD` values from your `.env`.

Then *Add New Server*:

| Field | Value |
|---|---|
| Name | anything |
| **Host** | **`postgres`** |
| **Port** | **`5432`** |
| Database | `mentalcoaching` |
| Username / Password | `POSTGRES_USER` / `POSTGRES_PASSWORD` from `.env` |

**The host is `postgres`, not `localhost`.** pgAdmin runs inside Docker, so it reaches the
database over the container network, where the service is named `postgres` and still
listens on 5432. `localhost:5433` is the address from *your machine*, which pgAdmin
cannot see. Getting this wrong gives "could not translate host name".

Tables are three levels down, which is where most people conclude the database is empty:

```
Servers → <your server> → Databases → mentalcoaching → Schemas → public → Tables
```

Make sure you are under **`mentalcoaching`** and not the default `postgres` database,
which genuinely is empty.

### From your machine instead

A desktop client such as DBeaver, or `psql`, connects to `localhost` on the port in
`POSTGRES_HOST_PORT` — **5432 by default, but check your `.env`**, since a locally
installed PostgreSQL often already owns that port.

Quickest look of all, no GUI:

```bash
docker exec -it mc-postgres psql -U mc -d mentalcoaching -c '\dt'
docker exec -it mc-postgres psql -U mc -d mentalcoaching -c 'select "Email","Role","Status" from "Users";'
```

Column names are PascalCase, so the double quotes are required — unquoted identifiers get
folded to lowercase by PostgreSQL and the query fails.

> Reading is fine. Do not edit rows by hand to change application state — pausing an
> account, redeeming an invitation and so on all have endpoints, and the API keeps
> invariants the database alone does not.

---

## Getting a test athlete account

A fresh database has **only the Admin**. Athlete screens need an athlete, and there is no
public sign-up by design (BR-01) — accounts exist only by invitation. The whole loop takes
about a minute.

**1. Sign in as the Admin** and keep the `accessToken`:

```bash
curl -s -X POST http://localhost:5229/api/v1/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"lillysynchro@gmail.com","password":"<your Seed:AdminPassword>"}'
```

**2. Invite an athlete** (any address — nothing is actually sent):

```bash
curl -s -X POST http://localhost:5229/api/v1/invitations \
  -H "Authorization: Bearer <accessToken>" \
  -H "Content-Type: application/json" \
  -d '{"email":"athlete@example.com"}'
```

**3. Read the code from the API console.** The terminal running `dotnet run` prints the
email the stub "sent":

```
Your invitation code is: MRPZB-AXZYY
```

**4. Validate it** — this does not consume the invitation, and returns a
`registrationToken` valid for 30 minutes:

```bash
curl -s "http://localhost:5229/api/v1/invitations/validate?code=MRPZB-AXZYY"
```

**5. Create the account** with that token:

```bash
curl -s -X POST http://localhost:5229/api/v1/auth/register \
  -H "Content-Type: application/json" \
  -d '{"registrationToken":"<from step 4>","termsAccepted":true,
       "password":"Athlete#Strong2026","fullName":"Alex Thompson"}'
```

You are now signed in as the athlete, with `profileCompleted: false` — which is exactly
the state the app must route to **Complete Profile** rather than Home. Finish it with
`POST /api/v1/athletes/me/profile`, after which `GET /api/v1/auth/me` reports
`profileCompleted: true`.

From then on that athlete signs in normally with email and password.

All of this is also in [`requests.http`](requests.http) as clickable requests if you use
the VS Code REST Client extension, and every endpoint can be driven from the Scalar UI at
<http://localhost:5229/scalar/v1>.

---

## Connecting the Flutter app

### Base URL — this is the usual first stumble

`localhost` means *the device*, not your machine.

| Running on | Base URL |
|---|---|
| Android emulator | `http://10.0.2.2:5229` |
| iOS simulator | `http://localhost:5229` |
| Physical phone | `http://<your-machine-LAN-IP>:5229` |

For a physical device the server must also listen beyond loopback:

```bash
dotnet run --project src/BeyondMovement.Api --urls http://0.0.0.0:5229
```

…and your firewall must allow inbound 5229 on the private network.

### Cleartext HTTP

Local development is plain HTTP. Android blocks that by default, so debug builds need
`android:usesCleartextTraffic="true"` (or a network security config limited to the dev
host) in the **debug** manifest only — never in release.

### Flutter Web (the PWA)

A browser only lets the PWA read an API response if the API names the page's origin in
`Access-Control-Allow-Origin`. The native apps send no `Origin` header, so nothing here affects
them.

**Locally, nothing needs configuring.** `appsettings.Development.json` sets
`Cors:AllowLocalhostOrigins` to `true`, which allows `localhost`, `127.0.0.1` and `[::1]` on any
port, over http or https. `flutter run -d chrome` picks a new port every run, so an exact list could
not keep up. Run the API as usual, then from the Flutter project:

```bash
flutter run -d chrome --web-port 8080 --dart-define=API_BASE_URL=http://localhost:5229
```

- **`API_BASE_URL` must be passed.** The Flutter default is `http://localhost:5000`, and nothing
  listens there.
- **`--web-port` is optional for CORS**, since any port works. A fixed port is still worth using,
  because Google Sign-In on the web only accepts origins listed in the Google Cloud console.
- Use the API's `http` profile (`:5229`). The `https` profile (`:7264`) also works for CORS, but
  Chrome rejects it until the dev certificate is trusted (`dotnet dev-certs https --trust`).

**Deployed environments list their exact origins.** These are environment variables with numeric
indexes, like the other arrays in this file:

```text
Cors__AllowedOrigins__0=https://app.example.com
Cors__AllowedOrigins__1=https://staging-app.example.com
```

`appsettings.json` ships the list empty, so a deployment that sets none allows no browser at all.
The app refuses to start if an origin is not exact (a trailing slash, a path, or `*`), if one is
not `https` outside Development, or if `Cors__AllowLocalhostOrigins` is `true` outside
Development.

What the policy allows, taken from the client's Dio setup:

| | |
|---|---|
| Methods | `GET` `POST` `PUT` `DELETE`. The middleware answers the `OPTIONS` preflight itself. There is no `PATCH` |
| Request headers | `Authorization`, `Content-Type`, `Idempotency-Key`, `X-Correlation-ID`, `X-Token-Transport` |
| Exposed response headers | none. The client reads `retryAfterSeconds` and `correlationId` from the body |
| Credentials | allowed, for trusted origins only, so the auth calls can carry the refresh cookie (below) |
| Preflight cache | 10 minutes |

To add a header or method, edit `WebClientCors.cs` and the CORS tests together.

#### The PWA's refresh token is an HttpOnly cookie

Native apps get the refresh token in JSON, as they always have. The PWA opts in to keeping it in
a cookie that no script can read. It does this by sending `X-Token-Transport: cookie` on its auth
calls, with credentials (`withCredentials: true` in Dio):

| Endpoint | With `X-Token-Transport: cookie` |
|---|---|
| `POST /auth/login`, `/auth/google`, `/auth/register` | Sets the cookie. `refreshToken` in the JSON is `null` |
| `POST /auth/refresh` | Reads the token from the cookie (no body needed). On success it replaces the cookie; a failure never touches it |
| `POST /auth/logout` | Reads the cookie, ends that sign-in, expires the cookie |
| `POST /auth/change-password`, `/auth/reset-password` | On success, also expires the cookie |

The cookie is `__Secure-bm_refresh`, with `HttpOnly; Secure; SameSite=Strict; Path=/api/v1/auth`.
It has no `Domain`, so it belongs to the API host alone. Its `Max-Age` is the refresh token's
lifetime.

- **Only a trusted origin may use it.** A cookie-transport request whose `Origin` is not on the list
  above, or is missing, gets `403 ORIGIN_NOT_ALLOWED`, which together with `SameSite=Strict` is
  the CSRF protection. Any header value other than `cookie` is `400 TOKEN_TRANSPORT_UNSUPPORTED`.
  Ordinary endpoints never read the cookie; they still need `Authorization: Bearer`.
- **The PWA and the API must be same-site**, e.g. `app.beyondmovementbyn.com` and
  `api.beyondmovementbyn.com`. A cookie on any other domain would be a third-party cookie, and
  browsers block those.
- **Locally**, `http://localhost:<port>` → `http://localhost:5229` is same-site too. Chrome and
  Firefox accept a `Secure` cookie from `http://localhost`. Safari does not, so develop in Chrome,
  or use the API's https profile.

### Reading the emails

`docker compose up -d` starts **Mailpit**, a local mail server. Real email is sent to it over
SMTP and delivered nowhere else, so invitation codes and reset links can be opened and clicked
with no provider account, no domain, and no chance of reaching a real person.

**Open <http://localhost:8025>** and the invitation appears there, logo and all, the moment
`POST /api/v1/invitations` returns.

No configuration is needed — `appsettings.Development.json` already points at it. The API
prints which transport it chose on startup:

```
Email: sending over SMTP to localhost:1025. With the default development setup that is
Mailpit — open http://localhost:8025 to read it.
```

If Mailpit is not running the API falls back to printing emails to its own console, and says
so. Nothing breaks either way.

### Google sign-in

The app performs the native sign-in and posts the resulting **ID token** to
`POST /api/v1/auth/google`. The mobile app holds no API secret; the client IDs are in
`appsettings.json` and are public by design.

Google sign-in **authenticates, it never registers** (BR-01). An unknown Google account
returns `403 INVITATION_REQUIRED` — show "ask your coach", never a sign-up prompt.

### Reset deep link

```
beyondmovement://reset-password?token=<url-encoded token>
```

URL-decode the token before posting it to `/auth/reset-password`. Single use, one hour.

---

## Sending real email

The API sends two messages: the **invitation code** and the **password-reset link**. Their
wording and markup live in one file, `EmailTemplates`, so copy can be reviewed without
reading handler code. Every message carries an HTML body and a plain-text body — clients
that refuse HTML must still show the code, and a message with no text part scores worse
with spam filters.

The API picks a transport at startup, in this order: **Postmark**, then **SMTP** (Mailpit
locally), then the **console**. Development needs none of the steps below — Mailpit already
covers it. Postmark is for staging and production, where mail must reach a real person.

To send for real:

**1. Create a Postmark account** and verify a sending domain — add the DKIM and Return-Path
DNS records Postmark gives you. Unverified domains are rejected, and mail that skips SPF and
DKIM lands in spam, which for an invitation means the athlete never joins (BR-01).

**2. Configure it.** The token is a secret and belongs in user secrets, never a file:

```bash
dotnet user-secrets set "Email:Postmark:ServerToken" "<server token>" --project src/BeyondMovement.Api
dotnet user-secrets set "Email:FromAddress" "no-reply@yourdomain.com" --project src/BeyondMovement.Api
```

`Email:FromName` and `Email:Postmark:MessageStream` are non-secret and already in
`appsettings.json`. Keep the stream as `outbound` — Postmark separates transactional from
broadcast mail, and sending invitations on a broadcast stream harms deliverability for both.

In deployment these arrive as `Email__Postmark__ServerToken` and `Email__FromAddress`.

**3. Restart.** The startup warning disappears and mail goes out through Postmark. A refused
send throws with Postmark's own reason attached — an unconfirmed sender signature is the
usual first-time cause.

### Sending real email from a local machine, via Gmail

Only for a developer who needs mail to reach an actual inbox before the API is deployed —
testing on a real phone, say. **Mailpit is the better tool for ordinary integration work:** it
shows the message instantly at `http://localhost:8025`, lets you read raw headers, and cannot
disturb a real person. Reach for this only when Mailpit genuinely will not do.

It needs the shared mailbox's **Google app password** — a 16-character code from
myaccount.google.com → Security → App passwords, not the account password, and only available
once 2-Step Verification is on. Ask whoever owns the mailbox; never commit it, and never paste
it into a chat window, because that burns it and it has to be rotated.

Six keys, because the development defaults point at Mailpit and every one of them has to be
overridden:

```bash
cd Beyond-Movement-Backend

dotnet user-secrets set "Email:FromAddress"   "beyondmovementbyn@gmail.com" --project src/BeyondMovement.Api
dotnet user-secrets set "Email:Smtp:Host"     "smtp.gmail.com"              --project src/BeyondMovement.Api
dotnet user-secrets set "Email:Smtp:Port"     "587"                         --project src/BeyondMovement.Api
dotnet user-secrets set "Email:Smtp:UseSsl"   "true"                        --project src/BeyondMovement.Api
dotnet user-secrets set "Email:Smtp:Username" "beyondmovementbyn@gmail.com" --project src/BeyondMovement.Api
dotnet user-secrets set "Email:Smtp:Password" "<the 16-character app password, no spaces>" --project src/BeyondMovement.Api
```

Strip the spaces Google shows in the app password — `abcd efgh ijkl mnop` is `abcdefghijklmnop`.

Leave `Email:FromName` alone. It is empty on purpose: a brand display name over an `@gmail.com`
address is the shape of phishing, and Gmail files it as spam. This was measured, not guessed.

Restart, and the startup line tells you which transport won:

```
Email: sending REAL mail via smtp.gmail.com:587 as beyondmovementbyn@gmail.com.
Messages will reach real inboxes.
```

If it still says *local mail catcher*, one of the six did not take — most often `Email:FromAddress`,
without which SMTP is not considered configured at all.

**Expect the invitation to land in spam.** Gmail's consumer SMTP sends from a shared domain with
no SPF or DKIM of ours, so a recipient who has never corresponded with the address will find it
filtered. Marking it *Not spam* fixes that inbox and only that inbox. A verified sending domain
is the only real fix, and it comes with deployment.

To go back to Mailpit, remove the overrides:

```bash
for key in Email:FromAddress Email:Smtp:Host Email:Smtp:Port Email:Smtp:UseSsl Email:Smtp:Username Email:Smtp:Password; do
  dotnet user-secrets remove "$key" --project src/BeyondMovement.Api
done
```

### Putting the logo in the emails

Save the logo as `src/BeyondMovement.Api/wwwroot/brand/logo.png` — the API serves that folder
publicly — then point the templates at it:

```bash
dotnet user-secrets set "Email:LogoUrl" "https://api.yourdomain.com/brand/logo.png" --project src/BeyondMovement.Api
```

Committing the file is not enough on its own: mail clients fetch the image over the internet
when the message is opened, so the URL must be the **public HTTPS address of the deployed
API**, not `localhost`. A `data:` URI is not an alternative — Gmail strips them, so the logo
would be missing for most recipients while looking correct in local testing.

Leave `Email:LogoUrl` empty and the masthead falls back to the wordmark set as type, which is
also what recipients see when images are switched off. See
[`wwwroot/brand/README.md`](src/BeyondMovement.Api/wwwroot/brand/README.md) for the file
requirements.

> **Not yet handled:** a failed send is not retried. The invitation row already exists, so
> the athlete simply never receives a code and the Admin must resend. Architecture §5 puts
> email behind Hangfire from phase 5 for exactly this reason.

---

## Session note images (S3)

Images attached to session notes live in a **private** S3 bucket. The app uploads and downloads
them directly with short-lived pre-signed URLs, so no image bytes pass through the API. Postgres
holds only metadata and object keys. For the API behaviour, see `contract/CHANGELOG.md` →
"Session Note Image Attachments".

### Configuration

**The bucket is chosen by the deployment, not by the application.** `appsettings.json` ships
`Storage:S3:BucketName` and `Storage:S3:Region` **empty**, and each environment supplies its own.
This is the same convention as `Payments__InstaPay__*`. None of these values is secret.

| Key (env var form) | Shipped default | Meaning |
|---|---|---|
| `Storage__S3__BucketName` | *(empty)* | The private bucket. **Supplied per environment** |
| `Storage__S3__Region` | *(empty)* | The bucket's region. **Required whenever a bucket is set** |
| `Storage__S3__ServiceUrl` | *(empty)* | Only for a local S3 emulator such as MinIO. Leave empty for AWS |
| `Storage__UploadUrlMinutes` | `5` | Lifetime of a pre-signed PUT |
| `Storage__DownloadUrlMinutes` | `15` | Lifetime of a pre-signed GET |
| `Storage__MaxImageBytes` | `10485760` | 10 MB per image |
| `Storage__MaxAttachmentsPerNote` | `5` | Pending plus committed images per note |

The four limits are product rules and have defaults. Only the bucket, region and emulator URL
depend on the environment.

**Production (ECS)** sets these two in the task definition:

```text
Storage__S3__BucketName=beyond-movement-files-745059801486-ap-south-1-an
Storage__S3__Region=ap-south-1
```

How the API behaves with each combination:

| Configuration | Startup | Attachment endpoints |
|---|---|---|
| No bucket (the default: fresh clone, local dev, tests) | Starts. Prints `WARNING  No object storage configured` | `503 STORAGE_UNAVAILABLE`, nothing created. Everything else works |
| Bucket **and** region | Starts. Prints `Storage: session note images in S3 bucket …`. Nothing contacts AWS yet | Work if the process has AWS credentials. Otherwise `503 STORAGE_UNAVAILABLE` |
| Bucket **without** region (and no `ServiceUrl`) | **Refuses to start**: `Storage:S3:Region must be set when Storage:S3:BucketName is` | none |
| `ServiceUrl` that is not an absolute http(s) URL | **Refuses to start** | none |

To try uploads from a developer machine against a real bucket, set the bucket and region with
user secrets and sign in with your own AWS profile:

```bash
dotnet user-secrets set "Storage:S3:BucketName" "<a bucket you may use>" --project src/BeyondMovement.Api
dotnet user-secrets set "Storage:S3:Region" "ap-south-1" --project src/BeyondMovement.Api
```

There is deliberately **no key prefix setting**. Keys are always `session-notes/{noteId}/{attachmentId}.{ext}`,
because the task role's IAM policy allows only `session-notes/*`. Any other prefix would be
denied.

### Credentials: never configured

**Do not put an AWS access key or secret in appsettings, user secrets, `.env`, source control or
the Flutter app.** The API uses the AWS SDK's default credential chain:

- **In ECS**, the chain picks up the task role `BeyondMovementECSTaskRole` automatically. That
  role's inline policy `BeyondMovementSessionNoteAttachments` grants only `s3:PutObject`,
  `s3:GetObject` and `s3:DeleteObject` on `…/session-notes/*`. The task definition supplies
  only the bucket and region shown above, and **no** credential variables.
- **Locally with no bucket or no AWS credentials**, the API still starts and every other endpoint
  works. Only the attachment endpoints fail with `503 STORAGE_UNAVAILABLE`, as do note reads that
  include images. Nothing contacts AWS at startup.
- **Locally against a real bucket**, set the bucket and region as above and sign in with your own
  AWS profile (`aws sso login` or `AWS_PROFILE`). The SDK finds it, and your own IAM permissions
  apply.
- **Tests** never touch AWS. `ApiFactory` replaces storage with an in-memory `FakeObjectStorage`.

The API's own CORS settings (`Cors:*`, see [Flutter Web](#flutter-web-the-pwa)) cover the PWA
calling the API only. The bucket has no CORS configuration yet. That will be set up when web image
uploads are built, because a browser `PUT`s to the pre-signed URL directly.

### Cleanup

The Hangfire recurring job `session-note-attachment-cleanup` runs every 30 minutes (when
`Jobs__Enabled` is true). It deletes objects left by uploads that were never completed, and by
images whose note was deleted, then deletes their rows. It always deletes the object before the
row, so a failed S3 delete is simply retried on the next run. The bucket's lifecycle rule that
aborts incomplete multipart uploads after 1 day is only a secondary safety net.

---

## Everyday commands

```bash
docker compose up -d                                  # database
dotnet run --project src/BeyondMovement.Api           # API on :5229
dotnet build                                          # zero warnings expected
dotnet test                                           # needs Docker: uses Testcontainers

# migrations
dotnet ef migrations add <Name> -p src/BeyondMovement.Infrastructure -s src/BeyondMovement.Api
dotnet ef database update    -p src/BeyondMovement.Infrastructure -s src/BeyondMovement.Api

# regenerate the contract after changing an endpoint, then note it in contract/CHANGELOG.md
curl -s http://localhost:5229/openapi/v1.json -o contract/openapi.json
```

---

## When something is wrong

| Symptom | Cause |
|---|---|
| Refuses to start, mentions `Jwt:SigningKey` | Step 3 was skipped |
| `/health` is `Unhealthy` | Docker is not running, or the port in the connection string is wrong |
| `password authentication failed for user "mc"` | Another PostgreSQL owns the port — check `POSTGRES_HOST_PORT` |
| Phone or emulator cannot reach the API | `localhost` on a device is the device; see the base URL table |
| Browser console: `blocked by CORS policy` | The page's origin is not in `Cors:AllowedOrigins`, or the API is not running as Development locally. See Flutter Web |
| `401` on every endpoint including login | An endpoint is missing `.AllowAnonymous()` — the fallback policy denies by default |
| Tests fail with "Docker is either not running" | Integration tests need Docker for Testcontainers |
| Changed an entity and nothing happened | EF needs a new migration; the database does not follow the code |
