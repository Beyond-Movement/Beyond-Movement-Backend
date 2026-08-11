# Phase 0 & Phase 1 — Step-by-Step Backend Guide

Written for someone who has **not used .NET before**. Every command is meant to be run exactly as written, from the repository root unless stated otherwise. Windows instructions; macOS/Linux notes where they differ.

Work through this in order. Do not skip ahead — each step depends on the last.

---

# PHASE 0 — Project Setup

**Goal:** a running API that connects to a real PostgreSQL database, logs properly, and has one working endpoint. No features yet.

---

## Step 1 — Install the tools

### 1.1 .NET 10 SDK

Download from <https://dotnet.microsoft.com/download/dotnet/10.0> — choose the **SDK** (not the Runtime), x64 for Windows.

Verify in a **new** terminal:

```bash
dotnet --version
```

You should see `10.0.xxx`. If the command isn't found, restart the terminal so the PATH updates.

> **Why .NET 10 and not 8?** .NET 8 loses Microsoft support on 10 November 2026 — about three months from now. .NET 10 is the current LTS, supported to November 2028.

### 1.2 Docker Desktop

Download from <https://www.docker.com/products/docker-desktop/>. Install, launch it, and wait for the whale icon to say "Engine running".

This runs PostgreSQL for you. Installing Postgres directly also works, but Docker means your machine matches your teammates' and matches production.

Verify:

```bash
docker --version
```

### 1.3 EF Core command-line tool

This is what creates and applies database migrations.

```bash
dotnet tool install --global dotnet-ef
```

Verify:

```bash
dotnet ef --version
```

### 1.4 A database viewer

