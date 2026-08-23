# Mental Coaching Platform — Product Specification

\[ INSERT LOGO HERE \]

Mental Coaching Platform

**PRODUCT SPECIFICATION**  
Version 1.0

| **Prepared for** | \_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_ |
|------------------|----------------------------------------------------------|
| **Prepared by**  | \_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_\_ |
| **Date**         | August 2026                                              |
| **Status**       | Approved requirements / design-ready                     |

# Document Control

This document is the single source of truth for the first release of the product. It is written for the client, designers, and development team.

| **Version** | **Date**    | **Status**       | **Summary**                                                    |
|-------------|-------------|------------------|----------------------------------------------------------------|
| 1.0         | August 2026 | Final for design | Consolidates all confirmed requirements and product decisions. |

# How to Use This Document

- Client: confirm the product still matches the agreed vision and use it as the reference for change requests.

- Designer: use the screen catalogue, user journeys, and business rules to create wireframes and final UI.

- Developers: use the functional requirements, data model, integrations, and acceptance criteria to plan implementation.

- Project team: treat anything marked Future Scope as out of the first release unless formally approved later.

#  

# Table of Contents

[**Document Control 2**](#_heading=)

[**How to Use This Document 2**](#_heading=)

[**Table of Contents 2**](#_heading=)

[**1. Product Overview 3**](#_heading=)

> [1.1 Product Vision 3](#_heading=)
>
> [1.2 Product Goals 3](#_heading=)
>
> [1.3 Scope of Version 1 3](#_heading=)
>
> [1.4 Out of Scope for Version 1 3](#_heading=)

[**2. User Roles and Permissions 3**](#_heading=)

> [2.1 Admin (Mental Coach) 3](#_heading=)
>
> [2.2 Athlete 4](#_heading=)
>
> [2.3 Permission Matrix 4](#_heading=)

[**3. Product Modules 4**](#_heading=)

[**4. Functional Requirements 5**](#_heading=)

> [4.1 Authentication and Invitations 5](#_heading=)
>
> [4.2 Admin Dashboard 5](#_heading=)
>
> [4.3 Athlete Dashboard 6](#_heading=)
>
> [4.4 Athlete Management 6](#_heading=)
>
> [4.5 Packages 7](#_heading=)
>
> [4.6 Booking and Session Management 7](#_heading=)
>
> [4.7 Chat 8](#_heading=)
>
> [4.8 To-Dos 8](#_heading=)
>
> [4.9 Payments and Expenses 9](#_heading=)
>
> [4.10 Notifications 9](#_heading=)
>
> [4.11 Profiles and Settings 9](#_heading=)
>
> [4.12 Reports and Analytics 10](#_heading=)

[**5. User Journeys 10**](#_heading=)

> [5.1 Admin Journey 10](#_heading=)
>
> [5.2 Athlete Onboarding Journey 11](#_heading=)
>
> [5.3 Athlete Booking Journey 11](#_heading=)
>
> [5.4 Package Renewal Journey 11](#_heading=)

[**6. Screen Catalogue 11**](#_heading=)

[**7. Common UI and UX Behaviour 12**](#_heading=)

> [Loading states 12](#_heading=)
>
> [Empty states 12](#_heading=)
>
> [Error states 12](#_heading=)
>
> [Confirmations 12](#_heading=)
>
> [Accessibility 12](#_heading=)
>
> [Branding 12](#_heading=)

[**8. Integrations 13**](#_heading=)

[**9. Conceptual Data Model 13**](#_heading=)

[**10. Non-Functional Requirements 13**](#_heading=)

> [Security 13](#_heading=)
>
> [Privacy 13](#_heading=)
>
> [Performance 14](#_heading=)
>
> [Reliability 14](#_heading=)
>
> [Scalability 14](#_heading=)
>
> [Compatibility 14](#_heading=)
>
> [Maintainability 14](#_heading=)

[**11. Consolidated Business Rules 14**](#_heading=)

[**12. Release Priorities 14**](#_heading=)

[**13. Release Acceptance Checklist 15**](#_heading=)

[**14. Assumptions and Dependencies 15**](#_heading=)

[**15. Next Steps 16**](#_heading=)

[**Appendix A. Glossary 16**](#_heading=)

# 1. Product Overview

The Mental Coaching Platform is a mobile application for a single Mental Coach and invited athletes. It brings athlete management, packages, booking, communication, tasks, notifications, payments tracking, and practice reporting into one place.

The first release will be available as a mobile app for both user types: the Admin (Mental Coach) and the Athlete.

## 1.1 Product Vision

To give the coach one simple place to manage the full coaching relationship while giving athletes a clear, organized way to book sessions, view their package, communicate, and follow assigned work.

## 1.2 Product Goals

- Reduce manual work currently spread across Calendly, messages, notes, and spreadsheets.

- Give the coach a clear overview of sessions, hours, packages, payments, and expenses.

- Give athletes easy access to booking, remaining sessions, reminders, chat, and to-dos.

- Keep registration private and invitation-only.

- Provide a foundation that can later support more advanced coaching tools.

## 1.3 Scope of Version 1

- Mobile app for Admin and Athlete.

- Invitation-only registration and secure login.

- Athlete profiles and one active package per athlete.

- Calendly Standard integration for booking and synchronization.

- Text and voice-note chat; image sharing if feasible.

- To-dos with due date, priority, and completion status.

- Push and email notifications.

- Manual payment confirmation after redirecting athletes to InstaPay.

- Admin reporting for sessions, hours, delivery type, payments, and expenses.

## 1.4 Out of Scope for Version 1

- Public self-registration.

- Multiple coaches or team accounts.

- Group chat or group sessions.

- Built-in video calls.

- Automatic InstaPay verification.

- A fully custom booking engine.

- AI summaries, journaling, mood tracking, or assessments.

# 2. User Roles and Permissions

## 2.1 Admin (Mental Coach)

The Admin owns and manages the platform. There is one Admin account in Version 1.

- View practice overview and reports.

- Invite, manage, pause, reactivate, and delete athletes.

- Create and manage packages.

- View and manage booked sessions.

- Mark attendance and deduct sessions.

- Record payments and expenses.

- Assign to-dos.

- Chat with athletes.

- Add session observations and share whiteboard/notes links.

## 2.2 Athlete

Athletes are invited by the Admin. Each athlete can access only their own data and interactions.

- Create an account using a code emailed to the address invited by the Admin. Invitation links may be added later as an alternative entry method.

- Log in using email/password or Google.

- View active and past packages.

- Book, cancel, and reschedule sessions through Calendly.

- View upcoming and past sessions.

- Chat with the coach.

- View and complete assigned to-dos.

- Receive reminders and renewal notifications.

- Open shared whiteboard/notes links.

## 2.3 Permission Matrix

| **Capability**         | **Admin**                     | **Athlete** |
|------------------------|-------------------------------|-------------|
| View all athletes      | Yes                           | No          |
| View own profile       | Yes                           | Yes         |
| Invite athlete         | Yes                           | No          |
| Pause athlete          | Yes                           | No          |
| Delete athlete data    | Yes                           | No          |
| Create package         | Yes                           | No          |
| View own package       | Yes                           | Yes         |
| Book session           | Optional on behalf of athlete | Yes         |
| Mark attended          | Yes                           | No          |
| Assign to-do           | Yes                           | No          |
| Complete to-do         | View progress                 | Yes         |
| Send chat messages     | Yes                           | Yes         |
| Record payment/expense | Yes                           | No          |
| View practice reports  | Yes                           | No          |

# 3. Product Modules

| **Module**                       | **Purpose**                                                                                   |
|----------------------------------|-----------------------------------------------------------------------------------------------|
| **Authentication & Invitations** | Controls private access, invitation links/codes, sign-in, password reset, and paused access.  |
| **Admin Dashboard**              | Shows sessions, hours, delivery type, payments, expenses, and package alerts.                 |
| **Athlete Management**           | Stores athlete details, package information, notes links, payment status, and account status. |
| **Packages**                     | Tracks one active session package and previous package history.                               |
| **Booking & Sessions**           | Uses Calendly Standard for availability, booking, cancellation, and rescheduling.             |
| **Chat**                         | Supports direct coach-athlete communication.                                                  |
| **To-Dos**                       | Lets the coach assign and track actions between sessions.                                     |
| **Payments & Expenses**          | Tracks whether athletes paid and records coach expenses.                                      |
| **Notifications**                | Delivers push and email alerts.                                                               |
| **Profiles & Settings**          | Allows users to manage their information and preferences.                                     |
| **Reports & Analytics**          | Summarizes practice activity over selected periods.                                           |

# 4. Functional Requirements

## 4.1 Authentication and Invitations

**Purpose**

Provide secure, private access to the platform without public sign-up.

**Core capabilities**

- From the Athlete List, Admin can open an Invite Athlete modal, enter the athlete's email address, and send an invitation.

- The backend generates a unique code bound to that email and sends it directly to the address.

- Athlete can enter the code from the Login screen and create an account using a password or a matching Google account.

- Admin and Athlete can sign in using email/password or Google OAuth.

- Users can request a password reset by email.

- The system displays a clear message for invalid or expired invitations.

- The Admin can pause or reactivate an athlete account.

**Business rules**

- No athlete can register without a valid invitation.

- Each invitation can be used only for its intended athlete and should expire after use or after a configurable period.

- Successful validation of a code delivered by the backend to the intended email counts as verification of that email.

- The verified invitation email is read-only during account creation.

- Invitation validation does not consume the invitation; it is redeemed only when account creation succeeds.

- Google registration requires the Google account email to match the verified invitation email and does not require a password.

- Account creation collects authentication details only. Full name and athlete-specific details are collected on Complete Profile before Athlete Home.

- No username is required; email is the login identifier.

- A Google-created account holder who still controls the verified email can use Forgot Password to set a first local password.

- A paused athlete cannot log in or use the app.

- User data remains stored while the account is paused.

**Primary screens**

Welcome / Login, Invite Athlete modal, Enter Invitation Code, Create Account, Complete Profile, Forgot Password, Invitation Error.

**Acceptance criteria**

- A valid invitation creates exactly one athlete account.

- The backend sends the invitation code only to the intended email, and successful validation opens Create Account with that email read-only.

- Password registration requires a password; Google registration requires a matching Google email and no password.

- Both registration methods continue to Complete Profile, where the athlete enters their full name and required profile information.

- A paused athlete receives an access-disabled message.

- Google and email/password login both open the correct role-based dashboard.

## 4.2 Admin Dashboard

**Purpose**

Give the coach a clear snapshot of practice activity and items needing attention.

**Core capabilities**

- Switch between weekly, monthly, yearly, and all-time views.

- Display number of sessions and total coaching hours.

- Break down sessions into online, face-to-face, and observations.

- Show paid versus unpaid athletes.

- Show total recorded expenses for the selected period.

- Show athletes with one session remaining or no sessions remaining.

- Show today’s and upcoming sessions.

- Provide shortcuts to athletes, schedule, payments, and notifications.

**Business rules**

- Dashboard totals are based on stored session records and selected date range.

- Only attended sessions contribute to delivered-session totals.

- Cancelled sessions do not count as delivered sessions.

- Observation records longer than one hour consume one session.

**Primary screens**

Admin Home / Dashboard.

**Acceptance criteria**

- Changing the date range refreshes all dashboard values.

- Totals match the underlying sessions, payments, and expenses.

- Alerts open the relevant athlete or package record.

## 4.3 Athlete Dashboard

**Purpose**

Give each athlete a simple view of what matters now.

**Core capabilities**

- Show active package and sessions remaining.

- Show next booked session.

- Show recent chat messages or unread count.

- Show outstanding to-dos.

- Show important notifications.

- Provide direct buttons for booking, chat, package details, and profile.

**Business rules**

- The athlete sees only their own information.

- If no active package exists, the dashboard shows a renewal message instead of package balance.

**Primary screens**

Athlete Home / Dashboard.

**Acceptance criteria**

- The next session matches the latest Calendly-synced booking.

- The package balance matches attended-session deductions.

- Paused athletes cannot reach the dashboard.

## 4.4 Athlete Management

**Purpose**

Give the coach one complete view of each athlete and their coaching history.

**Core capabilities**

- View athlete list with name, sport, active/paused status, package balance, and payment status.

- Search and filter athletes.

- Open athlete profile containing photo, name, sport, phone, email, gender, date of birth, package details, previous packages, whiteboard/notes link, payment status, and session history.

- Edit athlete details.

- Pause or reactivate the athlete.

- Delete athlete data after explicit confirmation.

**Business rules**

- Athlete data is retained until deleted by the Admin.

- Deletion should remove or anonymize associated personal data according to the final privacy implementation.

- Paused athletes remain visible to the Admin.

**Primary screens**

Athlete List, Athlete Profile, Edit Athlete, Pause Confirmation, Delete Confirmation.

**Acceptance criteria**

- Search returns matching athletes by name or sport.

- Pausing immediately blocks future login.

- Deleting requires confirmation and cannot happen accidentally.

## 4.5 Packages

> **⚠ This section describes the PURCHASED package only, and is not yet built.**
>
> The client split packages in two after this document was written:
>
> | | Where it lives |
> |---|---|
> | **Package options** — the reusable catalogue the coach sells: name, sessions, default price, ordered features, archive/restore, athlete loyalty discount, per-athlete price overrides | **Built.** Phase 4. Specified in `contract/CHANGELOG.md` → "Phase 4". Nothing about it is described below. |
> | **Purchased packages** — everything in this section: an athlete buying a package, session balance, history, renewal, BR-03 | **Not built.** Deferred to Phase 4b in the roadmap. |
>
> Read this section for the purchase model. Do **not** read it as a description of what the API
> does today, and do not treat the absence of a catalogue here as meaning one should not exist.

**Purpose**

Track the session balance and package history for each athlete.

**Core capabilities**

- Admin creates a package by entering total number of sessions, price, start date, optional notes, and payment status.

- Only one package can be active for an athlete at a time.

- Athlete can view the active package and previous packages.

- System shows sessions used and remaining.

- Admin can renew by creating a new package after the previous package is completed or closed.

**Business rules**

- A booking does not deduct a session.

- A session is deducted only after the Admin marks it Attended.

- When one session remains, notify the athlete.

- When zero sessions remain, notify the athlete to renew/resubscribe.

- Previous packages remain visible in history.

**Primary screens**

Package List / History, Package Details, Create Package, Renew Package.

**Acceptance criteria**

- Creating a second active package is prevented.

- Attendance reduces balance by exactly one session unless an approved special rule applies.

- Notifications trigger at one and zero remaining sessions.

## 4.6 Booking and Session Management

**Purpose**

Keep the current Calendly workflow while showing bookings inside the platform.

**Core capabilities**

- Athlete taps Book Session inside the app.

- Calendly Standard booking interface opens embedded or in an in-app browser.

- Coach manages availability in Calendly.

- Successful bookings sync to both Admin and Athlete schedules.

- Cancellations and reschedules follow Calendly and sync back to the app.

- Each session record shows date, time, delivery type, status, and linked athlete.

- Admin can mark a past session Attended, No-show, or Cancelled.

- Admin can attach observations and a whiteboard/notes link to the session.

**Business rules**

- Approved solution is Calendly Standard.

- The app uses Calendly as the source of truth for booking, cancellation, and rescheduling.

- Sessions are not deducted when booked.

- Attended sessions consume one package session.

- Cancelled sessions do not consume a session.

- No-show deduction remains configurable; Version 1 default is no deduction unless the Admin marks the session attended or explicitly overrides according to business policy.

- Observations longer than one hour consume one session.

**Primary screens**

Book Session, Schedule / Calendar, Session Details, Mark Attendance, Cancellation / Reschedule via Calendly.

**Acceptance criteria**

- A new Calendly booking appears in both accounts.

- A cancellation or reschedule updates both accounts.

- Package balance changes only after attendance is saved.

## 4.7 Chat

**Purpose**

Allow direct communication between the coach and each athlete.

**Core capabilities**

- One-to-one conversations only.

- Send and receive text messages.

- Record and send voice notes.

- Send images if feasible within Version 1 implementation.

- Show unread message count and message notifications.

- Admin can view a list of all athlete conversations.

**Business rules**

- Athletes can chat only with the Admin.

- Message editing and deletion are deferred to a later version.

- Chat history is retained until deleted with the athlete record or according to future retention policy.

**Primary screens**

Conversation List, Chat Thread, Voice Note Recorder, Image Picker (optional).

**Acceptance criteria**

- Messages appear in the correct conversation.

- Unread badges clear when the thread is opened.

- Voice notes can be played after sending.

## 4.8 To-Dos

**Purpose**

Help the coach assign work between sessions and track completion.

**Core capabilities**

- Admin creates a to-do with title, description, due date, priority, and status.

- Athlete views assigned to-dos.

- Athlete marks a to-do completed.

- Admin can edit, archive, or reopen a to-do.

- New assignments and due reminders generate notifications.

**Business rules**

- Priority values are Low, Medium, and High.

- Status values are Pending, Completed, Overdue, and Archived.

- A to-do becomes overdue when its due date passes while still pending.

**Primary screens**

To-Do List, To-Do Details, Create / Edit To-Do.

**Acceptance criteria**

- Completing a to-do updates both Admin and Athlete views.

- Overdue status is calculated automatically.

## 4.9 Payments and Expenses

**Purpose**

Provide simple financial tracking without building a full payment gateway in Version 1.

**Core capabilities**

- Athlete can tap Pay and be redirected to the coach’s InstaPay payment destination or instructions.

- Admin manually marks a package payment as Paid after receiving it.

- Admin can mark payment as Unpaid, Partially Paid, or Paid.

- Admin records expenses with amount, date, category, and note.

- Dashboard shows paid/unpaid athletes and total expenses.

- Payment reminder notifications can be sent to athletes.

**Business rules**

- No third-party payment gateway is integrated in Version 1.

- The app does not automatically verify InstaPay payments.

- Admin confirmation is the source of truth for payment status.

**Primary screens**

Payment Details, InstaPay Redirect / Instructions, Record Payment, Expenses List, Add Expense.

**Acceptance criteria**

- Tapping Pay opens the configured InstaPay destination or instructions.

- Admin status changes appear immediately in the athlete package view if payment status is shown there.

- Dashboard figures match recorded entries.

## 4.10 Notifications

**Purpose**

Keep users informed about important events without requiring constant manual checking.

**Core capabilities**

- Send push and email notifications.

- Notify for booking confirmation, rescheduling, cancellation, and upcoming session.

- Notify for new chat messages.

- Notify for new or due to-dos.

- Notify when one package session remains.

- Notify when the package reaches zero and renewal is needed.

- Notify for payment reminders.

**Business rules**

- Users may manage non-essential notification preferences in Settings.

- Critical account and security messages cannot be fully disabled.

- Duplicate notifications should be avoided where possible.

**Primary screens**

Notifications Centre, Notification Preferences.

**Acceptance criteria**

- Selecting a notification opens the correct screen.

- Push and email delivery are logged for troubleshooting where feasible.

## 4.11 Profiles and Settings

**Purpose**

Allow each user to maintain accurate personal information and preferences.

**Core capabilities**

- Admin profile includes photo, name, phone, email, and general information.

- Athlete profile includes photo, name, phone, email, sport, gender, and date of birth.

- Users can update allowed profile fields.

- Users can manage password, sign-out, and notification preferences.

**Business rules**

- Role and account status cannot be changed by the Athlete.

- Sensitive changes may require re-authentication.

**Primary screens**

Admin Profile, Athlete Profile, Edit Profile, Settings, Change Password.

**Acceptance criteria**

- Updated profile data appears across the app.

- Invalid email and phone formats are rejected.

## 4.12 Reports and Analytics

**Purpose**

Help the coach understand activity over time.

**Core capabilities**

- Filter by week, month, year, or all time.

- Show number of attended sessions.

- Show total coaching hours.

- Show online versus face-to-face delivery.

- Show observations.

- Show paid versus unpaid athlete counts.

- Show expenses.

- Show athletes approaching package completion.

**Business rules**

- Only attended sessions count as completed service.

- Reports use the user’s local timezone.

- Totals should be reproducible from underlying records.

**Primary screens**

Reports Dashboard, Report Detail (optional).

**Acceptance criteria**

- Date filters produce correct totals.

- Online and face-to-face counts add up to the reported attended sessions where applicable.

# 5. User Journeys

## 5.1 Admin Journey

1\. Sign in.

2\. Review dashboard and today’s schedule.

3\. Open athlete list or an alert.

4\. Review athlete package, payment status, notes, and previous work.

5\. Conduct session.

6\. Mark session as Attended and add observations or a notes link.

7\. Assign a to-do if needed.

8\. Reply in chat.

9\. Review package balance and trigger renewal/payment follow-up when needed.

## 5.2 Athlete Onboarding Journey

1\. Receive an invitation code at the email address entered by the coach.

2\. From Login, select Enter invitation code and validate the emailed code.

3\. Create an account using a password or a Google account with the same verified email.

4\. Complete profile information, including full name.

5\. Reach the Athlete Dashboard and view available actions.

## 5.3 Athlete Booking Journey

1\. Tap Book Session.

2\. Calendly opens inside the app or an in-app browser.

3\. Choose an available time and confirm.

4\. Receive Calendly/app confirmation.

5\. See the session on the Athlete Dashboard.

6\. Receive reminder before the session.

7\. Attend the session.

8\. Coach marks Attended; package balance decreases.

## 5.4 Package Renewal Journey

1\. Athlete reaches one remaining session and receives a reminder.

2\. After the final attended session, the balance reaches zero.

3\. Athlete receives a renewal/resubscribe notification.

4\. Athlete follows payment instructions through InstaPay.

5\. Admin confirms payment and creates a new active package.

# 6. Screen Catalogue

| **Screen**               | **Role** | **Purpose**              | **Key Elements**                                 | **Primary Actions**                |
|--------------------------|----------|--------------------------|--------------------------------------------------|------------------------------------|
| Welcome / Login          | Both     | Authenticate user or start invited onboarding | Email, password, Google sign-in, forgot password, Enter invitation code | Sign in / enter code |
| Invite Athlete modal     | Admin    | Invite an athlete by email | Athlete email | Send invitation |
| Enter Invitation Code    | Athlete  | Validate invitation and verify email | Invitation code, validation and error states | Continue to Create Account |
| Create Account           | Athlete  | Establish authentication | Read-only verified email, password fields or Google, legal acceptance | Create account |
| Complete Profile         | Athlete  | Collect athlete details after authentication | Full name, profile photo, date of birth, gender, sport | Finish setup |
| Admin Dashboard          | Admin    | Practice overview        | Date filter, KPIs, alerts, upcoming sessions     | Open details                       |
| Athlete Dashboard        | Athlete  | Personal overview        | Package, next session, to-dos, unread chat       | Book / open item                   |
| Athlete List             | Admin    | Find and manage athletes | Search, filters, status, balance                 | Open athlete                       |
| Athlete Profile          | Admin    | Complete athlete record  | Personal info, package, history, payments, notes | Edit / pause / delete              |
| Athlete Profile          | Athlete  | View/update own profile  | Photo, personal details                          | Edit allowed fields                |
| Package Details          | Both     | View package status      | Total, used, remaining, payment, history         | Renew / pay                        |
| Create Package           | Admin    | Assign or renew package  | Sessions, price, start date, notes               | Save                               |
| Book Session             | Athlete  | Book through Calendly    | Embedded Calendly experience                     | Select slot                        |
| Schedule                 | Both     | View sessions            | Calendar/list, status, delivery type             | Open session                       |
| Session Details          | Both     | View session             | Date, time, type, status, notes link             | Cancel/reschedule or mark attended |
| Conversation List        | Admin    | See athlete chats        | Unread count, latest message                     | Open thread                        |
| Chat Thread              | Both     | Direct communication     | Text, voice notes, image optional                | Send message                       |
| To-Do List               | Both     | Track assigned work      | Filters, due dates, priorities, status           | Open / complete                    |
| Create To-Do             | Admin    | Assign work              | Title, description, date, priority               | Save                               |
| Payments                 | Admin    | Track package payments   | Paid/unpaid/partial, history                     | Update status                      |
| Expenses                 | Admin    | Track costs              | Date, category, amount                           | Add/edit expense                   |
| Notifications            | Both     | View alerts              | Notification list, read/unread                   | Open destination                   |
| Profile & Settings       | Both     | Manage account           | Profile, password, notifications, sign out       | Update settings                    |

# 7. Common UI and UX Behaviour

## Loading states

- Show a clear loading indicator when data is being fetched.

- Avoid blank screens during synchronization.

## Empty states

- Explain why a list is empty and provide the next useful action.

- Example: “No upcoming sessions. Book a session.”

## Error states

- Use plain-language errors.

- Allow retry where possible.

- Do not expose technical error details to users.

## Confirmations

- Require confirmation before pausing, deleting, cancelling, or changing payment status.

## Accessibility

- Use readable text sizes, clear contrast, large touch targets, and labels for icons.

## Branding

- Insert approved logo, colors, and typography before final visual design.

- This document uses placeholders only.

# 8. Integrations

| **Integration**    | **Purpose**                                       | **Version 1 Approach**                                           | **Notes**                                                    |
|--------------------|---------------------------------------------------|------------------------------------------------------------------|--------------------------------------------------------------|
| Calendly Standard  | Availability, booking, cancellation, rescheduling | Embed booking and synchronize events using Calendly API/webhooks | One paid coach seat; athletes do not need Calendly accounts. |
| Google OAuth       | Faster sign-in                                    | Allow Google sign-in for Admin and Athlete                       | Invitation validation still applies to athletes.             |
| Push Notifications | Mobile reminders                                  | Use a mobile push service appropriate to the chosen framework    | Final provider selected during technical design.             |
| Email              | Account and reminder emails                       | Transactional email provider                                     | Final provider selected during technical design.             |
| InstaPay           | Payment direction                                 | Redirect or show configured payment instructions                 | Admin manually confirms receipt.                             |
| Cloud Storage      | Voice notes and optional images                   | Store securely with access control                               | Provider selected during architecture design.                |

# 9. Conceptual Data Model

The following entities describe the information the system must store. This is not a final database schema; the technical team will convert it into tables and relationships.

| **Entity**          | **Key Information**                                                                                 |
|---------------------|-----------------------------------------------------------------------------------------------------|
| **User**            | ID, role, name, email, phone, password/OAuth details, photo, status, notification preferences       |
| **Athlete Profile** | User ID, sport, gender, date of birth, contact details, notes link, account status                  |
| **Invitation**      | Hashed code, athlete email, status, created date, expiry date, validation date, redeemed date       |
| **Package**         | Athlete ID, total sessions, used sessions, remaining sessions, price, start date, status, notes     |
| **Session**         | Athlete ID, Calendly event ID, date/time, duration, delivery type, status, observations, notes link |
| **To-Do**           | Athlete ID, title, description, due date, priority, status, created by                              |
| **Conversation**    | Admin ID, athlete ID, latest message date                                                           |
| **Message**         | Conversation ID, sender, type, content/file reference, sent date, read status                       |
| **Payment**         | Athlete/package ID, amount, status, date, confirmation note                                         |
| **Expense**         | Amount, category, date, note                                                                        |
| **Notification**    | User ID, type, title, body, destination, read status, delivery status                               |

# 10. Non-Functional Requirements

## Security

- Encrypt data in transit and at rest where supported.

- Store passwords using a secure one-way hashing method.

- Use role-based authorization on every protected action.

- Validate Calendly and other webhook signatures.

- Do not expose one athlete’s data to another athlete.

## Privacy

- Collect only information required for the coaching service.

- Retain data until deleted by the Admin, subject to applicable legal requirements.

- Provide a privacy policy and consent language before launch.

- Deletion must be logged and handled carefully.

## Performance

- Main screens should load quickly under normal mobile conditions.

- Chat and notification updates should feel near real time.

- Long-running operations should not freeze the interface.

## Reliability

- Prevent duplicate bookings and duplicate webhook records.

- Use backups for core data.

- Record integration failures for support and retry.

## Scalability

- Architecture should support more athletes and future multiple-coach expansion without a full rewrite.

## Compatibility

- Support current iOS and Android versions selected during development.

- Use responsive layouts suitable for common phone sizes.

## Maintainability

- Separate mobile UI, backend services, and integrations cleanly.

- Use documented APIs and environment-based configuration.

# 11. Consolidated Business Rules

| **ID**    | **Rule**                                                                    |
|-----------|-----------------------------------------------------------------------------|
| **BR-01** | The platform is invitation-only.                                            |
| **BR-02** | There is one Admin account in Version 1.                                    |
| **BR-03** | An athlete can have only one active package.                                |
| **BR-04** | A booking never deducts a package session.                                  |
| **BR-05** | A session is deducted only after the Admin marks it Attended.               |
| **BR-06** | Cancelled sessions do not consume a session.                                |
| **BR-07** | Observation work longer than one hour consumes one session.                 |
| **BR-08** | The athlete is notified at one remaining session.                           |
| **BR-09** | The athlete is notified again when the package reaches zero.                |
| **BR-10** | Paused athletes cannot log in.                                              |
| **BR-11** | Paused athlete data remains available to the Admin.                         |
| **BR-12** | Data is retained until deleted by the Admin, subject to legal requirements. |
| **BR-13** | Calendly Standard is the approved booking solution for Version 1.           |
| **BR-14** | InstaPay payment is confirmed manually by the Admin.                        |
| **BR-15** | Whiteboard or notes links may be shared with the athlete.                   |
| **BR-16** | Chat is one-to-one between the Admin and an athlete.                        |
| **BR-17** | Text and voice notes are required; images are optional if feasible.         |
| **BR-18** | Push and email are the required notification channels.                      |

# 12. Release Priorities

| **Priority** | **Meaning**                      | **Features**                                                                                                                                                                                                                                           |
|--------------|----------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Must Have    | Required for first release       | Authentication, invitations, dashboards, athlete profiles, one active package, Calendly Standard booking sync, session attendance, text chat, voice notes, to-dos, push/email notifications, manual payment tracking, expenses, reports, pause access. |
| Nice to Have | Include if time and effort allow | Image attachments in chat, richer filtering, exportable reports, advanced notification preferences.                                                                                                                                                    |
| Future       | Explicitly outside first release | Custom scheduler, automatic InstaPay verification, multiple coaches, journaling, mood tracking, AI summaries, video calls, group features.                                                                                                             |

# 13. Release Acceptance Checklist

☐ Admin can invite an athlete and the athlete can create an account.

☐ Email/password and Google login work for the correct role.

☐ Paused athletes cannot access the app.

☐ Admin dashboard shows correct period-based totals.

☐ Athlete dashboard shows the correct package balance and next session.

☐ Only one active package can exist per athlete.

☐ Calendly booking, cancellation, and rescheduling synchronize successfully.

☐ Booked sessions do not reduce package balance.

☐ Marking a session Attended reduces the balance once and only once.

☐ One-session-left and zero-session notifications are sent.

☐ Text chat and voice notes work in both directions.

☐ To-dos can be created, viewed, completed, and marked overdue.

☐ InstaPay redirect/instructions open correctly.

☐ Admin can manually update payment status and record expenses.

☐ Push and email notifications open the correct destination.

☐ Users can update allowed profile fields.

☐ No athlete can access another athlete’s information.

☐ Core data is backed up and integration failures are logged.

# 14. Assumptions and Dependencies

- The client will provide the final logo, brand colors, and any required legal text.

- The coach will maintain availability and event settings in Calendly.

- A Calendly Standard subscription will be active before production launch.

- The InstaPay destination or instructions will be supplied by the client.

- Final app-store accounts, privacy policy, and terms will be prepared before release.

- No major change to the two-role model is expected during Version 1 design.

# 15. Next Steps

1\. Create low-fidelity wireframes from the screen catalogue and user journeys.

2\. Review navigation and screen flow with the client.

3\. Apply the approved brand and create high-fidelity Figma designs.

4\. Create the technical architecture, database schema, and API specification.

5\. Break the requirements into epics, user stories, and development tasks.

6\. Build, test, and release in agreed milestones.

# Appendix A. Glossary

| **Term**              | **Meaning**                                                                               |
|-----------------------|-------------------------------------------------------------------------------------------|
| **Admin**             | The Mental Coach and owner of the platform.                                               |
| **Athlete**           | An invited coaching client.                                                               |
| **Active Package**    | The athlete’s current usable set of sessions.                                             |
| **Attended**          | A session that took place and consumes one package session.                               |
| **Calendly**          | The external scheduling service used for availability and booking.                        |
| **Observation**       | A coaching observation linked to the athlete/session; over one hour consumes one session. |
| **Push Notification** | An alert delivered to the user’s phone.                                                   |
| **Webhook**           | A background message from Calendly that tells the app a booking changed.                  |

**  
END OF PRODUCT SPECIFICATION**
