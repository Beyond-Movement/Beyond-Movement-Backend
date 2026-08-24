# CLAUDE.md — Beyond Movement Backend

Context file for Claude Code. Read this before doing anything in this repository.

**Last updated:** phases 0–6 backend complete. See section 8 for exactly where we are.

---

## 1. What this repository is

The **backend API** for Beyond Movement, a mental coaching platform for one coach (Admin) and their invited athletes. ASP.NET Core modular monolith, PostgreSQL, deployed as a single service.

The Flutter mobile app lives in a **separate repository**. This repo never contains Dart code. The two repos are connected only by the OpenAPI contract in `contract/openapi.yaml` — note **`contract/`, singular**, not `contracts/`.

**Two developers work here, and at least one is new to .NET.** Explain the *why* behind .NET-specific choices when they are not obvious. Prefer clear, conventional code over clever code.

---

## 2. Golden rules

1. **The Product Specification is the source of truth** — with the exception recorded in section 8.1, where the client has deliberately superseded it and the documents have not caught up. Never invent a feature. If something seems missing, say so and stop.
2. **Never silently resolve an open decision.** Section 9 lists what is unresolved. If a task needs one, flag it and ask.
3. **Never write mobile code, screens, or UI logic here.**
4. **One solution, one deployable.** Modules are projects inside it, not repos or services.
5. **A contract change is a breaking change.** Update `contract/openapi.yaml` and note it in `contract/CHANGELOG.md`.
6. **Do not add a NuGet package, external service, or architectural pattern without asking.**
7. **Work phase by phase.** Do not build ahead.
8. **Do not commit unless asked.** The repository owner stages and commits. Leave work in the working tree and say what changed.

---

## 3. Stack

| Concern | Choice | Notes |
|---|---|---|
| Runtime | **.NET 10** | Do not target 8 or 9 |
| Language | C# 14, nullable enabled, `TreatWarningsAsErrors` | A warning fails the build |
| API | ASP.NET Core Minimal APIs, `/api/v1` | Endpoint groups per module |
| Database | PostgreSQL 18 (Docker) | Host port **5433** locally — 5432 is often taken by a native install |
| ORM | EF Core 10, code-first migrations | |
| Auth | Custom JWT + rotating refresh tokens | `PasswordHasher<T>` from ASP.NET Identity for hashing only, **not** the Identity stack |
| Validation | FluentValidation | At the endpoint, before the handler |
| Logging | Serilog, structured | Correlation id on every request |
| Mail | Postmark → SMTP → console, in that order | See section 7.3 |
| Tests | xUnit + Testcontainers | Integration tests need Docker running |
| OpenAPI | .NET 10 built-in + Scalar UI | Document/schema transformers in `Api/OpenApi/` |

---

## 4. Solution structure

Real names — the projects are **`BeyondMovement.*`** and the solution file is **`BeyondMovement.slnx`**.

```
Beyond-Movement-Backend/
├── skills/                                   # the four source documents live HERE, not docs/
│   ├── CLAUDE (1).md                         # this file
│   ├── product-specification (1).md
│   ├── software-architecture (1).md
│   ├── ui-ux-design-decisions (1).md
│   └── development-roadmap (1).md
├── contract/
│   ├── openapi.yaml                          # generated, committed
│   └── CHANGELOG.md                          # the working source of truth for API behaviour
├── docker-compose.yml                        # postgres, redis, pgadmin, mailpit
├── BeyondMovement.slnx
├── src/
│   ├── BeyondMovement.Api/                   # host, endpoints, middleware, DI, cross-module read models
│   ├── BeyondMovement.SharedKernel/          # Result, Error, IClock, PagedResult, Gender
│   ├── BeyondMovement.Infrastructure/        # AppDbContext, migrations, email, Google
│   ├── BeyondMovement.Modules.Identity/
│   ├── BeyondMovement.Modules.Athletes/
│   └── BeyondMovement.Modules.Packages/
└── tests/
    ├── BeyondMovement.UnitTests/
    └── BeyondMovement.IntegrationTests/
```

### Dependency direction — important

```
Api  ──────────────► Modules ──────► SharedKernel
 │                      ▲
 └──► Infrastructure ───┘
```

- **Modules depend on SharedKernel only.** Never on `Infrastructure`, never on each other.
- **Infrastructure references the modules** so `AppDbContext` picks up their EF configurations.
- **Api references everything** and is the only project that knows the full graph.

