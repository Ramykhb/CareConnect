# CareConnect

Two independent ASP.NET Core MVC applications targeting .NET 10, using the standard MVC project structure. Business services, repositories, persistence, and messaging live in folders within each application rather than separate layer projects.

```text
CareConnect/
├── CareConnect.sln
├── src/
│   ├── PetSitting/
│   │   ├── Controllers/
│   │   ├── Models/
│   │   ├── Views/
│   │   ├── wwwroot/
│   │   ├── Properties/
│   │   ├── Data/                 # Application-owned EF Core context and migrations
│   │   ├── Services/             # Business services; messaging configuration
│   │   ├── Repositories/
│   │   ├── Program.cs
│   │   ├── appsettings.json
│   │   └── PetSitting.csproj
│   ├── BabySitting/              # Same MVC structure
│   └── CareConnect.Contracts/    # Shared event metadata and future message contracts
├── tests/
│   ├── PetSitting.Tests/
│   └── BabySitting.Tests/
└── docker-compose.yml
```

## Run

Install the .NET SDK specified in `global.json`. From the solution directory:

```sh
dotnet restore
dotnet build
dotnet test
```

Start each application in its own terminal:

```sh
dotnet run --project src/PetSitting
```

```sh
dotnet run --project src/BabySitting
```

PetSitting: http://localhost:5101. BabySitting: http://localhost:5102.
Both provide `/health/live`. This endpoint checks application liveness only.
The landing and privacy pages work without SQL Server or RabbitMQ running.

## SQL Server and migrations

Both applications use the existing SQL Server at `localhost:1433`, with independent databases and Identity tables:

| Application | Database | Context |
| --- | --- | --- |
| PetSitting | `CareConnect_PetSitting` | `PetSittingDbContext` |
| BabySitting | `CareConnect_BabySitting` | `BabySittingDbContext` |

Local connection strings, including passwords, are saved in each project's .NET user secrets. Checked-in settings contain no password. To configure a different machine:

```sh
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=localhost,1433;Database=CareConnect_PetSitting;User Id=sa;Password=<your-password>;Encrypt=True;TrustServerCertificate=True" --project src/PetSitting
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=localhost,1433;Database=CareConnect_BabySitting;User Id=sa;Password=<your-password>;Encrypt=True;TrustServerCertificate=True" --project src/BabySitting
```

Restore EF tools and apply migrations independently:

```sh
dotnet tool restore
dotnet ef database update --project src/PetSitting -- --environment Development
dotnet ef database update --project src/BabySitting -- --environment Development
```

For a subsequent model change, generate a named migration in the affected application, then apply it:

```sh
dotnet ef migrations add YourChangeName --project src/PetSitting --output-dir Data/Migrations -- --environment Development
dotnet ef database update --project src/PetSitting -- --environment Development
```

Migrations are committed under each project's `Data/Migrations`. Applications do not automatically create or migrate databases on startup. The SQL administrator account and development certificate settings are for local setup; provision restricted database users before deployment.

## Models

Each application owns its `ApplicationUser` based on ASP.NET Core Identity with GUID keys. Profiles, requests, offers, and bookings are local entities; nothing references the other application's tables.

| PetSitting models | BabySitting models |
| --- | --- |
| Pet, PetSitterProfile | FamilyProfile, BabysitterProfile |
| PetCareRequest, PetCareRequestPet | ChildcareRequest |
| PetCareOffer, PetBooking | BabysittingOffer, BabysittingBooking |
| SitterService, SitterAvailability | SitterService, SitterAvailability |
| Payment, Review, Notification | Payment, Review, Notification |
| MessageOutbox, ProcessedMessage | MessageOutbox, ProcessedMessage |

Requests belong to customers and receive sitter offers; bookings reference the request, selected offer, customer, and sitter. Pets link to pet requests through a join table. Family profiles have one local parent account. A booking has payment attempts and at most one customer review. Sitter services and availability belong to a profile. Babysitter verification is stored independently from pet sitter verification.

Entities use UTC timestamps, SQL Server row-version concurrency tokens, explicit foreign keys, fixed monetary precision, indexes, and database check constraints for core amounts, dates, status ranges, and ratings. Unique constraints protect payment idempotency, processed event IDs per consumer, external request links, one offer per sitter/request, and one active booking per request. Deletes require deliberate handling of dependent records rather than cascading through booking/payment history.

Combined care uses explicit one-time invitation codes to link customer-owned requests in the two apps. Each customer signs in separately and supplies local pet or family details; the broker exchanges coordination state only. Invitations waiting to be claimed do not create actionable, ownerless requests. Childcare data contains child counts, age ranges, and essential care instructions without child names, birth dates, or school information; access controls must precede exposing this data through any future endpoints.

