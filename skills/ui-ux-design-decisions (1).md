# Beyond Movement — UI/UX Design Decisions

Purpose: This document captures UI, UX, and interaction decisions made during the design process. It complements the SRS and serves as the implementation reference.

## Global Design Principles

### Design Style
- ​ Clean and minimal
- ​ Premium, modern appearance
- ​ White background
- ​ Primary color: #3E4DA1
- ​ Secondary color: #D5EFFA
- ​ Accent color: #FDF6B0
- ​ Notification/highlight color: #D86ED7
- ​ Rounded corners
- ​ Soft shadows
- ​ Plenty of whitespace

### General UX Principles
- ​ Prioritize simplicity.
- ​ Show only the most important information.
- ​ Keep navigation intuitive.
- ​ Reuse components.
- ​ Mobile-first design.

## Admin Home
### Purpose
- ​ Provide the mental coach with a quick overview of activity and shortcuts to common actions.

### Header
- ​ Greeting
- ​ Current date
- ​ Coach profile picture

### Period Filter
- ​ Weekly
- ​ Monthly
- ​ Yearly
- ​ All Time
- ​ Selecting a period updates dashboard statistics only.

- ​ Layout remains unchanged.

### Statistics
- ​ Sessions
- ​ Hours
- ​ Online Sessions
- ​ Face-to-Face Sessions
- ​ Observations
- ​ Statistics update according to the selected period.
- ​ Statistic cards may become clickable in a future version.

### Upcoming Sessions
- ​ Display the next two sessions.
- ​ Horizontal scrolling if more sessions exist.
- ​ Each card shows: Time, Athlete Name, Session Type (Online / Face-to-Face), Last Session Note (1 line).
- ​ Selecting a session opens the Athlete Profile.

### Quick Actions
- ​ New Session
- ​ Add Athlete
- ​ Messages
- ​ Create To-Do

### Bottom Navigation
- ​ Home
- ​ Athletes
- ​ Schedule
- ​ More
- ​ Chat is accessed from Athlete Profile and Messages.

### Future Improvements
- ​ Improve the Online / Face-to-Face indicator.
- ​ Analytics drill-down from statistic cards.
- ​ Polish icons and spacing after all screens are completed.

## Athlete List
### Purpose
- ​ The Athlete List screen allows the coach to quickly view, search, filter, sort, and access all athletes in the system.

### Header
Displays:

- ​ Screen title: Athletes
- ​ Total number of athletes (e.g., 24 Athletes)

Search
- ​ A search bar allows the coach to search athletes by name.

Filters Available filters:

- ​ All
- ​ Active
- ​ Inactive

Behaviour

- ​ All: Displays all athletes; shows the Active/Inactive status badge on each athlete card.
- ​ Active: Displays only athletes with an active package; hides the Active status badge, as all displayed athletes are already active.
- ​ Inactive: Displays only athletes without an active package; hides the Inactive status badge; hides the Sessions Remaining field, since inactive athletes do not have an active package.

Sorting
- ​ Sorting is accessed using the icon beside the filters.
- ​ Available sorting options: Alphabetical (A–Z), Alphabetical (Z–A), Sport, Newly Added, Oldest Added.
- ​ Behaviour: The selected sorting option is saved and remains the default even after the application is closed, until changed by the coach.

Athlete Card Each athlete card displays:

- ​ Profile picture (or initials if no profile picture exists)
- ​ Athlete name
- ​ Sport

- ​ Package name
- ​ Sessions remaining (Active athletes only)
- ​ Active/Inactive status (Displayed only when the 'All' filter is selected)

Sport Display
- ​ The sport should be displayed as a small metadata label rather than a button, making it clear that it is informational only.

Card Behaviour
- ​ Selecting an athlete card opens the corresponding Athlete Profile.

