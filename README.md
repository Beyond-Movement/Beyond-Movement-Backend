# Beyond Movement Backend

ASP.NET Core 10 modular monolith on PostgreSQL. The Flutter app lives in a separate
repository; the two are connected only by [`contract/openapi.yaml`](contract/openapi.yaml).

- **What the product must do** → `skills/product-specification (1).md`
- **How the system is built** → `skills/software-architecture (1).md`
- **Conventions and rules for this repo** → `skills/CLAUDE (1).md`
- **What changed for the mobile client** → [`contract/CHANGELOG.md`](contract/CHANGELOG.md)

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

### Codes arrive in the terminal, not an inbox

Email delivery is a console stub until a provider is wired up. Invitation codes and
password-reset links are printed to the **API console**. Watch that window after calling
`POST /api/v1/invitations` or `POST /api/v1/auth/forgot-password`.

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
| `401` on every endpoint including login | An endpoint is missing `.AllowAnonymous()` — the fallback policy denies by default |
| Tests fail with "Docker is either not running" | Integration tests need Docker for Testcontainers |
| Changed an entity and nothing happened | EF needs a new migration; the database does not follow the code |