Both apps implement Identity authentication and roles, pet/family profiles, request drafts and submission, sitter decisions, bookings, cancellation, demo payments, customer reviews and sitter replies. Sitter profiles, services/pricing, availability, care checklists, private photo updates, SignalR booking chat, notifications, analytics and exports are implemented. Payout requests reserve balances locally; bank transfers and administrative payout processing are not connected.

## RabbitMQ

The main Docker Compose file manages RabbitMQ, reusing your existing SQL Server. The separate authentication Compose file runs Mailpit for local confirmation and recovery email. Copy `.env.example` to `.env`, set RabbitMQ credentials, and run:

```sh
docker compose up -d rabbitmq
```

AMQP uses port 5672; management is at http://localhost:15672. Set `RabbitMq:UserName`, `RabbitMq:Password` and `RabbitMq:Enabled` in both projects as shown below. `.env` is read by Compose, not automatically by `dotnet run`.

## Architecture and phased development

Each MVC app owns its users, database, migrations, services, and repositories. Controllers delegate business operations to services. Applications must never access each other's databases or call each other's APIs. RabbitMQ is the sole cross-application transport. Shared contracts contain message data only, with no shared persistence or business entities.

Implemented local relationships: users own pets or family profiles and care requests; sitters submit offers on requests; an accepted offer creates a booking; bookings own payments and reviews. Availability belongs to a sitter. Outbox and processed-message records belong to the local application. Cross-platform links use external request IDs and correlation IDs, never cross-database foreign keys. Child information is restricted to essential local data.

Implemented topic exchange: `careconnect.events`. Durable quorum queues `petsitting.events` and `babysitting.events` receive the other platform's `*.combined.#` events. Both queues are declared by either app, so starting one app while the other is offline does not lose messages. Each queue has `.retry` (10-second delay with at-least-once dead lettering) and `.dead` queues. Quorum queues have delivery limits disabled on the main queues because the application handles retry limits explicitly.

A shared transport library has no persistence or business entities. Each app implements its own integration store. A lifecycle change and its outgoing event are saved in the same database transaction. The hosted worker uses persistent messages, mandatory routing and publisher confirms; it marks an outbox row published only after confirmation. Failed outgoing delivery backs off up to five minutes and keeps retrying. Consumers acknowledge only after the local transaction commits; processed event IDs make redelivery harmless. Per-link revisions prevent older events from overwriting newer states. Transient incoming failures retry five times before dead-lettering; invalid JSON, versions, sources or contracts go directly to `.dead`. Broker outages never undo a saved local request.

### Enable messaging

Use the same credentials as `.env` (replace the example values; do not include angle brackets):

```sh
dotnet user-secrets set "RabbitMq:UserName" "careconnect" --project src/PetSitting
dotnet user-secrets set "RabbitMq:Password" "your-rabbitmq-password" --project src/PetSitting
dotnet user-secrets set "RabbitMq:Enabled" "true" --project src/PetSitting
dotnet user-secrets set "RabbitMq:UserName" "careconnect" --project src/BabySitting
dotnet user-secrets set "RabbitMq:Password" "your-rabbitmq-password" --project src/BabySitting
dotnet user-secrets set "RabbitMq:Enabled" "true" --project src/BabySitting
dotnet ef database update --project src/PetSitting
dotnet ef database update --project src/BabySitting
```

Set `CombinedCare:PeerOrigin` to the other app’s public origin when deploying (local defaults use ports 5101/5102); this is a browser navigation link, not an API call. Restart both apps. `RabbitMq:Enabled` defaults to false so ordinary local workflows still start without a broker. Invalid enabled configurations fail startup. `RabbitMq:UseTls=true` enables certificate-validated TLS with the configured hostname and port. `/health/live` reports process liveness; `/health/ready` returns 503 while enabled messaging is disconnected. The Combined care page displays queued outgoing updates and the timestamp of the last received peer update.

Existing manually created classic queues with these names must be drained and migrated before deploying the quorum topology; RabbitMQ will reject a queue type mismatch. Broker management remains bound to localhost in Compose. In production use private networking, TLS, persistent broker volumes, protected user secrets and persistent ASP.NET Data Protection keys. Invitation codes are stored encrypted locally; only their SHA-256 hash travels through the broker. Broker messages contain request IDs, correlation IDs, revisions and statuses, with no email, child/pet records, care notes or addresses.

### Use combined care