Add / Invite Athlete
- ​ A floating Add Athlete (+) button is displayed in the bottom-right corner.
- ​ Selecting the button opens an Invite Athlete modal rather than a separate Create Athlete screen.
- ​ Athletes cannot create an account unless they have been invited by the Admin.

Invite Athlete Modal The modal allows the Admin to:

- ​   Enter the athlete's email address.
- ​   Select Send Invitation.
- ​   View a success confirmation that the invitation was emailed.

Invitation Behaviour
- ​ Each invitation is unique and can only be used to create an authorized athlete account.
- ​ The backend generates a single-use invitation code bound to the entered email address and emails it directly to that address.
- ​ The Admin can resend or revoke a pending invitation.
- ​ Invitation links are deferred. They may later open the same validation and account-creation flow without replacing invitation codes.
- ​ The athlete enters the emailed invitation code and then completes their own registration. A link may be added later as an alternative way to reach the same flow.
- ​ During account creation, the athlete establishes authentication using email/password or Google Sign-In. Required name and athlete profile information are provided afterward on Complete Your Profile.
- ​ The Admin therefore does not need to manually create the athlete's full account or enter all of their personal information.
- ​ Once registration is completed, the athlete appears in the Admin's Athlete List.
- ​ Unused or invalid invitation links/codes must not allow account creation.

### Bottom Navigation
- ​ Home
- ​ Athletes (Selected)
- ​ Schedule
- ​ More

### Future Improvements
- ​ Filter athletes by sport.
- ​ Filter athletes by package type.
- ​ Display profile photos instead of initials whenever available.

- ​ Display package expiry warnings directly on the athlete card.

## Athlete Profile
### Purpose
Allows the coach to view and manage the key information related to an individual athlete, including their package, payment status, assigned tasks, notes, and communication.

### Header
Displays:

- ​ Profile picture or initials

- ​ Athlete name

- ​ Sport

- ​ Account status: Active / Paused

Payment Status Payment status is displayed near the top of the profile, above the package progress.

Possible statuses:

Behaviour
- ​ The Admin can manually change the payment status between Pending and Paid.

- ​ This allows the Admin to confirm payment after receiving it through InstaPay.

- ​ Updating the payment status does not change the athlete's package/session balance.

- ​ Paid

- ​ Pending

Active Package Displays:

- ​ Package name

- ​ Number of completed sessions (e.g. 7 / 12 completed)

- ​ Number of sessions remaining

- ​ Progress bar

Behaviour
- ​ A session is deducted only after it has been attended.

- ​ When all sessions have been consumed, instead of displaying 0 sessions remaining, display: New sessions pending

Package History The profile displays only the athlete's most recent previous package to avoid cluttering the screen.

A View Package History option opens a separate view containing all previous packages.

Assigned To-Dos Displays the athlete's assigned To-Dos and their current completion status. Each To-Do displays:

- ​ To-Do title

- ​ Completion status: Pending / Completed

- ​ Due date, if one has been set

Behaviour
- ​ The Admin can view the status of each To-Do.

- ​ The athlete marks their own To-Dos as completed.

- ​ The Admin does not mark a To-Do as completed on behalf of the athlete.

- ​ When the athlete completes a To-Do, its status automatically updates for the Admin.

- ​ A + Add To-Do action is displayed within the Assigned To-Dos section.

- ​ Selecting + Add To-Do opens an Add To-Do modal.

- ​ A separate To-Do Management screen is not required.

Add To-Do Modal The modal contains:

- ​ Title — required

- ​ Description / Instructions — optional

- ​ Due Date — optional

- ​ Create To-Do button

Behaviour
- ​ The athlete does not need to be selected because the modal is opened directly from that athlete's profile.

- ​ Selecting Create To-Do assigns the To-Do to the athlete.

- ​ The new To-Do immediately appears in the athlete's Assigned To-Dos section.

- ​ The assigned To-Do also becomes visible in the athlete's own app.

- ​ The athlete should receive a notification when a new To-Do is assigned.