Install **DBeaver** (<https://dbeaver.io/>) or **pgAdmin**. You'll want to look at your tables with your own eyes, especially early on.

### 1.5 VS Code extensions

- **C# Dev Kit** (Microsoft) — IntelliSense, debugging, project navigation
- **REST Client** (Huachao Mao) — lets you send HTTP requests from a `.http` file

---

## Step 2 — Create the repository

```bash
mkdir mental-coaching-backend
cd mental-coaching-backend
git init
```

Create `.gitignore`:

```bash
dotnet new gitignore
```

Add `CLAUDE.md` (provided separately) at the root, and create a `docs/` folder holding the Product Specification, Software Architecture, UI/UX Design Decisions, and Development Roadmap. Claude Code reads these.

```bash
mkdir docs contract
```

---

## Step 3 — Start PostgreSQL

Create `docker-compose.yml` at the repository root:

```yaml
services:
  postgres:
    image: postgres:16
    container_name: mc-postgres
    environment:
      POSTGRES_USER: mc
      POSTGRES_PASSWORD: mc_dev_password
      POSTGRES_DB: mentalcoaching
    ports:
      - "5432:5432"
    volumes:
      - mc-pgdata:/var/lib/postgresql/data

  redis:
    image: redis:7
    container_name: mc-redis
    ports:
      - "6379:6379"

volumes:
  mc-pgdata:
```

Start it:

```bash
docker compose up -d
```

Check it's running:

```bash
docker ps
```

Now connect with DBeaver: host `localhost`, port `5432`, database `mentalcoaching`, user `mc`, password `mc_dev_password`. The database exists but has no tables yet — that's expected.

> Redis isn't used until phase 9. It's here so the file is done once.

---

## Step 4 — Create the solution and projects

A **solution** (`.sln`) is a container that groups **projects**. Each project compiles to one assembly (`.dll`).

```bash
dotnet new sln -n BeyondMovement

dotnet new web       -o src/BeyondMovement.Api
dotnet new classlib  -o src/BeyondMovement.SharedKernel
dotnet new classlib  -o src/BeyondMovement.Infrastructure

dotnet new xunit     -o tests/BeyondMovement.UnitTests
dotnet new xunit     -o tests/BeyondMovement.IntegrationTests
```

Add them all to the solution:

```bash
dotnet sln add src/BeyondMovement.Api
dotnet sln add src/BeyondMovement.SharedKernel
dotnet sln add src/BeyondMovement.Infrastructure
dotnet sln add tests/BeyondMovement.UnitTests
dotnet sln add tests/BeyondMovement.IntegrationTests
```

Wire up the references — **direction matters**, see CLAUDE.md section 4:

```bash
dotnet add src/BeyondMovement.Infrastructure reference src/BeyondMovement.SharedKernel
dotnet add src/BeyondMovement.Api reference src/BeyondMovement.SharedKernel
dotnet add src/BeyondMovement.Api reference src/BeyondMovement.Infrastructure
dotnet add tests/BeyondMovement.UnitTests reference src/BeyondMovement.SharedKernel
dotnet add tests/BeyondMovement.IntegrationTests reference src/BeyondMovement.Api
```

Confirm everything compiles:

```bash
dotnet build
```

---

## Step 5 — Turn on strict settings

Create `Directory.Build.props` at the repository root. This applies to every project at once, so you don't repeat yourself:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```

> `Nullable enable` makes the compiler tell you when something might be null. It will feel noisy at first. It catches a whole category of runtime crashes — leave it on.

---

## Step 6 — Install the NuGet packages

```bash
# Infrastructure — database access
dotnet add src/BeyondMovement.Infrastructure package Microsoft.EntityFrameworkCore
dotnet add src/BeyondMovement.Infrastructure package Npgsql.EntityFrameworkCore.PostgreSQL

# Api — migrations tooling, logging, validation, OpenAPI
dotnet add src/BeyondMovement.Api package Microsoft.EntityFrameworkCore.Design
dotnet add src/BeyondMovement.Api package Serilog.AspNetCore
dotnet add src/BeyondMovement.Api package FluentValidation.DependencyInjectionExtensions
dotnet add src/BeyondMovement.Api package Microsoft.AspNetCore.OpenApi
dotnet add src/BeyondMovement.Api package Scalar.AspNetCore
```

Don't specify versions — `dotnet add package` picks the latest compatible with .NET 10.

> `Scalar.AspNetCore` gives you a browser UI for testing endpoints. .NET 10 generates the OpenAPI document itself but ships no UI.

---

## Step 7 — The shared kernel

Create `src/BeyondMovement.SharedKernel/IClock.cs`:

```csharp
namespace BeyondMovement.SharedKernel;

public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
```

Delete the auto-generated `Class1.cs` from both class library projects.

> **Why not just call `DateTime.UtcNow`?** Overdue to-dos, session reminders, and token expiry all depend on "now". If "now" is hard-coded, you cannot write a test that says "pretend it's tomorrow". Injecting a clock is a small habit that pays off from phase 7 onward.

---

## Step 8 — The database context

Create `src/BeyondMovement.Infrastructure/AppDbContext.cs`:

```csharp
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Infrastructure;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Picks up every IEntityTypeConfiguration in this assembly.
        // From Phase 1 we also scan each module assembly here.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
```

A `DbContext` is EF Core's representation of your database — one class, one connection, one set of tables.

---

## Step 9 — Configuration and secrets

Replace `src/BeyondMovement.Api/appsettings.Development.json` with:

```json
{
  "ConnectionStrings": {
    "Postgres": "Host=localhost;Port=5432;Database=mentalcoaching;Username=mc;Password=mc_dev_password"
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft.AspNetCore": "Warning",
        "Microsoft.EntityFrameworkCore.Database.Command": "Information"
      }
    }
  }
}
```

The dev password can live here because it only reaches your machine. **Real secrets never go in these files.** From phase 1 onward use:

```bash
dotnet user-secrets init --project src/BeyondMovement.Api
dotnet user-secrets set "SomeKey" "some value" --project src/BeyondMovement.Api
```

User secrets are stored outside the repo, so they cannot be committed by accident.

---

## Step 10 — Wire up `Program.cs`

Replace the whole of `src/BeyondMovement.Api/Program.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using BeyondMovement.Infrastructure;
using BeyondMovement.SharedKernel;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// --- logging -------------------------------------------------------------
builder.Host.UseSerilog((context, config) =>
    config.ReadFrom.Configuration(context.Configuration)
          .Enrich.FromLogContext()
          .WriteTo.Console());

// --- services ------------------------------------------------------------
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

builder.Services.AddSingleton<IClock, SystemClock>();

builder.Services.AddOpenApi();

builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>();

builder.Services.AddProblemDetails();

var app = builder.Build();

// --- pipeline ------------------------------------------------------------
app.UseSerilogRequestLogging();
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();   // UI at /scalar/v1
}

app.MapHealthChecks("/health");

app.MapGet("/api/v1/ping", () => Results.Ok(new { message = "pong" }));

app.Run();

public partial class Program;   // lets integration tests start the app
```

**What each block does:**

| Block | Purpose |
|---|---|
| `UseSerilog` | Structured logging instead of plain console text |
| `AddDbContext` | Registers the database connection so anything can ask for an `AppDbContext` |
| `AddSingleton<IClock, SystemClock>` | Dependency injection: "when someone asks for `IClock`, give them `SystemClock`" |
| `AddOpenApi` / `MapScalarApiReference` | Generates the API contract and a UI to test it |
| `AddHealthChecks().AddDbContextCheck` | `/health` fails if the database is unreachable — this is what your hosting platform probes |
| `AddProblemDetails` / `UseExceptionHandler` | Errors come back as RFC 7807 JSON, never a stack trace |

---

## Step 11 — Run it

```bash
dotnet run --project src/BeyondMovement.Api
```

The terminal prints something like `Now listening on: http://localhost:5xxx`. Open:

- `http://localhost:5xxx/api/v1/ping` — `{"message":"pong"}`
- `http://localhost:5xxx/health` — `Healthy` (this proves the database connection works)
- `http://localhost:5xxx/scalar/v1` — the API testing UI

If `/health` says `Unhealthy`, Docker isn't running or the connection string is wrong.

Stop the app with `Ctrl+C`.

---

## Step 12 — Basic CI

Create `.github/workflows/backend-ci.yml`:

```yaml
name: backend-ci
on:
  push:
    branches: [main]
  pull_request:

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - run: dotnet restore
      - run: dotnet build --no-restore --configuration Release
      - run: dotnet test --no-build --configuration Release
```

Commit everything:

```bash
git add .
git commit -m "Phase 0: solution, database, logging, health endpoint, CI"
```

### ✅ Phase 0 is done when

- [ ] `dotnet build` succeeds with zero warnings
- [ ] `docker compose up -d` starts Postgres
- [ ] `/health` returns Healthy
- [ ] `/api/v1/ping` responds
- [ ] Scalar UI loads
- [ ] CI passes on a pull request

---

# PHASE 1 — Authentication & Access

**Goal:** the Admin can log in with email and password, receive tokens, refresh them, and reset a password. Paused accounts are blocked. No athlete accounts yet — those arrive in phase 3.

---

## Step 1 — Create the Identity module

```bash
dotnet new classlib -o src/BeyondMovement.Modules.Identity
dotnet sln add src/BeyondMovement.Modules.Identity

dotnet add src/BeyondMovement.Modules.Identity reference src/BeyondMovement.SharedKernel
dotnet add src/BeyondMovement.Infrastructure reference src/BeyondMovement.Modules.Identity
dotnet add src/BeyondMovement.Api reference src/BeyondMovement.Modules.Identity

dotnet add src/BeyondMovement.Modules.Identity package Microsoft.EntityFrameworkCore
dotnet add src/BeyondMovement.Modules.Identity package Microsoft.AspNetCore.Identity
dotnet add src/BeyondMovement.Modules.Identity package FluentValidation
```

Folder layout inside the module:

```
Domain/          User.cs, RefreshToken.cs, PasswordResetToken.cs, enums
Persistence/     UserConfiguration.cs, RefreshTokenConfiguration.cs
Features/        Login/, RefreshToken/, Logout/, ForgotPassword/, ResetPassword/
Services/        ITokenService.cs, TokenService.cs
Contracts/       DTOs shared with the Api project
```

> **Why not full ASP.NET Core Identity?** The full stack brings its own user tables, its own registration and sign-in flows, and its own conventions. This product is invitation-only with two fixed roles and a custom paused state — you would spend more time bending Identity than writing the logic. We use only its `PasswordHasher<T>`, which is the vetted, well-tested part.

---

## Step 2 — Domain entities

`Domain/Enums.cs`:

```csharp
namespace BeyondMovement.Modules.Identity.Domain;

public enum UserRole { Admin, Athlete }
public enum UserStatus { Active, Paused, Deleted }
```

`Domain/User.cs`:

```csharp
namespace BeyondMovement.Modules.Identity.Domain;

public sealed class User
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public UserRole Role { get; private set; }
    public string Email { get; private set; } = null!;
    public string? PasswordHash { get; private set; }
    public string? GoogleSubjectId { get; private set; }
    public string FullName { get; private set; } = null!;
    public string? Phone { get; private set; }
    public UserStatus Status { get; private set; } = UserStatus.Active;
    public string TimeZone { get; private set; } = "UTC";
    public string? UiPreferences { get; private set; }          // jsonb — athlete-list sort order
    public string? NotificationPreferences { get; private set; } // jsonb
    public Guid CoachId { get; private set; }                    // always the single admin in v1
    public int FailedLoginAttempts { get; private set; }
    public DateTime? LockedOutUntilUtc { get; private set; }
    public DateTime? LastLoginAtUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    private User() { }   // EF Core needs this

    public static User CreateAdmin(string email, string fullName, string passwordHash, DateTime nowUtc)
    {
        var user = new User
        {
            Role = UserRole.Admin,
            Email = email.ToLowerInvariant(),
            FullName = fullName,
            PasswordHash = passwordHash,
            Status = UserStatus.Active,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc
        };
        user.CoachId = user.Id;
        return user;
    }

    public bool IsLockedOut(DateTime nowUtc) => LockedOutUntilUtc is not null && LockedOutUntilUtc > nowUtc;

    public void RecordFailedLogin(DateTime nowUtc)
    {
        FailedLoginAttempts++;
        if (FailedLoginAttempts >= 5)
            LockedOutUntilUtc = nowUtc.AddMinutes(15);
        UpdatedAtUtc = nowUtc;
    }

    public void RecordSuccessfulLogin(DateTime nowUtc)
    {
        FailedLoginAttempts = 0;
        LockedOutUntilUtc = null;
        LastLoginAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    public void Pause(DateTime nowUtc)      { Status = UserStatus.Paused; UpdatedAtUtc = nowUtc; }
    public void Reactivate(DateTime nowUtc) { Status = UserStatus.Active; UpdatedAtUtc = nowUtc; }
}
```

> Note the `private set` on every property and the behaviour methods. Outside code cannot put a `User` into an invalid state — it has to go through a method that keeps the rules. This is the pattern to follow for `Package` and `Session` in phases 4 and 6, where it matters far more.

`Domain/RefreshToken.cs`:

```csharp
namespace BeyondMovement.Modules.Identity.Domain;

public sealed class RefreshToken
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; } = null!;   // never store the raw token
    public Guid FamilyId { get; private set; }               // for reuse detection
    public string? DeviceId { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? UsedAtUtc { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    private RefreshToken() { }

    public static RefreshToken Issue(Guid userId, string tokenHash, Guid familyId,
                                     string? deviceId, DateTime nowUtc, int lifetimeDays = 30) => new()
    {
        UserId = userId,
        TokenHash = tokenHash,
        FamilyId = familyId,
        DeviceId = deviceId,
        ExpiresAtUtc = nowUtc.AddDays(lifetimeDays),
        CreatedAtUtc = nowUtc
    };

    public bool IsActive(DateTime nowUtc) =>
        RevokedAtUtc is null && UsedAtUtc is null && ExpiresAtUtc > nowUtc;

    public void MarkUsed(DateTime nowUtc)    => UsedAtUtc = nowUtc;
    public void Revoke(DateTime nowUtc)      => RevokedAtUtc = nowUtc;
}
```

Also create `PasswordResetToken` with `Id`, `UserId`, `TokenHash`, `ExpiresAtUtc` (1 hour), `UsedAtUtc`, `CreatedAtUtc`.

---

## Step 3 — EF configurations

`Persistence/UserConfiguration.cs`:

```csharp
using BeyondMovement.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BeyondMovement.Modules.Identity.Persistence;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("Users");
        b.HasKey(x => x.Id);

        b.Property(x => x.Email).IsRequired().HasMaxLength(256);
        b.HasIndex(x => x.Email).IsUnique();

        b.Property(x => x.GoogleSubjectId).HasMaxLength(128);
        b.HasIndex(x => x.GoogleSubjectId).IsUnique().HasFilter("\"GoogleSubjectId\" IS NOT NULL");

        b.Property(x => x.FullName).IsRequired().HasMaxLength(200);
        b.Property(x => x.Phone).HasMaxLength(40);
        b.Property(x => x.TimeZone).IsRequired().HasMaxLength(64);

        // enums as strings — readable in the database, immune to reordering
        b.Property(x => x.Role).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        b.Property(x => x.UiPreferences).HasColumnType("jsonb");
        b.Property(x => x.NotificationPreferences).HasColumnType("jsonb");

        b.HasIndex(x => new { x.CoachId, x.Role, x.Status });
    }
}
```

Do the same for `RefreshToken` (unique index on `TokenHash`, index on `UserId`) and `PasswordResetToken`.

Then tell `AppDbContext` to scan the module. In `OnModelCreating`, add:

```csharp
modelBuilder.ApplyConfigurationsFromAssembly(
    typeof(BeyondMovement.Modules.Identity.Domain.User).Assembly);
```

---

## Step 4 — Your first migration

```bash
dotnet ef migrations add InitialIdentity -p src/BeyondMovement.Infrastructure -s src/BeyondMovement.Api
```

**Open the generated file** in `src/BeyondMovement.Infrastructure/Migrations/`. Read it. It's C# describing the SQL that will run. Getting used to reading these now will save you badly later, when a migration threatens to drop a column.

Apply it:

```bash
dotnet ef database update -p src/BeyondMovement.Infrastructure -s src/BeyondMovement.Api
```

Refresh DBeaver — `Users`, `RefreshTokens`, and `PasswordResetTokens` now exist.

> `-p` is the project holding the migrations. `-s` is the startup project that knows the connection string. You'll type this a lot.

---

## Step 5 — JWT configuration

```bash
dotnet add src/BeyondMovement.Api package Microsoft.AspNetCore.Authentication.JwtBearer
dotnet add src/BeyondMovement.Modules.Identity package System.IdentityModel.Tokens.Jwt
```

Generate a signing key and store it in user secrets — **never in a file**:

```bash
dotnet user-secrets init --project src/BeyondMovement.Api
dotnet user-secrets set "Jwt:SigningKey" "REPLACE_WITH_A_64_CHARACTER_RANDOM_STRING" --project src/BeyondMovement.Api
```

Generate one with PowerShell:

```powershell
[Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Max 256 }))
```

Add non-secret settings to `appsettings.json`:

```json
"Jwt": {
  "Issuer": "beyond-movement",
  "Audience": "beyond-movement-app",
  "AccessTokenMinutes": 15,
  "RefreshTokenDays": 30
}
```

---

## Step 6 — Token service

`Services/ITokenService.cs` — the interface the handlers depend on:

```csharp
public interface ITokenService
{
    string CreateAccessToken(User user);
    (string raw, string hash) CreateRefreshToken();
    string Hash(string raw);
}
```

Implementation notes rather than full code, so you write it and understand it:

- **Access token:** a signed JWT containing claims `sub` (user id), `role`, `coachId`, `jti`, `exp`. Built with `JwtSecurityTokenHandler` and `SymmetricSecurityKey`.
- **Refresh token:** 32 random bytes from `RandomNumberGenerator.GetBytes(32)`, Base64-encoded. Return the raw value to the caller, store **only the SHA-256 hash**.
- **Hash:** `SHA256.HashData(Encoding.UTF8.GetBytes(raw))`, hex or Base64 encoded.

> Refresh tokens are hashed for the same reason passwords are: if the database leaks, the tokens in it must be useless.

---

## Step 7 — Login endpoint

Flow for `POST /api/v1/auth/login`:

1. Validate the request shape with FluentValidation (email format, password not empty).
2. Look up the user by lower-cased email.
3. If the user is missing **or** the password is wrong — return the **same** `401` with the same message. Never reveal which. Different messages let an attacker enumerate your users.
4. If `IsLockedOut(now)` — `423 Locked`.
5. Verify with `PasswordHasher<User>.VerifyHashedPassword`.
6. On failure — `RecordFailedLogin(now)`, save, return `401`.
7. If `Status == Paused` — `403` with `errorCode: ACCOUNT_PAUSED`.
8. On success — `RecordSuccessfulLogin(now)`, issue an access token and a refresh token, store the refresh token's hash with a new `FamilyId`, return both plus the user's role.

Response shape (this goes in the contract):

```json
{
  "accessToken": "...",
  "refreshToken": "...",
  "expiresInSeconds": 900,
  "user": { "id": "...", "role": "Admin", "fullName": "...", "email": "..." }
}
```

---

## Step 8 — Refresh, with reuse detection

`POST /api/v1/auth/refresh`:

1. Hash the incoming token, look it up.
2. Not found or expired — `401`.
3. **Already used** — someone stole it. Revoke every token in that `FamilyId` and return `401`. This is the important branch — don't skip it.
4. Valid — mark used, issue a new access + refresh pair with the **same** `FamilyId`.

`POST /api/v1/auth/logout` revokes the presented refresh token.

---

## Step 9 — Authentication and authorization in `Program.cs`

```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:SigningKey"]!)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly",   p => p.RequireRole(nameof(UserRole.Admin)));
    options.AddPolicy("AthleteOnly", p => p.RequireRole(nameof(UserRole.Athlete)));

    // deny by default — an endpoint must opt out with .AllowAnonymous()
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});
```

And in the pipeline, **in this order**:

```csharp
app.UseAuthentication();
app.UseAuthorization();
```

Mark the auth endpoints `.AllowAnonymous()`, plus `/health` and `/api/v1/ping`.

---

## Step 10 — Paused-account middleware

An access token stays valid for 15 minutes after a pause. Close that gap: after authentication, check the user's current status on every authenticated request.

```csharp
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true)
    {
        var userId = /* read "sub" claim */;
        var db = context.RequestServices.GetRequiredService<AppDbContext>();
        var status = await db.Set<User>()
            .Where(u => u.Id == userId)
            .Select(u => u.Status)
            .FirstOrDefaultAsync();

        if (status != UserStatus.Active)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { errorCode = "ACCOUNT_PAUSED" });
            return;
        }
    }
    await next();
});
```

Place it **after** `UseAuthorization()`. In phase 9 this lookup moves behind a 60-second Redis cache; a database hit per request is fine for now.

---

## Step 11 — Seed the Admin

There is no registration endpoint, so without this you cannot log in at all.

Write a startup seeder that runs in Development only: if no user with role `Admin` exists, create one from configuration (`Seed:AdminEmail`, and a password from user secrets), hashed with `PasswordHasher<User>`.

Verify the row appears in DBeaver, and that `PasswordHash` is a long opaque string — never the password itself.

---

## Step 12 — Password reset

- `POST /auth/forgot-password` — **always** returns `200`, whether or not the email exists. Creates a single-use token, stores its hash with a 1-hour expiry, and emails a link. Email delivery can be a logged stub until phase 3 — write a real `IEmailSender` interface now with a console implementation.
- `POST /auth/reset-password` — validate the token, set the new hash, **revoke every refresh token for that user**, mark the reset token used, write an audit entry.

---

## Step 13 — Test it

Create `requests.http` at the repo root and use the REST Client extension:

```http
@host = http://localhost:5xxx

### login
POST {{host}}/api/v1/auth/login
Content-Type: application/json

{ "email": "admin@beyondmovement.com", "password": "your-seed-password" }

### refresh
POST {{host}}/api/v1/auth/refresh
Content-Type: application/json

{ "refreshToken": "paste-from-login" }
```

Check each of these by hand:

- [ ] Correct credentials return two tokens
- [ ] Wrong password returns `401` with the **same** message as an unknown email
- [ ] Five failures lock the account for 15 minutes
- [ ] A protected endpoint returns `401` without a token, `200` with one
- [ ] Refreshing returns a new pair
- [ ] Reusing an old refresh token returns `401` **and kills the whole family**
- [ ] Pausing the admin in the database (set `Status = 'Paused'`) causes `403 ACCOUNT_PAUSED` on the next request

Then write automated tests for at least: password verification, lockout after five attempts, and refresh-token reuse detection.

---

## Step 14 — Publish the contract

```bash
dotnet run --project src/BeyondMovement.Api
# fetch the generated document from /openapi/v1.json and save it
```

Convert to YAML, save as `contract/openapi.yaml`, and note the additions in `contract/CHANGELOG.md`. The Flutter developer generates their API client from this file — it is the interface between your two repositories.

```bash
git add .
git commit -m "Phase 1: identity module, JWT auth, refresh rotation, paused checks, password reset"
```

### ✅ Phase 1 is done when

- [ ] Admin logs in and receives tokens
- [ ] Refresh rotation works and detects reuse
- [ ] Paused accounts are blocked within one request
- [ ] Password reset works end to end
- [ ] Protected endpoints reject unauthenticated calls by default
- [ ] `contract/openapi.yaml` is committed and the Flutter developer has been told
- [ ] Tests pass in CI

---

## Things that will trip you up

| Symptom | Cause |
|---|---|
| `dotnet ef` not found | Restart the terminal after `dotnet tool install` |
| "A connection could not be established" | Docker Desktop isn't running |
| Migration created but tables missing | You ran `migrations add` but not `database update` |
| `401` on every endpoint including login | Missing `.AllowAnonymous()` — the fallback policy denies everything |
| `IDX10720` / signing key error | The signing key is shorter than 32 bytes |
| Nullable warnings as errors | Intentional. Fix the warning; don't disable the setting. |
| Tokens work locally, fail in CI | User secrets don't exist in CI — configure environment variables |
| Changed an entity, nothing happened | EF changes need a new migration; the database doesn't follow your code automatically |

---

## When to ask, not decide

Stop and ask the team or client if you hit any of these in phases 0–1:

- Anything touching the open decisions in `CLAUDE.md` section 9
- A need to add a NuGet package not listed here
- A situation where the Product Specification and the UI/UX document disagree
- Any temptation to weaken an authorization check to make something work
