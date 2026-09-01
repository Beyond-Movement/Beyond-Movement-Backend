# Beyond Movement — Development Roadmap & Team Work Guide

**BEYOND MOVEMENT**

Development Roadmap & Team Work Guide

Mental Coaching Platform • v1.0

<table>
<colgroup>
<col style="width: 100%" />
</colgroup>
<thead>
<tr class="header">
<th>PURPOSE<br />
Open this document → find the current phase → pick your role → work on the listed items. For business rules, use the Product Specification. For database, APIs, authentication, module structure, Calendly, security, and implementation details, use the Software Architecture document.</th>
</tr>
</thead>
<tbody>
</tbody>
</table>

# The two repositories

| **Repository**              | **Owner / Main Work** | **Contains**                                                     |
|-----------------------------|-----------------------|------------------------------------------------------------------|
| **mental-coaching-mobile**  | Flutter / Mobile      | Admin + Athlete app, screens, navigation, state, API integration |
| **mental-coaching-backend** | Backend               | ASP.NET Core API, PostgreSQL, modules, integrations, jobs, tests |

| Backend = one modular monolith in one repo. Do NOT create a separate repo for each module. Mobile = one Flutter app for both Admin and Athlete roles. |
|-------------------------------------------------------------------------------------------------------------------------------------------------------|

# How to use this roadmap

- Work phase by phase. Do not wait for every screen in the whole app to be designed.

- Admin-first where Admin UI/UX is already ready. Athlete work can follow as its UI/UX is finalized.

- Backend builds capabilities/APIs, not screens. Flutter builds screens and connects them to those APIs.

- A phase is done only when its backend + available Flutter screens are integrated and tested.

- If a feature has detailed technical behavior here only at a high level, the Software Architecture document is the implementation reference.

# Backend modules

**Identity & Invitations • Athletes • Packages • Scheduling •** Chat • To-Dos • Finance (Payments & Expenses) • Notifications • Reporting • Files

# Progress — where the backend actually is

Updated as phases complete. **Backend-complete** means the endpoints, rules, migrations and tests
are done and the contract is published; a phase is not *done* by this document's own definition
until the Flutter screens are connected and tested end to end.

| # | Phase | Backend | Mobile integration |
|---|---|---|---|
| 0 | Project Setup | ✅ Complete | n/a |
| 1 | Authentication & Access | ✅ Complete | done |
| 2 | Athlete Management | ✅ Complete | done |
| 3 | Invitation Codes & Account Creation | ✅ Complete | done |
| 4 | **Package catalogue** (revised scope — see below) | ✅ Complete | In progress |
| 5 | Scheduling & Calendly | ▶ **Start here** | — |
| 6 | Attendance & Session Notes | Not started | — |
| 7 | To-Dos | Not started | — |
| 8 | Finance | Not started | — |
| 9 | Admin Dashboard | Not started | — |
| 10 | Notifications | Not started | — |
| 11 | Reports & Analytics | Not started | — |
| 12 | Athlete Experience | Not started | — |
| 13 | Chat & Files | Not started | — |
| 14 | Hardening, QA & Release | Not started | — |

## What was built beyond the original phase text

Phases 1–3 grew during implementation. The additions, all published in `contract/CHANGELOG.md`:

- **Google sign-in** on both login and account creation — authenticates only, never registers (BR-01).
- **Change password** while signed in, and password reset by emailed deep link.
- **Rotating refresh tokens with family revocation** — reusing a rotated token kills the whole family.
- **`profileCompleted`** on every authentication response, so the app routes without a second call.
- **`fullName` is nullable** until Complete Profile, and guaranteed non-null once it is finished.
- **`Gender` enum** (`Female` / `Male`) replacing free text.
- **`termsAccepted` removed** — the client decided the app ships with no Terms or Privacy Policy.
- **Rate limiting** on invitation validation (10/hour/IP) and password reset (3/hour/email, 10/hour/IP).
- **Coach sort preference** persisted for the athlete list.

## Immediate next step

**Phase 5 — Scheduling & Calendly.** It is blocked on open decision **A-01**: *does the Calendly
plan include webhooks?* The answer changes the design between push and polling, so settle it
before writing code.

If A-01 cannot be answered yet, the next unblocked work is the **purchase phase** split out of
Phase 4 below, which needs no external answer.

# PHASE 0 — Project Setup ✅ COMPLETE