### Quick Actions
The profile provides access to:

- ​ Open Chat

- ​ Whiteboard & Notes

Open Chat Opens the private conversation between the admin and this athlete.

Whiteboard & Notes Provides access to the athlete's external whiteboard or notes resource.

The exact workflow and level of integration will be finalized during implementation.

- ​ This may initially function as a simple external link from the athlete's profile.

- ​ No separate in-app Whiteboard & Notes screen is required at this stage.

Account Management Account-level actions are accessed through the three-dot (•••) menu on the Athlete Profile.

Available action:

- ​ Pause Athlete — Temporarily disables the athlete's access to the application.

Pause Behaviour
- ​ Selecting Pause Athlete displays a confirmation message before any change is made.

- ​ Once paused, the athlete cannot log in or access their account.

- ​ The athlete's profile, package history, sessions, notes, To-Dos, and chat history remain stored and accessible to the Admin.

- ​ The athlete is shown as Inactive/Paused in the Admin interface.

- ​ When an athlete is paused, the action changes to Reactivate Athlete.

- ​ Selecting Reactivate Athlete restores the athlete's access to their existing account and data.

Schedule (Admin View)
### Purpose
- ​ Allows the coach to view all upcoming booked sessions in one place and quickly access session details.

### Header
Displays:

- ​ Screen title: Schedule

- ​ Open Calendly ↗ action in the top-right corner

Open Calendly Behaviour Selecting Open Calendly opens the coach’s Calendly account/app so they can manage:

- ​ Available days and times

- ​ Booking availability

- ​ Rescheduling settings

- ​ Cancellation settings

Calendly remains the main scheduling tool.

Calendar / Date Selector Displays the current week with:

- ​ Day names

- ​ Dates

- ​ Selected date highlighted

Behaviour Selecting a date updates the list of sessions shown below.

Session List Displays the booked sessions for the selected date.

Each session card shows:

- ​ Time

- ​ Athlete name

- ​ Session type: Online / Face-to-Face

The sport should not be shown here.

Session Card Behaviour Selecting a session card opens the Session Details screen.

Booking Behaviour Sessions are not manually created from this screen.

Bookings are made through Calendly and automatically appear in the app after synchronization.

The floating + button should therefore be removed.

### Bottom Navigation
- ​ Home

- ​ Athletes

- ​ Schedule (Selected)

- ​ More

Session Details (Admin View)
### Purpose
- ​ Allows the coach to review the details of a booked session, add notes, and update the session status after it takes place.

### Header
Displays:

- ​ Session time

- ​ Session date

Athlete Information Displays:

- ​ Athlete name

- ​ Sport

- ​ Session progress within the current package

(Example: Alex Thompson | Tennis • Session 7 of 12)

The package name is not shown on this screen.

Session Type Displays how the session is being delivered:

- ​ Online

- ​ Face-to-Face

- ​ Observation

Location / Platform The information shown depends on the session type:

- ​ Online: Meeting platform or meeting link

- ​ Face-to-Face: Physical location

- ​ Observation: Relevant location or event details

Session Status Possible statuses include:

- ​ Scheduled

- ​ Attended

- ​ Cancelled

Coach Session Notes The coach can add or edit notes related specifically to this session. These notes become part of the athlete’s overall Whiteboard & Notes history.

Mark as Attended The main action button is: Mark as Attended.

Behaviour When selected:

- ​ Session status changes from Scheduled to Attended.

- ​ One session is deducted from the athlete’s active package.

- ​ Package progress is updated.

- ​ The action must not deduct the same session more than once.

Reschedule Selecting Reschedule opens the relevant Calendly rescheduling flow. Once changed in Calendly, the new session details are synchronized back into the app.

Cancel Session Selecting Cancel Session opens the Calendly cancellation flow. Once cancelled, the session status is updated in the app. Cancelled sessions do not consume a package session.