1. Submit your pet or baby request normally. Choose **Add baby/pet care** on its confirmation page, or **Combined care** in the customer sidebar.
2. Select the request and generate a one-time code. Sign in as a customer in the other app, create the other care request, open **Combined care** there and paste the code while selecting that request. You choose each service's dates and budget separately. An invitation may take a few seconds to arrive.
3. Sitters see a Combined care badge on linked requests, the other service’s latest status in request details, and whether the link is still pending. These indicators update through the existing inbox polling. Each app continues its own sitter selection, chat, payment and care workflow. The Combined care page shows both request and booking states; refresh it to see updates. One service can be booked while the other is still seeking a sitter. No booking or payment is implied for the other app.
4. Cancelling a customer request synchronizes cancellation to the other request and any booking that has not started, withdraws pending offers, notifies the local customer/sitter, and updates dummy refund records. If the other service is already in progress or completed, it stays intact and displays a resolution notice. A sitter withdrawing reopens only that service's request. Account closure also publishes cancellation of open linked requests.

Codes expire after 24 hours and can be used for only one request pair. The originating customer can select the same request and generate again to recover the existing code, extending it if it expired. Keep the code private: possession authorizes linking, without requiring accounts in the two apps to share an email address. Replaying an already accepted invitation cannot reassign its request. Local request ownership and customer-role authorization apply to both actions, with antiforgery protection.

### Recover dead letters and verify

Inspect `.dead` in RabbitMQ management after correcting the underlying problem. Replay up to 100 messages per invocation with confirmed publication before removal from the dead-letter queue:

```sh
dotnet run --project src/PetSitting -- --replay-dead-letters
dotnet run --project src/BabySitting -- --replay-dead-letters
```

Replays preserve event IDs and correlation IDs; duplicate effects are suppressed by the inbox. Retain outbox/inbox records until an explicit retention policy accounts for the maximum replay window. A single-node development broker has durable storage but no node-level high availability; production quorum replication requires a broker cluster.

The focused end-to-end test uses both real SQL databases and an **isolated** RabbitMQ instance (it declares the normal queue names). It inserts and cleans up its own records, and covers committed delivery, rolled-back outbox writes, invitation/linking, booking status, cancellation/refund, and duplicate/stale replay:

```sh
CARECONNECT_MESSAGING_TESTS=1 \
CARECONNECT_RABBITMQ_USER=careconnect \
CARECONNECT_RABBITMQ_PASSWORD='your-test-broker-password' \
CARECONNECT_RABBITMQ_PORT=5673 \
dotnet test tests/CareConnect.Messaging.Tests
```

1. Foundation: solution, default MVC folders, layouts, EF Core configuration, infrastructure configuration, independent startup checks.
2. Identity: registration/login, roles, authorization, and profile management using the migrated Identity tables.
3. Pet sitting: pets, sitter profiles, requests, offers, bookings.
4. Babysitting: family details, sitter profiles, requests, offers, bookings.
5. Messaging: versioned payloads, publishers, hosted consumers, topology and reliability.
6. Combined care: linked requests, independent booking states, partial success, cancellation.
7. Advanced features: SignalR, dummy payments, AI matching, reporting.
8. Quality assurance: business and integration tests, messaging reliability, deployment verification.

Verification is available through the application test projects. Database constraint tests roll back; workflow and authentication tests clean up their temporary records. SQL-backed tests require `CARECONNECT_SQL_TESTS=1`, accessible local databases, and initialized roles. Focused checks cover chat ownership and read receipts, request submission, Google authentication, and committed notification delivery and preferences. Run commands below rather than relying on a fixed historical test count.

Run route/configuration checks with `dotnet test`. To include the actual SQL Server checks using local user secrets:

```sh
CARECONNECT_SQL_TESTS=1 dotnet test CareConnect.sln
```

RabbitMQ delivery and combined-care coordination are implemented. External AI integration and manager operations remain future work. Local care workflows continue independently when messaging is disabled or unavailable.

## Authentication

Both applications use ASP.NET Core Identity and its built-in Razor account UI, with local customizations under `Areas/Identity/Pages`. Each application has independent users, roles, cookies, antiforgery cookies, two-factor cookies, and data-protection application names. The MVC folder structure is preserved.

### Local setup

SQL Server must be available on port 1433 with each connection string in user secrets. Start the local email inbox:

```sh
docker compose -f docker-compose.auth.yml up -d
```

Apply migrations (commands above), then initialize roles in each database once:

```sh
dotnet run --project src/PetSitting -- --initialize-identity
dotnet run --project src/BabySitting -- --initialize-identity
```

