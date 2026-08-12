# CLAUDE.md — Beyond Movement Backend

Context file for Claude Code. Read this before doing anything in this repository.

---

## 1. What this repository is

The **backend API** for Beyond Movement, a mental coaching platform for one coach (Admin) and their invited athletes. ASP.NET Core modular monolith, PostgreSQL, deployed as a single service.

The Flutter mobile app lives in a **separate repository** (`mental-coaching-mobile`). This repo never contains Dart code. The two repos are connected only by the OpenAPI contract in `contract/openapi.yaml`.

**The developer working here is new to .NET.** Explain the *why* behind .NET-specific choices when they are not obvious. Prefer clear, conventional code over clever code.

---

## 2. Golden rules

1. **The Product Specification is the source of truth.** Never invent a feature that is not in it. If something seems missing, say so and stop — do not decide.
2. **Never silently resolve an open decision.** The items in section 9 are unresolved with the client. If a task requires one, flag it and ask.
3. **Never write mobile code, screens, or UI logic here.** This repo builds capabilities and APIs, not screens.
4. **Do not create a repo or service per module.** One solution, one deployable, modules as projects inside it.
5. **A contract change is a breaking change.** If a change alters a request or response shape, update `contract/openapi.yaml` and note it in `contract/CHANGELOG.md`.
6. **Do not add a NuGet package, a new external service, or a new architectural pattern without asking first.**
7. **Work phase by phase** (section 8). Do not build ahead into a later phase.

---

## 3. Stack

| Concern | Choice | Notes |
|---|---|---|
| Runtime | **.NET 10 (LTS)** | Supported to Nov 2028. .NET 8/9 reach EOL Nov 2026 — do not target them. |
| Language | C# 14 | Nullable reference types **enabled** everywhere |
| API | ASP.NET Core Minimal APIs, `/api/v1` | Endpoint groups per module |
| Real-time | SignalR | Chat only, phase 13 |
| Database | PostgreSQL 16 | Local via Docker Compose |
| ORM | EF Core 10 (code-first migrations) | Dapper for reporting aggregates only, phase 9+ |
| Cache | Redis | Not needed before phase 9 |
| Background jobs | Hangfire (Postgres storage) | Introduced phase 5 |
| Auth | Custom JWT + rotating refresh tokens | Uses `PasswordHasher<T>` from `Microsoft.AspNetCore.Identity`, **not** the full Identity stack — see section 6 |
| Validation | FluentValidation | At the endpoint, before the handler |
| Logging | Serilog, structured JSON | Correlation ID on every request |
| Storage | S3-compatible (MinIO locally) | Phase 13 |
| Tests | xUnit + Testcontainers | Integration tests use a real Postgres container |

---

## 4. Solution structure

```
mental-coaching-backend/
├── CLAUDE.md
├── docs/                              # the three source documents — read them
│   ├── product-specification.pdf
│   ├── software-architecture.md
│   ├── ui-ux-design-decisions.pdf
│   └── development-roadmap.pdf
├── contract/
│   ├── openapi.yaml                   # generated, committed
│   └── CHANGELOG.md
├── docker-compose.yml                 # postgres + redis + minio for local dev
├── MentalCoaching.sln
├── src/
│   ├── MentalCoaching.Api/            # host, endpoints, middleware, DI composition
│   ├── MentalCoaching.SharedKernel/   # Result, Error, IClock, IDomainEvent, base types
│   ├── MentalCoaching.Infrastructure/ # AppDbContext, migrations, external clients
│   └── MentalCoaching.Modules.*/      # one project per module, added as phases need them
└── tests/
    ├── MentalCoaching.UnitTests/
    ├── MentalCoaching.IntegrationTests/
    └── MentalCoaching.ArchitectureTests/
```

### Dependency direction — important

```
Api  ──────────────► Modules ──────► SharedKernel
 │                      ▲
 └──► Infrastructure ───┘
```

- **Modules depend on SharedKernel only.** They contain entities, business rules, EF entity configurations, and use-case handlers. They must not reference `Infrastructure` or each other.
- **Infrastructure references the modules** so `AppDbContext` can pick up their EF configurations. Never the reverse.
- **Api references everything** and wires it together. It is the only place that knows the full graph.
- Cross-module communication uses **in-process domain events** (MediatR notifications), never a direct type reference.