Notifications (Admin View)
### Purpose
- ​ Provides the Admin with one place to view important updates and activity across the platform.

Notification Types
- ​ New session booked

- ​ Session reminder

- ​ Session cancelled/rescheduled

- ​ New payment / payment update

- ​ Package completed / renewal required

- ​ New message

- ​ To-do completed

- ​ Other relevant athlete or account alerts

Screen Content Notifications are displayed chronologically and grouped into:

- ​ Today

- ​ Earlier

Each notification displays:

- ​ Notification type/icon

- ​ Short title

- ​ Brief description

- ​ Time/date

- ​ Unread indicator

Interactions
- ​ Tap notification: Opens the related screen or item (e.g. session details, athlete profile, payment, chat).

- ​ Three-dot menu:

○​ Mark all as read

○​ Notification settings

○​ Clear all notifications

- ​ Notification Settings: Opens the Admin Settings screen where push and email notification preferences can be managed.

- ​ Clear All: Requires confirmation before notifications are removed.

Navigation
- ​ Accessible from More → Notifications and from relevant notification indicators/badges elsewhere in the app.

Settings (Admin View)
### Purpose
- ​ Allows the Admin to manage their account, notification preferences, package options, and general app preferences.

Screen Sections

Account
- ​ Edit Profile — Opens the Admin Profile screen where personal and contact information can be updated.

- ​ Change Password — Allows the Admin to securely change their password.

Notifications
- ​ Push Notifications — Enable or disable push notifications.

- ​ Email Notifications — Enable or disable email notifications.

- ​ Session Reminders — Enable or disable upcoming session reminders.

Business
- ​ Packages — Opens the Package Options screen where the Admin can:

○​ View available package options

○​ Create a new package

○​ Set the number of sessions

○​ Set or change the package price

○​ Edit existing package options

○​ Activate/deactivate package options while preserving previous package history

App
- ​ Appearance — Select the app appearance (e.g. Light/Dark).

- ​ Language — Select the app language.

- ​ Help & Support — Access help and support information.

Danger Zone
- ​ Log Out — Signs the Admin out of the application. A confirmation prompt should appear before logging out.

Navigation
- ​ Accessible through More → Settings.

More Menu (Admin View)
### Purpose
- ​ Provides quick access to secondary Admin features that do not need a permanent place in the main bottom navigation.

Behaviour
- ​ Tapping More in the bottom navigation opens a side drawer from the right.

- ​ The current screen remains visible but dimmed in the background.

- ​ The drawer can be closed using the close button or by tapping outside the drawer.

Menu Items

More
- ​ Notifications

○​ Opens the Admin Notifications screen.

○​ Displays an unread indicator when new notifications are available.

- ​ Packages

○​ Opens Package Management.

○​ Allows the Admin to view, add, edit, activate, and deactivate package options and adjust their number of sessions and prices.

Account
- ​ My Profile

○​ Opens the Admin My Profile screen.

Bottom Menu
- ​ Settings

○​ Opens the Admin Settings screen.

Admin Bottom Navigation The main Admin navigation contains four items:

- ​ Home → Admin Dashboard

- ​ Athletes → Athlete List

- ​ Schedule → Admin Schedule

- ​ More → Opens the More side drawer

Additional Navigation The Admin Dashboard also includes:

- ​ Notification bell → Notifications

- ​ Profile icon/avatar → My Profile

My Profile (Admin View)
### Purpose
- ​ Allows the Admin to view and manage their personal account information.

Content
- ​ Profile picture

- ​ Full name

- ​ Professional title/role

- ​ Email address

- ​ Phone number

- ​ Edit Profile button

Interactions
- ​ Edit Profile — Opens the Edit Profile screen where the Admin can update:

○​ Profile picture

○​ Full name

○​ Professional title

○​ Email address

○​ Phone number

- ​ Three-dot menu — Provides relevant account actions if needed.