**When a feature needs two modules at once, it goes in the Api.** That is the established pattern, not a workaround:

| Read model | Spans | Lives in |
|---|---|---|
| `AthleteDirectory` | Identity (`Users`) + Athletes (`AthleteProfiles`) | `Api/Athletes/` |
| `CatalogueReader` | Athletes (loyalty) + Packages (options, overrides) | `Api/Packages/` |

These are **read-only**. A cross-module *write* is orchestrated in an endpoint inside one transaction — see `RegistrationEndpoints`, which creates a user and an athlete profile together.

---

## 5. Modules

| Project | Owns | State |
|---|---|---|
| `Modules.Identity` | Users, roles, status, JWT, refresh tokens, invitations, password reset, Google sign-in | **Built** |
| `Modules.Athletes` | Athlete profiles, complete profile, loyalty flag | **Built** |
| `Modules.Packages` | Package options, features, per-athlete price overrides, pricing rule, purchased packages | **Built** |
| `Modules.Scheduling` | Sessions, Calendly, attendance, session notes | **Built** |
| `Modules.ToDos` | To-dos, overdue job | Phase 7 |
| `Modules.Finance` | Payments, expenses | Phase 8 |
| `Modules.Reporting` | Dashboard aggregates — read-only, may query across module tables | Phase 9, 11 |
| `Modules.Notifications` | Push, email, reminders | Phase 10 |
| `Modules.Chat`, `Modules.Files` | SignalR, uploads | Phase 13 |

Create a project only when its phase arrives.

---

## 6. Non-negotiable domain invariants

Enforced **at the database level as well as in code**, because application code eventually contains a bug.

| Rule | Enforcement |
|---|---|
| **BR-01** — invitation-only | No public registration endpoint. Google sign-in authenticates; it never creates an account. |
| **BR-10** — paused athletes cannot log in | Status checked on every authenticated request; pausing revokes all refresh tokens |
| Profile completion implies a name | `User.MarkProfileCompleted` throws without one — see section 7.1 |
| One price override per athlete/option | Unique index on `(AthleteUserId, PackageOptionId)` |
| Package names are unique | Unique index on `(CoachId, lower(Name))`, **archived options included** |
| Feature order is unique per option | Unique index on `(PackageOptionId, Position)` |
| No cross-athlete data access | Policy → ownership check → scoped query. Foreign resources return **404, not 403** |
| **BR-03** — one active package per athlete | Partial unique index on `PurchasedPackages(AthleteProfileId) WHERE Status='Active'` |
| **BR-04, BR-06** — only a session that happened consumes one | Check constraint: `ConsumedSessionCount = 0 OR Status IN ('Attended','NoShow')` |
| Exactly-once deduction (BR-05) | Three layers: `Session.Resolve` refuses a non-Scheduled session; `xmin` row versions on both rows; check constraint `UsedSessions <= TotalSessions` |

**Active/Inactive is not the same as Paused.** *Inactive* means "has no active package" and is derived, never stored. *Paused* is account access and lives in `Users.Status`. Do not merge them.

---

## 7. Flows an agent needs to know before touching this code

These are the parts where the obvious change is the wrong one. Each cost real debugging time.

### 7.1 Invitation → account → profile

```
Admin POST /invitations {email}
   → backend generates a TEN-character code, emails it, stores only its hash
Athlete GET /invitations/validate?code=MRPZB-AXZYY
   → does NOT consume the invitation; returns the invited email + a 30-minute registrationToken
Athlete POST /auth/register {registrationToken, password | googleIdToken}
   → creates the account, redeems the invitation, returns tokens
   → profileCompleted = false, fullName = null
Athlete POST /athletes/me/profile {fullName, dateOfBirth, gender, sport}
   → profileCompleted = true
```

Things that are easy to get wrong here:

- **Registration does not collect a name.** It establishes authentication only. Two places to set a name is two places for them to disagree. Google's display name is kept as a *prefill*.
- **`fullName` is nullable**, and the contract promises that `profileCompleted == true` implies it is non-null. That promise is kept by `User.MarkProfileCompleted` throwing, not by the endpoint remembering. Do not move the check into the endpoint.
- **Invitation codes are ten characters**, formatted `ABCDE-FGHJK`, from an alphabet with no `O`, `I`, `L`, `U`, `V`, `0` or `1`. `InvitationCode.Normalize` strips non-alphanumerics and upper-cases, so the dash and casing are optional on input.
- **An athlete who registered but never completed their profile still appears** in the coach's list with `fullName: null`. Search falls back to email so they stay findable. Do not "fix" this by hiding them.