> The architecture document's diagram shows `Modules → Infrastructure`. The direction above is the corrected one and is what this repo uses. Modules depend on abstractions; infrastructure implements them.

---

## 5. Modules

Create each project only when its phase arrives.

| Project | Phase | Owns |
|---|---|---|
| `Modules.Identity` | 1, 3 | Users, roles, status, JWT, refresh tokens, invitations, password reset |
| `Modules.Athletes` | 2 | Athlete profiles, list/search/filter, pause, delete/anonymize, whiteboard links |
| `Modules.Packages` | 4 | Packages, balance, history, threshold events |
| `Modules.Scheduling` | 5, 6 | Sessions, Calendly projection, webhooks, reconciliation, attendance, session notes |
| `Modules.ToDos` | 7 | To-dos, overdue job |
| `Modules.Finance` | 8 | Payments, expenses |
| `Modules.Reporting` | 9, 11 | Dashboard aggregates, reports — **read-only, may query across module tables** |
| `Modules.Notifications` | 10 | Notification records, push, email, reminders, dedup, retry |
| `Modules.Chat` | 13 | Conversations, messages, unread counts, SignalR |
| `Modules.Files` | 13 | Pre-signed upload/download, two-phase commit |

`Notifications` is a pure consumer — nothing depends on it, so its failure must never block a business operation.

---

## 6. Non-negotiable domain invariants

These are the rules the business cannot survive breaking. Each is enforced **at the database level as well as in code**, because application code eventually contains a bug.

| Rule | Enforcement |
|---|---|
| **BR-03** — one active package per athlete | Partial unique index on `Packages(AthleteProfileId) WHERE Status = 'Active'` **plus** a handler check |
| **BR-04** — booking never deducts a session | No deduction path exists outside `MarkAttended` |
| **BR-05** — a session is deducted exactly once | `SELECT … FOR UPDATE` on session and package, `RowVersion` optimistic concurrency, `Sessions.ConsumedSessionCount IN (0,1)` check constraint, `Idempotency-Key` header |
| **BR-06** — cancelled sessions never consume | Cancellation path never touches balance |
| **BR-01** — invitation-only | No public registration endpoint exists. Google sign-in authenticates; it never registers. |
| **BR-10** — paused athletes cannot log in | Middleware checks user status on every authenticated request; pausing revokes all refresh tokens |
| Balance integrity | `UsedSessions >= 0 AND UsedSessions <= TotalSessions` check constraint; `Remaining` is computed, never stored |
| No cross-athlete data access | Three layers: policy → ownership check → EF Core global query filter. Foreign resources return **404, not 403** |

**Active/Inactive is not the same as Paused.** *Inactive* (athlete list filter) means "has no active package" and is **derived**, never stored. *Paused* is account access and lives in `Users.Status`. Do not merge them.

---

## 7. Conventions

**Code**
- Nullable reference types on. No `!` null-forgiving operator without a comment explaining why.
- Handlers return `Result<T>` for expected failures. Exceptions are for genuinely exceptional conditions only.
- Never use `DateTime.UtcNow` directly — inject `IClock`. Overdue and reminder logic is untestable otherwise.
- All timestamps stored in **UTC**. Columns end in `Utc` where ambiguous.
- Enums stored as **strings** in the database, not integers.
- Never serialise EF entities. Every endpoint has explicit request/response DTOs.
- `AsNoTracking()` on all reads.

**API**
- RFC 7807 Problem Details for every error, plus `errorCode` and `correlationId`.
- Stable error codes: `INVITATION_EXPIRED`, `ACCOUNT_PAUSED`, `ACTIVE_PACKAGE_EXISTS`, `SESSION_ALREADY_ATTENDED`, `NO_SESSIONS_REMAINING`, `CALENDLY_UNAVAILABLE`. The mobile app switches on these — do not rename one without a contract change.
- Never leak stack traces or provider messages to clients.
- Deny by default: every endpoint requires authentication unless explicitly marked anonymous.
- Cursor pagination for streams (messages, sessions, notifications). Hard page cap of 100.
- Role notation used in the architecture doc: **A** = Admin only, **T** = Athlete only, **B** = both (ownership-scoped), **anon** = no auth.

**Database**
- One migration per logical change, named descriptively (`AddPackageThresholdFields`, not `Migration7`).
- Migrations must be **backward compatible** — additive first, destructive only after the old version is retired.
- Never edit a migration that has been applied to staging or production.