Design Notes
- ​ The profile is private and only visible to the Admin.

- ​ Coaching statistics, certifications, specializations, and other promotional information are not required.

- ​ The screen should remain simple and spacious rather than adding unnecessary information.

Package Management (Admin View)
### Purpose
- ​ Allows the Admin to create and manage the package options offered to athletes.

Screen Content Each package displays:

- ​ Package name

- ​ Number of sessions

- ​ Price

- ​ Status — Active / Inactive

- ​ Edit button

Actions

Add New Package
- ​ Selecting + Add New Package opens an Add Package modal over the Package Management screen.

- ​ The Admin enters:

○​ Package name

○​ Number of sessions

○​ Price

- ​ New packages are Active by default.

- ​ Selecting Add Package creates the package and adds it to the list.

- ​ Selecting Cancel closes the modal without saving.

- ​ A separate Add Package screen is not required.

Edit Package
- ​ Selecting Edit places the selected package card into a focused editing state.

- ​ The selected package is displayed prominently while the rest of the screen is dimmed/blurred.

- ​ The Admin can change:

○​ Package name

○​ Number of sessions

○​ Price

○​ Active/Inactive status

- ​ Selecting Save Changes updates the package.

- ​ Selecting Cancel returns to Package Management without saving changes.

- ​ A separate Edit Package screen is not required.

Business Rules
- ​ Active packages can be selected when assigning or renewing an athlete's package.

- ​ Inactive packages cannot be assigned to athletes.

- ​ Deactivating a package does not affect athletes who previously purchased it or their package history.

- ​ Existing historical package records retain the package details that applied when they were purchased.

- ​ Package options can be changed without deleting previous package history.

Navigation Accessible through:

- ​ More → Packages

The back button returns to the previous screen.

Authentication & Athlete Onboarding Splash Screen

### Purpose
- ​ Provides a simple branded entry point while the application loads and determines the user's authentication status.

Screen Content
- ​ Beyond Movement logo

- ​ Beyond Movement name

- ​ Mental Performance subtitle

- ​ Brand tagline

Behaviour
- ​ The screen is displayed briefly when the application launches.

- ​ If the user is already authenticated, they are taken directly to the appropriate Home screen.

- ​ If the user is not authenticated, they are taken to the Login screen.

Login

### Purpose
- ​ Allows existing Admin and Athlete users to securely access their accounts.

Screen Content
- ​ Email address

- ​ Password

- ​ Show/hide password control

- ​ Sign In button

- ​ Continue with Google option

- ​ Forgot Password?

- ​ Invitation guidance for new athletes with an Enter invitation code text action

Behaviour
- ​ Users can sign in using their registered email address and password or Google Sign-In.

- ​ Successful authentication directs the user to the appropriate experience according to their account role.

- ​ There is no public Sign Up option.

- ​ New athletes must receive an invitation from the Admin before creating an account.

New Athlete Message
- ​ Display: New athlete? Enter invitation code.

- ​ Selecting Enter invitation code opens a dedicated Enter Invitation Code screen. It is not presented as a modal so keyboard, loading, and invitation error states have sufficient space.

Forgot Password

### Purpose
- ​ Allows an existing user to request a password reset.

Screen Content
- ​ Email address field

- ​ Send Reset Link button

- ​ Back to Login

Behaviour
- ​ The user enters the email address associated with their account.

- ​ Selecting Send Reset Link sends password-reset instructions to the registered email address.

- ​ The user can return to Login using Back to Login.

Invitation & Account Creation

### Purpose
- ​ Allows an invited athlete to create their account.

- ​ Athlete registration is invitation-only.

Access
- ​ The athlete selects Enter invitation code from Login and enters the code delivered to their invited email address.

- ​ Users cannot access normal athlete registration without a valid invitation.

