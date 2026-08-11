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

# PHASE 0 — Project Setup

| **BACKEND**                                                                                                 | **FLUTTER / MOBILE**                                      |
|-------------------------------------------------------------------------------------------------------------|-----------------------------------------------------------|
| • Create backend repo and ASP.NET Core solution using the architecture folder/module structure.             | • Create Flutter repo/project.                            |
| • Create PostgreSQL database setup + EF Core migrations.                                                    | • Create app theme/design tokens and shared widgets.      |
| • Set up environments/configuration, secrets approach, logging, error handling foundation, health endpoint. | • Create Admin/Athlete app shells and routing foundation. |
| • Set up basic CI build/test pipeline.                                                                      | • Set up API client/environment configuration.            |

**CHECK:** Architecture: High-Level Architecture, Technology Stack, Mobile Architecture, Backend Architecture.

# PHASE 1 — Authentication & Access

| **BACKEND**                                              | **FLUTTER / MOBILE**                         |
|----------------------------------------------------------|----------------------------------------------|
| • Identity module: Users, roles, status, refresh tokens. | • Login screen and auth state.               |
| • Admin email/password login.                            | • Forgot/reset password screens if ready.    |
| • JWT access + rotating refresh tokens.                  | • Role-aware navigation.                     |
| • Google sign-in backend verification/foundation.        | • Access Disabled state for paused athletes. |
| • Role authorization + paused-athlete access checks.     |                                              |
| • Password reset flow.                                   |                                              |

**CHECK:** Architecture: Authentication & Authorization. Product Spec: Authentication & Invitations.

# PHASE 2 — Athlete Management — Admin First

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

# PHASE 3 — Invitations & Athlete Account Creation

| **BACKEND**                                                               | **FLUTTER / MOBILE**                                 |
|---------------------------------------------------------------------------|------------------------------------------------------|
| • Create invitation code/link.                                            | • Admin Invite Athlete flow.                         |
| • Invitation expiry, single-use validation and intended-email validation. | • Athlete Enter Access Code / invitation validation. |
| • Redeem invitation.                                                      | • Create Account flow when Athlete UI is ready.      |
| • Create athlete account/profile/conversation in one transaction.         | • Invitation error states.                           |
| • Invitation email integration.                                           |                                                      |

**CHECK:** Architecture: Invitation Flow + Identity module. Product Spec: BR-01 / BR-02.

# PHASE 4 — Packages

| **BACKEND**                                                   | **FLUTTER / MOBILE**                                           |
|---------------------------------------------------------------|----------------------------------------------------------------|
| • Package model + create package.                             | • Admin package section on Athlete Profile.                    |
| • Enforce one active package per athlete.                     | • Create Package.                                              |
| • Package history.                                            | • Package History / details screens that are already designed. |
| • Remaining sessions = total - used.                          | • Athlete package view later when Athlete UI is ready.         |
| • Close/complete package behavior.                            |                                                                |
| • Package payment status fields needed by later Finance work. |                                                                |

**CHECK:** Architecture: Packages module, database invariants. Product Spec: Packages + BR-03.

# PHASE 5 — Scheduling & Calendly

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

# PHASE 7 — To-Dos

| **BACKEND**                                   | **FLUTTER / MOBILE**                                             |
|-----------------------------------------------|------------------------------------------------------------------|
| • Create, edit and archive to-do.             | • Admin create/edit/archive To-Do screens when ready.            |
| • Assign athlete, due date, priority, status. | • Athlete To-Do list/detail/completion when Athlete UI is ready. |
| • List athlete to-dos.                        |                                                                  |
| • Athlete-only completion endpoint.           |                                                                  |
| • Overdue status job.                         |                                                                  |

**CHECK:** Architecture: ToDos module. Product Spec: To-Dos.

# PHASE 8 — Finance — Payments & Expenses

| **BACKEND**                                  | **FLUTTER / MOBILE**                                                           |
|----------------------------------------------|--------------------------------------------------------------------------------|
| • Payment records linked to athlete/package. | • Admin payment screens once UI/UX is ready.                                   |
| • Manual payment confirmation.               | • Admin expense screens once UI/UX is ready.                                   |
| • Payment status calculation.                | • Do not block backend data model/API work if final screen styling is pending. |
| • Expense create/edit/list.                  |                                                                                |
| • Date/category data needed for reports.     |                                                                                |

**CHECK:** Architecture: Finance module + Payments/Expenses entities. Product Spec: Payments & Expenses.

# PHASE 9 — Admin Dashboard

| **BACKEND**                                         | **FLUTTER / MOBILE**                            |
|-----------------------------------------------------|-------------------------------------------------|
| • Dashboard aggregate endpoint.                     | • Admin Home dashboard.                         |
| • Sessions completed + coaching hours.              | • Stats cards and period filters.               |
| • Upcoming sessions.                                | • Upcoming session cards.                       |
| • Package alerts.                                   | • Quick actions wired to the completed modules. |
| • Paid/unpaid totals.                               |                                                 |
| • Expenses.                                         |                                                 |
| • Period filters.                                   |                                                 |
| • Last-session-note summary required by Admin Home. |                                                 |

**CHECK:** Architecture: UI/UX architectural consequences + Reporting/dashboard queries. UI/UX: Admin Home.

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