**Logging**
- Never log passwords, tokens, chat message content, or full email addresses.
- Business events at `Information`: attendance marked, package created, invitation redeemed.
- Anything with legal or financial weight also goes to the `AuditLogs` table — the app role has INSERT only, no UPDATE or DELETE.

---

## 8. Roadmap

Build in this order. A phase is done only when its backend work is integrated with the available Flutter screens and tested end to end.

| # | Phase | Backend focus |
|---|---|---|
| 0 | Project Setup | Solution, DB, EF, config, logging, health, CI |
| 1 | Authentication & Access | Identity module, JWT + refresh, roles, paused checks, password reset |
| 2 | Athlete Management | Athlete profile, list, search, Active/Inactive filter, edit, pause |
| 3 | Invitations | Create/validate/redeem, single-use, expiry, account creation transaction |
| 4 | Packages | Model, one-active rule, history, balance, close |
| 5 | Scheduling & Calendly | Sessions, webhooks, projection, reconciliation |
| 6 | Attendance & Notes | Exactly-once deduction, statuses, session notes |
| 7 | To-Dos | CRUD, athlete-only completion, overdue job |
| 8 | Finance | Payments, manual confirmation, expenses |
| 9 | Admin Dashboard | Aggregate endpoint, period filters, alerts, last-session-note |
| 10 | Notifications | Device tokens, push, email, deep links, dedup, retry |
| 11 | Reports | Period reports, hours, financial, outstanding |
| 12 | Athlete Experience | Athlete-scoped endpoints, ownership verification |
| 13 | Chat & Files | Conversations, SignalR, voice notes, uploads |
| 14 | Hardening & Release | Security tests, concurrency tests, monitoring, deployment |

**Current phase: 1** ← update this line as you progress.
Phase 0 and the phase 1 backend are complete. Phase 1 is not *done* by the definition
below until the available Flutter screens are connected to the real API.

### Definition of done for a phase
- Migration and model applied
- Endpoints implemented with authorization and business rules
- Critical tests passing
- Available Flutter screens connected to the real API
- Loading, empty, validation, and error states work
- Tested end to end in the dev environment
- Any unresolved product or UI decision **documented, not guessed**

---

## 9. Open decisions — do not resolve these yourself

| ID | Question | Blocks |
|---|---|---|
| A-01 | Does the Calendly plan include webhooks? | Phase 5 |
| C-02 | What does the Admin Home "New Session" quick action do — create an Observation, book manually, or is it removed? | Phase 6 |
| C-01 | Payment statuses: Unpaid/PartiallyPaid/Paid (spec) or Paid/Pending (UI doc)? | Phases 4, 8 |
| A-02 | Where does session duration come from for the hours report? | Phase 5 |
| A-03 | How are Observation sessions created, and when do they deduct? | Phase 6 |
| A-04 | Does a No-show deduct a session? Current default: no. | Phase 6 |
| A-07 | Athlete deletion: hard delete or anonymize? | Phase 2 |

Four UI screens are also **not yet designed**: Chat, Packages, Payments, Settings. Backend data models and APIs for these can proceed; do not guess at screen behaviour.

---

## 10. Commands

```bash
# run the database and friends
docker compose up -d

# build and test
dotnet build
dotnet test

# run the API (from repo root)
dotnet run --project src/MentalCoaching.Api

# migrations
dotnet ef migrations add <Name> -p src/MentalCoaching.Infrastructure -s src/MentalCoaching.Api
dotnet ef database update            -p src/MentalCoaching.Infrastructure -s src/MentalCoaching.Api
dotnet ef migrations remove          -p src/MentalCoaching.Infrastructure -s src/MentalCoaching.Api

# local secrets (never commit secrets)
dotnet user-secrets set "Jwt:SigningKey" "<value>" --project src/MentalCoaching.Api
```

---

## 11. Reference documents

| Question | Document |
|---|---|
| What must the product do? | `docs/product-specification.pdf` |
| How is the system built? | `docs/software-architecture.md` |
| How should a screen behave? | `docs/ui-ux-design-decisions.pdf` |
| What do we build next? | `docs/development-roadmap.pdf` |

The architecture document is the implementation reference: section 5 (backend), 6 (database), 7 (auth), 8 (Calendly), 12 (security), 14 (API endpoints).