| **BACKEND**                                                                                                 | **FLUTTER / MOBILE**                                      |
|-------------------------------------------------------------------------------------------------------------|-----------------------------------------------------------|
| • Create backend repo and ASP.NET Core solution using the architecture folder/module structure.             | • Create Flutter repo/project.                            |
| • Create PostgreSQL database setup + EF Core migrations.                                                    | • Create app theme/design tokens and shared widgets.      |
| • Set up environments/configuration, secrets approach, logging, error handling foundation, health endpoint. | • Create Admin/Athlete app shells and routing foundation. |
| • Set up basic CI build/test pipeline.                                                                      | • Set up API client/environment configuration.            |

**CHECK:** Architecture: High-Level Architecture, Technology Stack, Mobile Architecture, Backend Architecture.

# PHASE 1 — Authentication & Access ✅ BACKEND COMPLETE

| **BACKEND**                                              | **FLUTTER / MOBILE**                         |
|----------------------------------------------------------|----------------------------------------------|
| • Identity module: Users, roles, status, refresh tokens. | • Login screen and auth state.               |
| • Admin email/password login.                            | • Forgot/reset password screens if ready.    |
| • JWT access + rotating refresh tokens.                  | • Role-aware navigation.                     |
| • Google sign-in backend verification/foundation.        | • Access Disabled state for paused athletes. |
| • Role authorization + paused-athlete access checks.     |                                              |
| • Password reset flow.                                   |                                              |

**CHECK:** Architecture: Authentication & Authorization. Product Spec: Authentication & Invitations.

# PHASE 2 — Athlete Management — Admin First ✅ BACKEND COMPLETE

| **BACKEND**                                                               | **FLUTTER / MOBILE**                      |
|---------------------------------------------------------------------------|-------------------------------------------|
| • Athlete profile model and persistence.                                  | • Admin Athlete List.                     |
| • List athletes.                                                          | • Search/filter/sort behavior from UI/UX. |
| • Search + Active/Inactive filter.                                        | • Athlete Profile.                        |
| • Get athlete details.                                                    | • Edit Athlete.                           |
| • Edit athlete details.                                                   | • Pause/reactivate actions.               |
| • Pause/reactivate access.                                                |                                           |
| • Soft-delete/anonymization path only when product decision is confirmed. |                                           |

**CHECK:** Architecture: Athletes module + Database Design. UI/UX: Athlete List and Athlete Profile.

# PHASE 3 — Invitation Codes & Athlete Account Creation ✅ BACKEND COMPLETE

| **BACKEND**                                                               | **FLUTTER / MOBILE**                                 |
|---------------------------------------------------------------------------|------------------------------------------------------|
| • Create an invitation code bound to the Admin-entered athlete email.      | • Athlete List (+) opens the Invite Athlete modal.             |
| • Email the code directly to that address; support resend and revoke.      | • Login links to a dedicated Enter Invitation Code screen.     |
| • Enforce expiry, single use, revocation, and rate-limited validation.     | • Handle invalid, expired, used, and revoked invitation states.|
| • Treat successful emailed-code validation as email verification.         | • Show the verified email as read-only on Create Account.      |
| • Issue a short-lived registration token without consuming the invitation.| • Support password or matching-email Google account creation.  |
| • Redeem invitation and create user/profile/conversation transactionally. | • Collect full name and athlete details on Complete Profile.   |

**CHECK:** Architecture: Invitation Flow + Identity module. Product Spec: BR-01 / BR-02.

Invitation links are a later enhancement over the same invitation record and validation flow. They are not required for the code-first Phase 3 increment.

# PHASE 4 — Package catalogue ✅ COMPLETE (revised scope)

> **The client split this phase in two.** Phase 4 is now the **catalogue** of package options the
> coach sells. Everything about a package an athlete has **bought** moved to the purchase phase
> below. The rest of this document, the Product Specification §4.5 and the Software Architecture
> §14.3 still describe the combined, older model — they have not been rewritten.

| **BACKEND — done** | **FLUTTER / MOBILE** |
|---|---|
| • `PackageOption`: name, sessions, default price, ordered features. | • Package Options list, Add and Edit screens. |
| • Archive and restore. Options are never deleted. | • Archive / restore with swipe, archived list. |
| • Case-insensitive unique names across active **and** archived. | • Duplicate-name error handling. |
| • Optimistic concurrency via `version`. | • Reload-on-conflict handling. |
| • Athlete-level loyalty flag, 15% off every default price. | • Loyalty badge on the athlete list and profile. |
| • Per-athlete, per-package price overrides. | • Custom price entry, when designed. |
| • Server-owned precedence: custom → loyalty → default. | • Display only. **Never reproduce the rule.** |
| • Athlete catalogue returning final prices only. | • Athlete package list, when the UI is ready. |
| • Money as integer piastres; loyalty rounded to the nearest tenth. | • Divide by 100 for display. |