### 7.2 Package pricing

```
effective price = custom override
               ?? (isLoyal ? default × 0.85 : default)
```

- **Money is an integer count of piastres**, never a decimal. 100 piastres to the EGP. Every field ends in `Minor`. A decimal in JSON becomes a Dart `double` on the client and loses precision once summed.
- **The loyalty discount rounds to the nearest tenth of a pound**, halves away from zero, and is clamped so it can never exceed the original price.
- **Default prices and custom overrides are never rounded.** Only the computed loyalty price is.
- **A custom override is not discounted again** for a loyal athlete. It is an agreed price, not a starting point.
- **A custom price of `0` is a real override.** `if (price)` would treat it as absent — do not write that.
- The rule lives in `PackagePricing` and **nowhere else**. The mobile app is contractually told not to reproduce it. If a price is wrong, it is a backend bug.

### 7.3 Email

Transport is chosen at startup: **Postmark → SMTP → console**. The startup line says plainly which one won and whether mail reaches real people.

- Locally, `appsettings.Development.json` points SMTP at **Mailpit** (`localhost:1025`, web UI at `localhost:8025`). Nothing leaves the machine.
- To send real mail from a dev machine, six user-secret keys must all be set — see the README section "Sending real email from a local machine, via Gmail". Setting some but not all silently falls back to Mailpit.
- **`Email:FromName` is empty on purpose.** A brand display name over an `@gmail.com` address is a phishing shape and Gmail files it as spam. This was measured, not guessed.
- **The logo needs a public HTTPS URL.** `localhost` resolves to nothing from Gmail's image proxy, and `data:` URIs are stripped.
- Templates live in one file, `EmailTemplates.cs`, and **every message has an HTML body and a plain-text body**. Change both, always.

### 7.4 Traps that have already bitten

- **Configuration read at startup is invisible to tests.** `WebApplicationFactory` adds configuration *after* the host is built, so anything captured during service registration ignores test overrides. Resolve per request from `context.RequestServices`. This caused two separate bugs — the JWT signing key and the rate limits.
- **`MapInboundClaims = false` everywhere.** Without it, .NET renames `sub` and `email` to long Microsoft URIs and every token lookup silently fails. It is needed on `JwtBearerOptions` *and* on any hand-rolled `JwtSecurityTokenHandler`.
- **EF maps computed collection properties as navigations.** A `public IReadOnlyList<T> Ordered => [.. _items.OrderBy(...)]` beside a mapped `_items` field produces a *second* foreign key. Use `b.Ignore(x => x.Ordered)`.
- **Replacing a child collection violates a unique index.** EF inserts the new rows before deleting the old, so positions collide. Rewrite existing rows in place instead — see `PackageOption.Apply`.
- **A shared enum schema folds `null` into itself.** One nullable use makes `null` legal everywhere, including requests that require a value. `SchemaNormalizingTransformer` strips it.
- **Docker Desktop stops often on this machine.** Integration tests then fail with `DockerUnavailableException` — that is the environment, not the code. Restart Docker and `docker compose up -d postgres`.

---

## 8. Where we are

| # | Phase | Backend | Notes |
|---|---|---|---|
| 0 | Project Setup | ✅ Done | Solution, DB, EF, config, Serilog, health, Docker |
| 1 | Authentication & Access | ✅ Done | JWT + rotating refresh with family revocation, roles, lockout, paused checks |
| 1.1 | Contract hardening | ✅ Done | Google sign-in, change password, problem-details envelope |
| 2 | Athlete Management | ✅ Done | List, search, filter, sort, paging, pause, reactivate, sort preference |
| 3 | Invitations & onboarding | ✅ Done | Invite, validate, register, complete profile, `Gender` enum, `termsAccepted` removed |
| 3.1 | Password-reset rate limiting | ✅ Done | 3/hour/email and 10/hour/IP, enumeration-safe |
| 4 | **Package catalogue** | ✅ Done | Options, features, archive/restore, loyalty, per-athlete overrides, athlete catalogue |
| 5 | Scheduling & Calendly | ✅ Done | Calendly projection, webhooks, reconciliation, sessions |
| 6 | **Attendance & Notes** | ✅ Done | Mark attended/no-show, exactly-once deduction, observations, session notes, **purchased packages** |
| 7 | To-Dos | ▶ **Next** | |
| 8 | Finance | Not started | Payment status on a package is still unbuilt — see C-01 |
| 9–14 | Dashboard → Release | Not started | |