Enter Invitation Code Screen
- ​ Invitation code field
- ​ Continue button
- ​ Back to Login
- ​ Loading state while the backend validates the code
- ​ Clear invalid, expired, already-used, and revoked invitation states

Validation Behaviour
- ​ Successful backend validation confirms that the code is valid and verifies the email address to which the backend delivered it.
- ​ Validation does not consume the invitation; the invitation is redeemed only after account creation succeeds.
- ​ Successful validation continues to Create Account with the verified email shown as read-only.

Screen Content
- ​ Welcome message indicating that the coach has invited the athlete

- ​ Verified email address (read-only)

- ​ Password

- ​ Confirm password

- ​ Show/hide password controls

- ​ Continue with Google option

- ​ Terms of Service and Privacy Policy agreement

- ​ Create Account button

Behaviour
- ​ The verified invitation email is pre-filled and cannot be edited.

- ​ The athlete can create their account using either: Email and password or Google Sign-In.

- ​ Email/password registration requires Password and Confirm password.

- ​ Google registration does not require a password or name on this screen.

- ​ Google Sign-In does not bypass the invitation requirement, and the Google account email must exactly match the verified invitation email.

- ​ The athlete must accept the Terms of Service and Privacy Policy before creating an account.

- ​ Once authentication setup is complete, the athlete proceeds to Complete Your Profile.

Complete Your Profile

### Purpose
- ​ Collects the athlete-specific information required by the platform without overloading the initial account-creation process.

Screen Content

Full Name
- ​ Required

- ​ If Google provides a display name, it may be pre-filled but remains editable and must be confirmed by the athlete.

Profile Picture
- ​ Optional

- ​ Athlete can upload a profile photo.

- ​ If no photo is uploaded, initials can be displayed throughout the application.

Date of Birth
- ​ Required

- ​ Selected using a date picker.

Gender
- ​ Required

- ​ Selection options: Female, Male, Prefer not to say.

Sport
- ​ Required

- ​ Searchable/text-entry field to accommodate different sports without requiring a fixed list.

Primary Action
- ​ Finish Setup

Behaviour
- ​ Required information must be completed before finishing profile setup.

- ​ Profile picture can be skipped and added later.

- ​ Selecting Finish Setup saves the athlete's profile information.

- ​ After successful completion, the athlete is taken to Athlete Home.

Athlete Onboarding Flow
- ​ Invitation code received by email → Login: Enter invitation code → Validate code and verify email → Create Account with password or matching Google account → Complete Profile, including full name → Athlete Home

General Rules
- ​ Athlete registration is invitation-only.

- ​ Authentication and athlete profile setup are treated as separate steps.

- ​ Both email/password and Google authentication are supported.

- ​ Regardless of authentication method, new athletes complete the same profile setup process.

- ​ No username is created. Email is the login identifier.

- ​ A Google-created account can use Forgot Password to set its first local password if the user still controls the verified email inbox. The user can then sign in with either Google or email/password.

- ​ Profile information can later be viewed and, where permitted, updated from the athlete's Profile.

- ​ A paused athlete cannot access their account until the Admin reactivates it.

Admin Authentication
### Purpose
The Admin uses the same authentication screens as the Athlete. No additional Admin authentication screens are required.

Behaviour
- ​ The Admin account is created during the initial system setup and is not created through the public app interface.

- ​ There is no Admin Sign Up screen.

- ​ The Admin uses the shared Login screen.

- ​ The Admin can sign in using:

○​ Email and password

○​ Google Sign-In

- ​ The shared Forgot Password flow is also used by the Admin.

- ​ After successful login, the system automatically identifies the user's role.

○​ Admin accounts → Admin Home

○​ Athlete accounts → Athlete Home

- ​ The Admin does not require an invitation.

- ​ The Admin does not go through the Athlete Complete Your Profile onboarding flow.

Design Decision
- ​ Login and password recovery screens are shared between both user roles.

- ​ No separate Admin authentication screens need to be designed in Figma.