**CHECK:** `contract/CHANGELOG.md` → "Phase 4". The architecture and specification are **not**
current for this phase.

# PHASE 4b — Package purchase (deferred, not started)

Split out of the original Phase 4. Nothing here is built.

| **BACKEND** | **FLUTTER / MOBILE** |
|---|---|
| • Purchase a package option, creating a purchased package. | • Purchase flow. |
| • InstaPay, pending purchases, payment confirmation. | • Payment screens. |
| • Activation, and **BR-03** one active package per athlete. | • Athlete package view. |
| • Remaining sessions = total − used. | • Balance display. |
| • Purchase history, close/complete behaviour. | • Package History screens. |
| • Payment status fields needed by Finance. | |

A purchased package must record the price **as paid**, independent of the catalogue: repricing or
archiving an option can never alter what somebody already bought.

**C-01 is closed.** The client ruled for the UI document's pair: `Pending | Paid`, and nothing
else. Partial payments are out of scope. **Built in Phase 8** — the purchase model, InstaPay
instructions and manual confirmation all shipped there; see `contract/CHANGELOG.md` → "Phase 8".
The purchase half of this section (creating a purchased package, BR-03, remaining sessions,
history, close) shipped earlier, in Phase 6.

**CHECK:** Architecture: Packages module, database invariants. Product Spec: Packages + BR-03.

# PHASE 5 — Scheduling & Calendly ▶ NEXT (blocked on A-01)

| **BACKEND**                                                    | **FLUTTER / MOBILE**                                            |
|----------------------------------------------------------------|-----------------------------------------------------------------|
| • Session model.                                               | • Admin Schedule.                                               |
| • Calendly client/configuration.                               | • Upcoming session cards.                                       |
| • Webhook receiver + raw webhook storage/idempotency.          | • Session Details.                                              |
| • Create/update/cancel local session projection from Calendly. | • Athlete Book Session opens Calendly when Athlete UI is ready. |
| • Calendly reconciliation job.                                 |                                                                 |
| • List schedule/upcoming sessions.                             |                                                                 |
| • Session details API.                                         |                                                                 |

**CHECK:** Architecture: Calendly Integration + Scheduling module. Product Spec: Booking & Sessions / BR-13.

# PHASE 6 — Attendance & Session Notes

| **BACKEND**                                                          | **FLUTTER / MOBILE**                                   |
|----------------------------------------------------------------------|--------------------------------------------------------|
| • Mark session Attended.                                             | • Admin Mark as Attended action.                       |
| • Exactly-once package deduction transaction.                        | • Session status updates.                              |
| • Cancelled and No-show status handling.                             | • Session notes UI.                                    |
| • Observation handling only according to confirmed product decision. | • Show session position/balance where specified by UI. |
| • Create/edit session notes.                                         |                                                        |
| • Return updated package balance after attendance.                   |                                                        |

**CHECK:** Architecture: Attendance Transaction + Scheduling/Packages domain rules. Product Spec: BR-04 to BR-07.

# PHASE 7 — To-Dos (DEFERRED — Phase 8 was built first)

| **BACKEND**                                   | **FLUTTER / MOBILE**                                             |
|-----------------------------------------------|------------------------------------------------------------------|
| • Create, edit and archive to-do.             | • Admin create/edit/archive To-Do screens when ready.            |
| • Assign athlete, due date, priority, status. | • Athlete To-Do list/detail/completion when Athlete UI is ready. |
| • List athlete to-dos.                        |                                                                  |
| • Athlete-only completion endpoint.           |                                                                  |
| • Overdue status job.                         |                                                                  |

**CHECK:** Architecture: ToDos module. Product Spec: To-Dos.

# PHASE 8 — Finance — Package purchase & manual payment ✅ BACKEND DONE

**Expenses were removed from this phase by the client**, and **Phase 7 (To-Dos) was deferred** —
Phase 8 was built before it.