**A phase is not *done* until the Flutter screens are connected and tested end to end.** By that definition phases 1–4 are backend-complete and awaiting mobile integration.

### 8.1 The Phase 4 scope change — read this before touching packages

The client **split Phase 4 in two**, and the source documents have **not** been updated. They still describe the old model.

| | Built now (Phase 4) | Deferred to the purchase phase |
|---|---|---|
| What | A **catalogue** of reusable package options the coach sells | A **purchased package** an athlete owns |
| Includes | Name, sessions, default price, ordered features, archive/restore, loyalty, per-athlete overrides | Purchasing, InstaPay, pending purchases, payment confirmation, activation, remaining sessions, history |
| Invariants | Unique names, one override per athlete/option | **BR-03** one active package per athlete |

So `product-specification.md` §4.5, `software-architecture.md` §14.3 and `development-roadmap.md` Phase 4 describe work that is **real but not yet built**, under a phase number that now means something else.

**`contract/CHANGELOG.md` is the working source of truth for API behaviour.** It is regenerated and reviewed with every change; the four source documents are not. When they disagree, the changelog is what shipped.

---

## 9. Open decisions — do not resolve these yourself

| ID | Question | Blocks |
|---|---|---|
| A-01 | Does the Calendly plan include webhooks? | **Phase 5 — the next phase** |
| C-01 | Payment statuses: Unpaid/PartiallyPaid/Paid (spec) or Paid/Pending (UI doc)? | Purchase phase, 8 |
| C-02 | What does the Admin Home "New Session" quick action do? | Phase 6 |
| A-02 | Where does session duration come from for the hours report? | Phase 5 |
| — | A-03 Observation creation. **Decided:** Admin creates them manually via `POST /sessions/observations`; the >1h rule (BR-07) is evaluated on Mark as Attended | Closed |
| — | A-04 No-show deduction. **Decided:** one deployment-wide setting, `Features__NoShowDeducts`, default off | Closed |
| A-07 | Athlete deletion: hard delete or anonymize? | Phase 2 cleanup |
| — | Should `sport` become an enum? **Decided: no**, required free text for v1 | Closed |
| — | Admin athlete-edit endpoint | **Deferred** by the client, outside Phase 3 |
| — | Terms of Service / Privacy Policy | **Removed** by the client. Note both app stores require a privacy policy URL at submission. |

Four UI screens are still **not designed**: Chat, Packages, Payments, Settings. Backend models and APIs may proceed; do not guess screen behaviour.

---

## 10. Commands

```bash
docker compose up -d                      # postgres, redis, pgadmin, mailpit
dotnet build                              # zero warnings expected
dotnet test                               # needs Docker
dotnet run --project src/BeyondMovement.Api

# migrations
dotnet ef migrations add <Name> -p src/BeyondMovement.Infrastructure -s src/BeyondMovement.Api
dotnet ef database update       -p src/BeyondMovement.Infrastructure -s src/BeyondMovement.Api
dotnet ef migrations remove     -p src/BeyondMovement.Infrastructure -s src/BeyondMovement.Api

# regenerate the contract after any endpoint change: run the API, then
curl -s http://localhost:5229/openapi/v1.json -o contract/openapi.json   # convert to YAML

# local secrets — never commit these
dotnet user-secrets set "Jwt:SigningKey" "<64 random chars>" --project src/BeyondMovement.Api
dotnet user-secrets list --project src/BeyondMovement.Api
```

Each developer has **their own** user secrets and their own local database. The values do not need to match.

---

## 11. Reference documents

| Question | Document |
|---|---|
| What did the API actually ship? | **`contract/CHANGELOG.md`** — the most current record |
| What is the exact request/response shape? | `contract/openapi.yaml` |
| How do I set this up? | `README.md` |
| What must the product do? | `skills/product-specification (1).md` |
| How is the system built? | `skills/software-architecture (1).md` |
| How should a screen behave? | `skills/ui-ux-design-decisions (1).md` |
| What do we build next? | `skills/development-roadmap (1).md` |

The four `skills/` documents are the original brief and **lag behind the code** where the client has changed direction — Phase 4 above is the live example. Read them for intent; read the changelog for behaviour.