These commands exit after setup. Start both MVC apps normally with `dotnet run --project src/PetSitting` and `dotnet run --project src/BabySitting`. Register using the navigation links, open the confirmation email in [Mailpit](http://localhost:8025), confirm the account, and log in. Mailpit captures local email without delivering to external recipients. SMTP listens on localhost:1025.

Registration requires exactly one account type: customer or sitter. Accounts cannot hold multiple platform roles; a unique index on `AspNetUserRoles.UserId` enforces this in SQL Server, including concurrent assignments. Manager accounts have their own single role. PetSitting uses PetOwner/PetSitter; BabySitting uses Parent/Babysitter. Public registration cannot assign PlatformManager. A sitter role does not constitute professional verification; verification remains a separate profile field.

### Account features

- `/Identity/Account/Register`: first/last name, email, password, allowed role choice; user and roles saved atomically.
- `/Identity/Account/Login`: email/password, remember me, confirmed email requirement, five-attempt lockout for 15 minutes, and authenticator challenge when enabled.
- `/Identity/Account/Logout`: POST with antiforgery protection.
- `/Identity/Account/ResendEmailConfirmation` and `/Identity/Account/ForgotPassword`: email confirmation and recovery.
- `/Identity/Account/Manage`: edit name, city, and phone; change password/email; enable authenticator, manage recovery codes, export personal data, and delete an unused account after password verification.
- `/Dashboard`: authenticated account hub; Customer/Sitter/Manager subpages enforce their respective roles. Both apps display their customer and sitter dashboards. Manager pages remain access-controlled placeholders.

Use an authenticator app's manual time-based setup with the secret shown on the setup page. Save recovery codes privately. Accounts linked to care records cannot be hard-deleted through self-service; the form explains that a platform manager must handle account closure while retaining those records.

Passwords require at least 12 characters, uppercase/lowercase, a digit, a symbol, and four distinct characters. Confirmation and reset tokens expire after two hours. Password/security-stamp changes and deactivation invalidate stale sessions on the next request. Auth POSTs are limited to 30 per minute per remote IP per application. The two-factor setup customization refreshes the current session after changing its security stamp.

### Provision a platform manager

Manager creation is an explicit local CLI operation. Choose a new email and a strong password; store them in that application's user secrets:

```sh
dotnet user-secrets set "BootstrapAdmin:Email" "<manager-email>" --project src/PetSitting
dotnet user-secrets set "BootstrapAdmin:Password" "<strong-password>" --project src/PetSitting
dotnet run --project src/PetSitting -- --initialize-identity
dotnet user-secrets remove "BootstrapAdmin:Password" --project src/PetSitting
dotnet user-secrets remove "BootstrapAdmin:Email" --project src/PetSitting
```

Repeat with `src/BabySitting` for its independent manager. There are no default manager credentials. Provisioning refuses to promote an existing public account and never resets an existing manager's password. The operator-provisioned manager email is marked confirmed; enable two-factor authentication after first login.

### Deployment configuration

### Real email through Brevo SMTP

Brevo SMTP uses an **SMTP key**, not a Brevo API key. In Brevo, open **Settings → SMTP & API → SMTP**, copy your SMTP login and generate an SMTP key. Use the email address of your verified/trusted sender as `Email:From`; it can differ from the SMTP login.

From the repository root, run:

```sh
python3 scripts/configure_brevo.py
```

On Windows, use `py scripts/configure_brevo.py` if `python3` is unavailable. Enter your SMTP login, verified sender address, and SMTP key when prompted. The key is hidden while typing and sent to the .NET CLI through stdin. The script saves only Email settings to **both apps' user secrets**, preserving other configuration. It does not send an email or put keys in source control. Restart both apps, then register a test account or resend a verification email and check your actual inbox (and spam folder). Inspect Brevo transactional logs if delivery is rejected.

The saved configuration is:

| User-secret key | Value |
| --- | --- |
| `Email:Provider` | `Brevo` |
| `Email:Host` | `smtp-relay.brevo.com` |
| `Email:Port` | `587` |
| `Email:EnableSsl` | `true` (STARTTLS) |
| `Email:UserName` | Your Brevo SMTP login |
| `Email:Password` | Your **SMTP key** |
| `Email:From` | Your verified sender email |
| `Email:FromName` | `CareConnect` |
| `Email:InboxUrl` | Empty; recipients use their own inbox |

There is no API-key setting for this SMTP integration. To replace an SMTP key later, rerun the setup script or update `Email:Password` in each project's user secrets. Selecting the Brevo provider enforces its host, port and TLS settings, requires SMTP credentials, and rejects API keys beginning with `xkeysib-`. Real SMTP delivery hides the development Mailpit inbox link. Registration, verification/resend, password recovery and Identity email changes use this sender; in-app booking notifications are separate.

For deployment, supply these values through the environment/secret store (`Email__Password`, `Email__UserName`, etc.), and configure each `Auth:PublicOrigin` to its public URL so recipients on other devices can open confirmation/reset links. Local user secrets are per machine and are not shared by Git.

References: [Brevo SMTP integration](https://developers.brevo.com/docs/smtp-integration), [SMTP key setup](https://help.brevo.com/hc/en-us/articles/7959631848850-Create-and-manage-your-SMTP-keys).

Outside Development, configure `Auth:PublicOrigin` as the application's public HTTPS origin, `Email:Host`, `Email:Port`, `Email:From`, `Email:EnableSsl=true`, and SMTP credentials (`Email:UserName`/`Email:Password`) through your deployment secret store. The public origin is used for Identity email links rather than trusting incoming Host headers. Configure each app separately.

Set `DataProtection:KeysPath` to a protected persistent directory mounted into the container, retain those keys across deployments, and protect them at rest. Replicas of the same app need the same key ring; the two applications must keep their distinct application names. Production cookies require HTTPS. Configure trusted proxy handling for your actual hosting topology before deploying behind a reverse proxy. Local SMTP was tested; delivery through a production provider requires that provider's credentials and deployment verification.

SQL authentication tests use unique temporary accounts and a test email sender. They exercise forbidden account types and database role exclusivity, registration-role tampering, email confirmation, restricted dashboards, profile updates, password change/reset and token replay, inactive sessions, authenticator setup/login, recovery-code login, lockout, and logout CSRF protection. Run them with `CARECONNECT_SQL_TESTS=1 dotnet test` after applying migrations and initializing roles.

### Password recovery UI

Both applications have custom forgot-password and inbox-confirmation pages sharing their own full-height CareConnect auth layout. They preserve Identity reset tokens and the two-hour configured lifetime. Confirmation uses the same wording for unknown, unconfirmed, inactive, and eligible accounts.

Resend requests have a 45-second per-email cooldown enforced on the server, including concurrent requests. The cooldown is kept in memory per application instance; the existing per-IP auth rate limit remains active. The browser shows the remaining time and enables resend when it finishes. Recovery state is held in the protected TempData cookie rather than an email-address query parameter.

In Development, `Email:InboxUrl` points to Mailpit at `http://localhost:8025`. A deployment can configure its own HTTPS inbox URL, or leave it unset to show a return-to-login button. Reset and resend continue to require antiforgery validation. No schema changes are required.

### BabySitting authentication theme

BabySitting's login, registration, forgot-password, and inbox-confirmation screens match the CareConnect typography, lime accents, rounded controls, and full-height layout used by PetSitting. BabySitting has its own family illustration, caregiver/child and toy-rabbit artwork, family icon, and copy. Registration uses the existing Parent or Babysitter role choices; no child information or new profile fields are collected on these pages.

Fonts, CSS, JavaScript, and SVG assets live inside BabySitting so it remains independently runnable. Its password-reset service, resend cooldown, SMTP sender, cookies, and Identity database are local to that application. Its babysitter registration link preselects the sitter role, and existing role validation still rejects multiple-role registration. No migrations are required for this UI update.

### PetSitting dashboards

`/Dashboard` redirects pet owners to `/Dashboard/Customer` and pet sitters to `/Dashboard/Sitter`. Both pages use the CareConnect portal theme from the supplied dashboard designs, including responsive navigation, lime metric cards, charts, booking details, notifications, and empty states. Existing pet CRUD and account links remain available.

Dashboard data comes from the signed-in account’s database records. Owners see their pets, requests, bookings, payments, and available sitters in their city. Sitters see their bookings and open requests in their profile/account city, filtered by configured services and excluding requests they have already responded to. Request addresses are not displayed on the dashboard. Dates use Beirut time. Analytics show the current calendar year and received/succeeded USD payments; individual payment and booking rows retain their recorded currencies.

Tabs and sidebar links open the dashboard sections or dedicated workflow pages. Search filters the current view, booking rows open details, and reports support CSV plus PDF/XLSX on sitter analytics pages. Request creation, profile/pricing updates, booking chat, customer reviews, demo payments and payout reservations have local backend workflows. Availability, care updates and request decisions use their dedicated sitter pages. AI matching, real payment charges, bank transfers and manager processing remain unimplemented. Dashboard shortcuts link to the implemented pages.

Dashboard HTTP tests cover role redirects, access denial, and rendering populated/empty accounts without external services. The SQL-backed scoping check runs with `CARECONNECT_SQL_TESTS=1`. For local visual QA, `CARECONNECT_DASHBOARD_QA=/tmp/careconnect-dashboard-qa dotnet test tests/PetSitting.Tests --filter Dashboard_renders` writes HTML using test-only fixtures; the running application never uses this fixture data.

### Sitter incoming requests

Open **Requests** in the sitter sidebar or visit `/SitterRequests`. The page implements the incoming inbox, acceptance confirmation, and saved decision designs. Status tabs and owner/pet search work through normal GET links/forms and are enhanced with partial-page updates. The inbox polls every 15 seconds while visible, pauses during confirmation, and shows a toast when new eligible requests arrive. It reports connection failures instead of claiming a successful sync.

Requests are discovered using the sitter’s profile/account city and configured services; this application currently has a city-based request marketplace rather than individually addressed invitations. An existing sitter profile is required to respond. New and pending requests can be accepted or declined, while previous decisions remain visible in their status tabs. Pending rows correspond to existing pending offer records. Exact service addresses are only included in the response after that sitter has accepted; medical, feeding, and special-care notes are available for review beforehand. Fit checks use actual bookings, saved availability intervals, city, and hourly rate; no distance, pet-type preference, owner rating, or response deadline is invented.

`POST /SitterRequests/Decide` requires the sitter role, an antiforgery token, and the displayed request/offer version. Acceptance records an accepted offer, marks the request booked, creates an awaiting-payment booking (confirmed immediately for zero-cost care), and saves an owner notification in one serializable transaction. Notifications are persisted locally; linked requests also enqueue a RabbitMQ state event in the booking transaction. This does not send email. Decline records the sitter’s rejected offer and notifies the owner while leaving the request open for other sitters. Stale details, expired requests, unavailable sitters, dates outside saved availability, and overlapping bookings cannot be accepted. Retries return the existing decision without creating duplicate bookings or notifications. The saved state includes an Open booking link to the dedicated sitter booking details page.

No new schema migration is required. Ensure the existing `AddPetCareDetails` migration is applied so medical and feeding notes can be read. SQL integration tests create uniquely identified temporary records and clean them up. Run `CARECONNECT_SQL_TESTS=1 dotnet test tests/PetSitting.Tests` to verify persistence, stale confirmations, private address access, rollback after a write failure, simultaneous acceptance by different sitters, and simultaneous overlapping bookings for one sitter.

### Sitter bookings and availability

The sitter sidebar now opens `/SitterBookings` and `/SitterAvailability`. Bookings have Upcoming, In progress, Accepted, Completed, and Cancelled filters, owner/pet search, a weekly summary, and a CSV export that neutralizes spreadsheet formula fields. Each booking opens `/SitterBookings/Details/{id}` with an acceptance/start/completion timeline, owner contact details when provided, payment records, a care checklist, photo updates, and issue reporting. Records are restricted to the assigned sitter; a different sitter cannot read or change the booking.

Care can start only at or after the scheduled start and after successful payments in the booking currency cover its full amount (zero-cost bookings require no payment). Start and completion timestamps and owner notifications are committed together. Repeated status submissions do not create duplicate notifications. Completion also completes the care request. Owner instructions provide the checklist’s initial items, without inventing medication doses or care times. Sitters can add tasks and optional due times within the stay, and update checkboxes while care is in progress. Care updates and issue reports are saved with owner notifications. Owners can read these notes and photos in their dashboard’s booking details. Photos accept JPEG/PNG/WebP up to 5 MB, are stored outside public static assets in the database, and are served only to the booking owner or assigned sitter. No veterinarian contact, service fee, or payout is fabricated.

The availability page supports month and week navigation, Today, date selection, private reasons, all-day/hour ranges, yearly recurrence, and a global accepting-requests preference. In week view, drag within a day to choose hours and review them in the editor before saving; day headings and keyboard Enter/Space provide alternatives. Monthly calendars include every date, including the sixth row when required. Dates and hours use `Asia/Beirut`; week totals include overnight care. A new rule overrides older rules for its selected interval, so a single date can be reopened without deleting a recurring block. Yearly ranges cannot start or end on February 29. Future recurring hours that fall in a DST gap begin at the next valid local minute.

Existing positive `SitterAvailability` windows remain the base schedule; no windows means no date restrictions. Private `SitterScheduleException` records add available/unavailable overrides. Request acceptance reads these same rules. Schedule writes and request acceptance use serializable transactions: blocking hours that affect an active booking is rejected, including when a request is accepted concurrently. Stale schedule forms require a refresh. A sitter profile is required to save schedule rules or respond to requests. Photo messages, reports, and owner notifications are local application records; these actions do not send email, real payment charges or bank transfers. Linked booking lifecycle transitions enqueue RabbitMQ state events; care notes and photos stay local. Booking conversations use a separate implemented SignalR chat flow.

Apply the additive migration before running the updated application:

```sh
dotnet ef database update --project src/PetSitting -- --environment Development
```

`AddSitterBookingCareAndCalendar` adds start timestamps, checklist tasks, care updates/private photos, schedule exceptions, and a schedule revision. It was applied to the local development database. Run `CARECONNECT_SQL_TESTS=1 dotnet test tests/PetSitting.Tests` for the HTTP role/CSRF/rendering checks and SQL tests for payment-gated status changes, private photo access, checklist persistence, annual rules, schedule conflicts, and concurrent blocking/acceptance. The week-page rendering check covers the custom `TimeOnly` hour format as well as the week date heading.

### Sitter account pages

The sitter portal now includes `/Sitter/Earnings`, `/Sitter/Analytics`, `/Sitter/Reviews`, `/Sitter/Notifications`, and `/Sitter/Settings`. Earnings and analytics use the signed-in sitter’s actual records, keep currencies separate, and provide CSV, PDF, and XLSX snapshot exports. Reviews show eligible completed-booking ratings and allow the assigned sitter to save a reply. Notifications support category filters, read controls, 15-second refresh, saved in-app alert preferences, and an optional browser sound test. Settings edit account details, create or update the sitter profile, choose services and pricing, and link to existing Identity security controls.

Payout requests reserve eligible, unallocated successful payments from completed bookings in a serializable transaction. Requests may be cancelled while pending. These are local records: no bank transfer is made. Bank details are not collected, and email/SMS alert switches remain disabled until those transports are connected. Existing prices are not converted between currencies. Analytics request counts refer to recorded sitter responses and their current outcomes; average reply time is available only for responses with a recorded decision timestamp.

Apply `AddSitterReportsPreferencesAndPayouts` with the existing EF database-update command. It adds notification categories/deduplication keys, alert preferences, review replies, decision timestamps, and the payout reservation ledger. Notification delivery and preference checks are covered by `LiveNotificationTests`; these do not validate all report/export or payout behaviors.

### BabySitting sitter portal

BabySitting now mirrors the PetSitting sitter portal for babysitters: `/Dashboard/Sitter`, `/SitterRequests`, `/SitterBookings`, `/SitterAvailability`, and `/Sitter/Earnings|Analytics|Reviews|Notifications|Settings`. Parents get the matching `/Dashboard/Customer` page with their family profile. The services, transaction rules, payment-gated care start, schedule rules, payout reservations, and exports work exactly as described for PetSitting above.

Child data stays restricted: requests and bookings show a child count and age range (for example "2 children · ages 3–7") plus the family's health notes, routines, and care instructions. No child names, birth dates, or schools are stored or displayed. The care checklist is seeded from those parent notes.

Apply the `AddBabysitterPortal` migration before running:

```sh
dotnet ef database update --project src/BabySitting -- --environment Development
```

Run `CARECONNECT_SQL_TESTS=1 dotnet test tests/BabySitting.Tests` for the HTTP and SQL-backed checks.

### Editing and deleting submitted requests

Pet owners can open a request from **Bookings** and choose **Edit request** while it is open and its care start time is still in the future. Editing updates the pets, service, dates (Beirut time), city, private address, care instructions and total budget. Only the owner's pets may be selected; dog walking requires dogs. Pending invitations use the updated budget. Every edit invalidates older sitter confirmations, including edits that only change the selected pets.

Both applications provide a delete confirmation for open requests. Requests without offers or booking history are deleted together with their submitted wizard draft (and pet links in PetSitting). Requests with offers or previous bookings are marked cancelled to preserve history; pending invitations are withdrawn and their sitters notified. Accepted, in-progress and completed bookings remain protected and use the existing booking cancellation rules. Delete POSTs require antiforgery tokens and the request version from the confirmation page.

Regression coverage includes pet edit validation and access control, ownership and stale-version checks, deleting wizard-linked requests, and preserving reopened booking history. SQL-backed tests require the configured SQL Server and `CARECONNECT_SQL_TESTS=1`.


## Live chat and notification delivery

Customer chat links open the local booking route. Chat connections join the booking group
before fetching visible history on initial connection, reconnect and return to a visible tab.
History and live messages merge by message ID. Read receipts acknowledge only displayed
message IDs, so messages missed during a disconnect remain unread until recovered.

New requests save alerts for every eligible sitter in the same database save as the request,
including any preferred sitter invitation. Eligibility uses city, services, active/available
profiles, saved schedules and booking conflicts. Notifications are pushed after commit;
explicit transaction rollbacks discard pending broadcasts. This is local SignalR delivery,
not RabbitMQ integration.

Category preferences apply consistently to notifications, unread badges, dashboards and
live toasts. Disabled categories remain stored as activity records and become visible again
when re-enabled. Preference updates notify other tabs to update counts and remove disabled
categories from their current view. General account notices remain enabled.

## Google sign-in (both applications)

Both apps support Google OAuth through ASP.NET Core Identity. Credentials are optional:
without them, the Google button is hidden and password login remains available. Configure
both values together; incomplete Google configuration is rejected at startup.

### Google Cloud setup

1. Create or select a Google Cloud project, for example `CareConnect`.
2. Open **Google Auth platform → Branding** and configure the consent screen: app name,
   support email, and developer contact email. Choose **External** audience for personal
   Google accounts. While testing, add your Google accounts under **Audience → Test users**.
3. Under **Data Access**, use only basic identity scopes (`userinfo.email` and
   `userinfo.profile`). This implementation does not need Drive, Gmail, or Calendar access.
4. Under **Clients**, create two OAuth clients of type **Web application**:

   | Client | Authorized redirect URI |
   | --- | --- |
   | CareConnect PetSitting local | `http://localhost:5101/signin-google` |
   | CareConnect BabySitting local | `http://localhost:5102/signin-google` |

   JavaScript origins are not needed for this server-side redirect flow. Use the exact URI,
   including scheme, hostname, port and path; do not add a trailing slash. The callback is
   `/signin-google`, not the Identity ExternalLogin page. Keep each client's ID and secret.
5. Store each client's credentials in that application's user secrets:

   ```sh
   dotnet user-secrets set "Authentication:Google:ClientId" "<pet-client-id>" --project src/PetSitting
   dotnet user-secrets set "Authentication:Google:ClientSecret" "<pet-client-secret>" --project src/PetSitting
   dotnet user-secrets set "Authentication:Google:ClientId" "<baby-client-id>" --project src/BabySitting
   dotnet user-secrets set "Authentication:Google:ClientSecret" "<baby-client-secret>" --project src/BabySitting
   ```

6. Ensure both SQL Server databases are available, migrations are applied, and roles have
   been initialized using the existing `--initialize-identity` commands. Restart the apps,
   browse their configured `localhost` addresses and select **Continue with Google**.

For deployment, create production clients with HTTPS callback URLs on your public domains,
set each app's `Auth:PublicOrigin` to the corresponding HTTPS origin, and supply credentials
through the deployment secret store (`Authentication__Google__ClientId` and
`Authentication__Google__ClientSecret` for environment variables). Configure the Google
consent screen's production branding, domains and publishing status as applicable.
Never commit a client secret or paste one into source files.

### Account behavior

- New Google users finish signup by confirming their name and selecting customer or sitter.
  The app uses Google's verified email from the external authentication ticket; posted email
  or administrator role values cannot create privileged accounts. Users have no local password
  initially and can add one through Identity's **Set a password** page.
- Each app owns its own account, role and Google login association; signing up in one does
  not create an account in the other. Sitter profile setup and professional verification are separate.
- Existing accounts with the same email are not automatically linked. Sign in with the current
  method, open **Account settings → Google & linked accounts**, then link Google through
  Identity's authenticated external-login management page.
- Returning linked users go through Identity's active-account, lockout, confirmation and
  two-factor checks. Google login does not bypass an enabled authenticator challenge.
- Cancelling Google consent or an invalid OAuth state returns a generic login error. Redirects
  after login are restricted to local application URLs. Google access/refresh tokens are not persisted.
- Account deletion requires a local password; Google-only users are directed to set one first.

### Verification

`dotnet test CareConnect.sln --filter FullyQualifiedName~GoogleAuthTests` exercises redirects,
CSRF protection, protected external tickets, verified-email claims, invalid state, disabled
configuration and forbidden signup roles using a simulated Google HTTP backchannel. The
SQL opt-in test additionally exercises customer/sitter signup and returning Google login:
`CARECONNECT_SQL_TESTS=1 dotnet test CareConnect.sln --filter FullyQualifiedName~GoogleAuthTests`.
A real Google consent round trip requires configured Google Cloud credentials.

References: [Google consent configuration](https://developers.google.com/workspace/guides/configure-oauth-consent),
[Google web-server OAuth](https://developers.google.com/identity/protocols/oauth2/web-server),
and [ASP.NET Core Google login](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/social/google-logins?view=aspnetcore-10.0).

Payment receipts download as PDF in both apps after a successful payment. Receipts include payment references, Beirut care dates, totals, and an explicit notice for demo payments. The generator embeds the app fonts, supports multiple pages, and runs without a browser installation.

Account links route customers and sitters to their portal settings, including the sitter service-city field. All remaining Identity account/security pages share branded layouts, including email changes, password setup/reset, linked logins, authenticator/recovery-code flows, lockout and access-denied screens. Generic MVC fallback pages also use CareConnect styling rather than the Bootstrap starter layout.