| **BACKEND**                                                   | **FLUTTER / MOBILE**                                                           |
|---------------------------------------------------------------|--------------------------------------------------------------------------------|
| ✅ Athlete purchase request against a Phase 4 option.          | • Athlete package selection and Pay screen.                                    |
| ✅ Server-resolved price, snapshotted onto the purchase.       | • Display only. **Never calculate a price, discount or rounding.**             |
| ✅ `Pending → Paid`, the only transition, idempotent.          | • Admin payment screens once UI/UX is ready.                                   |
| ✅ Confirmation creates the purchased package atomically.      | • Retry `mark-paid` safely on timeout; use `alreadyPaid`.                       |
| ✅ One pending purchase per athlete; re-selecting replaces it. | • No cancel affordance — there is no such endpoint.                            |
| ✅ BR-03 re-checked at payment; conflict leaves it Pending.    | • Prompt "close the current package first" on 409.                             |
| ✅ Configurable InstaPay QR, link, recipient, instructions.    | • Read them from the API; never embed. Handle 503.                             |
| ✅ Admin-recorded packages get a `Paid` purchase; backfilled.  |                                                                                |
| ❌ Expenses — **removed from scope**, not built.               |                                                                                |

**C-01 closed:** `Pending | Paid` only. No `Unpaid`, no `PartiallyPaid`, no cancellation.

**CHECK:** `contract/CHANGELOG.md` → "Phase 8" — authoritative. The architecture's `Payments`
entity and its `/payments` endpoint rows describe a model this phase replaced; §6.2 and §14.7
have been amended.

# PHASE 9 — Admin Dashboard ◐ PARTIALLY DONE

| **BACKEND**                                         | **FLUTTER / MOBILE**                            |
|-----------------------------------------------------|-------------------------------------------------|
| ✅ Dashboard aggregate endpoint.                    | • Admin Home dashboard.                         |
| ✅ Sessions completed + coaching hours.             | • Stats cards and period filters.               |
| ✅ Upcoming sessions.                               | • Upcoming session cards.                       |
| ✅ Period filters.                                  | • Quick actions wired to the completed modules. |
| ⏸ Package alerts.                                   |                                                 |
| ⏸ Paid/unpaid totals.                               |                                                 |
| ⏸ Expenses.                                         |                                                 |
| ⏸ Last-session-note summary required by Admin Home. |                                                 |

**⏸ means deferred from the current Admin Home implementation — still in scope for this phase,
not removed from the product.** They are absent from `GET /dashboard/admin` only because the
Admin Home screen does not display them yet; expenses are additionally not built at all, having
been removed from Phase 8 by the client. Every one of them is additive to the existing response
and needs no change to what shipped.

Delivered: attended-only statistics over **calendar** periods (week starts Monday) computed in
the **Admin's own time zone**, the Online/Face-to-Face/Observation breakdown, coaching minutes
from the session's stored duration, and upcoming sessions that do not move when the period
changes. **A-02 closed** — the hours source is the session's stored `DurationMinutes`.

**CHECK:** `contract/CHANGELOG.md` → "Phase 9" — authoritative. Architecture: UI/UX architectural
consequences + Reporting/dashboard queries. UI/UX: Admin Home.

# PHASE 10 — Notifications

| **BACKEND**                                                    | **FLUTTER / MOBILE**                                  |
|----------------------------------------------------------------|-------------------------------------------------------|
| • Device tokens.                                               | • Notification Center.                                |
| • In-app notification records.                                 | • Unread badges.                                      |
| • Push notification dispatch.                                  | • Push handling + deep links to the correct screen.   |
| • Email notification dispatch where required.                  | • Notification preferences when Settings UI is ready. |
| • Deep-link destination payloads.                              |                                                       |
| • Deduplication + retry jobs.                                  |                                                       |
| • Booking/session/package/payment/to-do notification triggers. |                                                       |

**CHECK:** Architecture: Notifications module + Background Jobs + deep-link navigation. Product Spec: Notifications.

# PHASE 11 — Reports & Analytics

| **BACKEND**                                        | **FLUTTER / MOBILE**                                   |
|----------------------------------------------------|--------------------------------------------------------|
| • Weekly/monthly/yearly/all-time report endpoints. | • Admin Reports/Analytics screens once UI/UX is ready. |
| • Session counts + coaching hours.                 | • Period filters and empty/error/loading states.       |
| • Revenue/payment status.                          |                                                        |
| • Expenses.                                        |                                                        |
| • Outstanding balances.                            |                                                        |
| • Underlying record queries for future drill-down. |                                                        |

**CHECK:** Architecture: Reporting module. Product Spec: Reports & Analytics.

# PHASE 12 — Athlete Experience Completion

| **BACKEND**                                                                                           | **FLUTTER / MOBILE**                          |
|-------------------------------------------------------------------------------------------------------|-----------------------------------------------|
| • Expose/finish athlete-scoped endpoints for own profile, package, sessions, to-dos and booking data. | • Implement remaining Athlete Dashboard/Home. |
| • Verify ownership authorization on every athlete-scoped resource.                                    | • Package/session views.                      |
| • Complete athlete notification/preferences APIs required by final UI.                                | • Book Session.                               |
|                                                                                                       | • To-Dos.                                     |
|                                                                                                       | • Profile/Settings.                           |
|                                                                                                       | • Integrate all completed backend APIs.       |

**CHECK:** Use final Athlete UI/UX + relevant Product Spec sections. Backend should mostly reuse modules already built.

# PHASE 13 — Chat & Files

| **BACKEND**                                 | **FLUTTER / MOBILE**                                 |
|---------------------------------------------|------------------------------------------------------|
| • One conversation per athlete.             | • Conversation list/thread.                          |
| • Text messages + pagination/unread counts. | • Text send/receive.                                 |
| • SignalR real-time delivery.               | • Voice recording/playback.                          |
| • File upload URL/commit flow.              | • Image upload if enabled.                           |
| • Voice notes.                              | • Offline/pending message behavior per architecture. |
| • Images only if enabled/confirmed.         |                                                      |
| • Chat notification trigger.                |                                                      |

**CHECK:** Architecture: Chat, Files, Voice Notes, Image Uploads, Offline Strategy.

# PHASE 14 — Hardening, QA & Release

| **BACKEND**                                | **FLUTTER / MOBILE**                        |
|--------------------------------------------|---------------------------------------------|
| • Integration tests for critical flows.    | • Full end-to-end testing on iOS + Android. |
| • Authorization/ownership/security tests.  | • Loading/empty/error/offline states.       |
| • Calendly failure/retry tests.            | • Push/deep-link testing.                   |
| • Attendance/package concurrency tests.    | • Crash monitoring.                         |
| • Monitoring/logging/backup checks.        | • Store-ready builds and release checks.    |
| • Production configuration and deployment. |                                             |

**CHECK:** Architecture: Security, Logging/Monitoring, CI/CD, Testing and deployment sections.

# Quick Feature Map

| If you only need to know “what are we working on now?”, use this page. |
|------------------------------------------------------------------------|

| **Order** | **Feature / Module**   | **Backend Focus**           | **Flutter Focus**                       |
|-----------|------------------------|-----------------------------|-----------------------------------------|
| **0**     | **Setup**              | Repo/solution/DB/foundation | Flutter project/theme/router/API client |
| **1**     | **Auth**               | Identity/auth/roles         | Login/auth navigation                   |
| **2**     | **Athletes**           | Athlete APIs                | Admin athlete screens                   |
| **3**     | **Invitations**        | Invite/redeem/register      | Invite + account screens                |
| **4**     | **Packages**           | Package rules/APIs          | Admin package screens                   |
| **5**     | **Scheduling**         | Calendly + sessions         | Admin schedule/session screens          |
| **6**     | **Attendance**         | Deduction + notes           | Attendance/session notes                |
| **7**     | **To-Dos**             | To-do APIs/jobs             | Admin + Athlete To-Dos                  |
| **8**     | **Finance**            | Payments/expenses           | Finance screens when ready              |
| **9**     | **Dashboard**          | Aggregates                  | Admin Home                              |
| **10**    | **Notifications**      | Push/email/in-app           | Center + deep links                     |
| **11**    | **Reports**            | Report queries              | Reports screens                         |
| **12**    | **Athlete completion** | Athlete-scoped APIs         | Remaining Athlete UI                    |
| **13**    | **Chat & Files**       | SignalR/uploads             | Chat/voice/images                       |
| **14**    | **QA & Release**       | Security/tests/deploy       | E2E/store builds                        |

# Definition of Done for a Phase

- Backend migration/model + endpoint/use case implemented.

- Authorization and business rules applied.

- Critical backend tests pass.

- Available Flutter screens are connected to the real API.

- Loading, empty, validation and error states work.

- Feature is tested end-to-end in the development environment.

- Any unresolved product/UI decision is documented instead of guessed.

<table>
<colgroup>
<col style="width: 100%" />
</colgroup>
<thead>
<tr class="header">
<th>SOURCE DOCUMENTS<br />
1. Mental Coaching Platform — Product Specification v1.0: WHAT the product must do.<br />
2. Mental Coaching Platform — Software Architecture v1.0: HOW the system should be implemented.<br />
3. Beyond Movement — UI/UX Design Decisions: HOW each completed screen should behave/look.<br />
<br />
This roadmap does not replace those documents. It tells the team what to build next and where to look.</th>
</tr>
</thead>
<tbody>
</tbody>
</table>
