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

Incoming linked requests can have no local customer initially, but must carry a valid source, external request ID, and correlation ID. Attaching them to a verified local account is a later application workflow. Childcare data contains child counts, age ranges, and essential care instructions without child names, birth dates, or school information; access controls must precede exposing this data through any future endpoints.

Identity authentication and role authorization are implemented. PetSitting sitter request decisions now enforce request eligibility, booking ownership, and overlapping-booking prevention. Sitter booking start/completion, care checklists, private photo updates, and calendar availability are also implemented. Review submission and payment/payout processing remain future service work.

## RabbitMQ

The main Docker Compose file manages RabbitMQ, reusing your existing SQL Server. The separate authentication Compose file runs Mailpit for local confirmation and recovery email. Copy `.env.example` to `.env`, set RabbitMQ credentials, and run:

```sh
docker compose up -d rabbitmq
```

AMQP uses port 5672; management is at http://localhost:15672. Set `RabbitMq:UserName` and `RabbitMq:Password` in each project's user secrets before implementing messaging. `.env` is read by Compose, not automatically by `dotnet run`.

## Architecture and phased development

Each MVC app owns its users, database, migrations, services, and repositories. Controllers delegate business operations to services. Applications must never access each other's databases or call each other's APIs. RabbitMQ is the sole cross-application transport. Shared contracts contain message data only, with no shared persistence or business entities.

Planned local relationships: users own pets or family profiles and care requests; sitters submit offers on requests; an accepted offer creates a booking; bookings own payments and reviews. Availability belongs to a sitter. Outbox and processed-message records belong to the local application. Cross-platform links use external request IDs and correlation IDs, never cross-database foreign keys. Child information is restricted to essential local data.

Planned topic exchange: `careconnect.events`, with durable `petsitting.events` and `babysitting.events` queues. Combined-care events carry event ID, correlation ID, source, version, timestamp, external request IDs, and minimal service details. Each platform consumes relevant events from the other and updates its own database. Publishers, consumers, retries, dead-letter queues, idempotency, outbox processing, and cancellation synchronization are Phase 5–6 work; configuring the broker does not implement these features.

1. Foundation: solution, default MVC folders, layouts, EF Core configuration, infrastructure configuration, independent startup checks.
2. Identity: registration/login, roles, authorization, and profile management using the migrated Identity tables.
3. Pet sitting: pets, sitter profiles, requests, offers, bookings.
4. Babysitting: family details, sitter profiles, requests, offers, bookings.
5. Messaging: versioned payloads, publishers, hosted consumers, topology and reliability.
6. Combined care: linked requests, independent booking states, partial success, cancellation.
7. Advanced features: SignalR, dummy payments, AI matching, reporting.
8. Quality assurance: business and integration tests, messaging reliability, deployment verification.

Current verification: both MVC applications build without warnings; InitialCreate, ConfigureIdentity, and EnforceSingleAccountRole migrations have been applied independently on `localhost:1433`. All 36 checks pass, including SQL Server constraints and full HTTP authentication flows. Database constraint tests roll back; authentication tests delete their temporary accounts. Live SMTP delivery and confirmation were verified in both applications using Mailpit.

Run route/configuration checks with `dotnet test`. To include the actual SQL Server checks using local user secrets:

```sh
CARECONNECT_SQL_TESTS=1 dotnet test CareConnect.sln
```

RabbitMQ connectivity and the remaining care workflows remain future work.

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
- `/Dashboard`: authenticated account hub; Customer/Sitter/Manager subpages enforce their respective roles. PetSitting routes display the owner or sitter dashboard; BabySitting and management care pages remain access-controlled placeholders.

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

`/Dashboard` redirects pet owners to `/Dashboard/Customer` and pet sitters to `/Dashboard/Sitter`. Both pages use the PawPal portal theme from the supplied dashboard designs, including responsive navigation, lime metric cards, charts, booking details, notifications, and empty states. Existing pet CRUD and account links remain available.

Dashboard data comes from the signed-in account’s database records. Owners see their pets, requests, bookings, payments, and available sitters in their city. Sitters see their bookings and open requests in their profile/account city, filtered by configured services and excluding requests they have already responded to. Request addresses are not displayed on the dashboard. Dates use Beirut time. Analytics show the current calendar year and received/succeeded USD payments; individual payment and booking rows retain their recorded currencies.

Tabs and sidebar links switch dashboard sections. Search filters the current view, booking rows open a details dialog, and sitter reports download as CSV. Request creation, pricing, general messaging, reviews, payments, payouts, and AI matching still require their planned backend workflows. Booking care updates and availability editing are implemented on their dedicated sitter pages. Sitter request responses are implemented on the Incoming requests page. Related buttons explicitly explain availability instead of simulating a successful operation. No database migration is needed.

Dashboard HTTP tests cover role redirects, access denial, and rendering populated/empty accounts without external services. The SQL-backed scoping check runs with `CARECONNECT_SQL_TESTS=1`. For local visual QA, `CARECONNECT_DASHBOARD_QA=/tmp/careconnect-dashboard-qa dotnet test tests/PetSitting.Tests --filter Dashboard_renders` writes HTML using test-only fixtures; the running application never uses this fixture data.

### Sitter incoming requests

Open **Requests** in the sitter sidebar or visit `/SitterRequests`. The page implements the incoming inbox, acceptance confirmation, and saved decision designs. Status tabs and owner/pet search work through normal GET links/forms and are enhanced with partial-page updates. The inbox polls every 15 seconds while visible, pauses during confirmation, and shows a toast when new eligible requests arrive. It reports connection failures instead of claiming a successful sync.

Requests are discovered using the sitter’s profile/account city and configured services; this application currently has a city-based request marketplace rather than individually addressed invitations. An existing sitter profile is required to respond. New and pending requests can be accepted or declined, while previous decisions remain visible in their status tabs. Pending rows correspond to existing pending offer records. Exact service addresses are only included in the response after that sitter has accepted; medical, feeding, and special-care notes are available for review beforehand. Fit checks use actual bookings, saved availability intervals, city, and hourly rate; no distance, pet-type preference, owner rating, or response deadline is invented.

`POST /SitterRequests/Decide` requires the sitter role, an antiforgery token, and the displayed request/offer version. Acceptance records an accepted offer, marks the request booked, creates an awaiting-payment booking (confirmed immediately for zero-cost care), and saves an owner notification in one serializable transaction. Notifications are persisted locally; this does not send email or publish RabbitMQ events. Decline records the sitter’s rejected offer and notifies the owner while leaving the request open for other sitters. Stale details, expired requests, unavailable sitters, dates outside saved availability, and overlapping bookings cannot be accepted. Retries return the existing decision without creating duplicate bookings or notifications. The saved state includes an Open booking link to the dedicated sitter booking details page.

No new schema migration is required. Ensure the existing `AddPetCareDetails` migration is applied so medical and feeding notes can be read. SQL integration tests create uniquely identified temporary records and clean them up. Run `CARECONNECT_SQL_TESTS=1 dotnet test tests/PetSitting.Tests` to verify persistence, stale confirmations, private address access, rollback after a write failure, simultaneous acceptance by different sitters, and simultaneous overlapping bookings for one sitter.

### Sitter bookings and availability

The sitter sidebar now opens `/SitterBookings` and `/SitterAvailability`. Bookings have Upcoming, In progress, Accepted, Completed, and Cancelled filters, owner/pet search, a weekly summary, and a CSV export that neutralizes spreadsheet formula fields. Each booking opens `/SitterBookings/Details/{id}` with an acceptance/start/completion timeline, owner contact details when provided, payment records, a care checklist, photo updates, and issue reporting. Records are restricted to the assigned sitter; a different sitter cannot read or change the booking.

Care can start only at or after the scheduled start and after successful payments in the booking currency cover its full amount (zero-cost bookings require no payment). Start and completion timestamps and owner notifications are committed together. Repeated status submissions do not create duplicate notifications. Completion also completes the care request. Owner instructions provide the checklist’s initial items, without inventing medication doses or care times. Sitters can add tasks and optional due times within the stay, and update checkboxes while care is in progress. Care updates and issue reports are saved with owner notifications. Owners can read these notes and photos in their dashboard’s booking details. Photos accept JPEG/PNG/WebP up to 5 MB, are stored outside public static assets in the database, and are served only to the booking owner or assigned sitter. No veterinarian contact, service fee, or payout is fabricated.

The availability page supports month and week navigation, Today, date selection, private reasons, all-day/hour ranges, yearly recurrence, and a global accepting-requests preference. In week view, drag within a day to choose hours and review them in the editor before saving; day headings and keyboard Enter/Space provide alternatives. Monthly calendars include every date, including the sixth row when required. Dates and hours use `Asia/Beirut`; week totals include overnight care. A new rule overrides older rules for its selected interval, so a single date can be reopened without deleting a recurring block. Yearly ranges cannot start or end on February 29. Future recurring hours that fall in a DST gap begin at the next valid local minute.

Existing positive `SitterAvailability` windows remain the base schedule; no windows means no date restrictions. Private `SitterScheduleException` records add available/unavailable overrides. Request acceptance reads these same rules. Schedule writes and request acceptance use serializable transactions: blocking hours that affect an active booking is rejected, including when a request is accepted concurrently. Stale schedule forms require a refresh. A sitter profile is required to save schedule rules or respond to requests. Photo messages, reports, and owner notifications are local application records; no email, chat transport, RabbitMQ event, payment charge, or payout is sent by these actions.

Apply the additive migration before running the updated application:

```sh
dotnet ef database update --project src/PetSitting -- --environment Development
```

`AddSitterBookingCareAndCalendar` adds start timestamps, checklist tasks, care updates/private photos, schedule exceptions, and a schedule revision. It was applied to the local development database. Run `CARECONNECT_SQL_TESTS=1 dotnet test tests/PetSitting.Tests` for the HTTP role/CSRF/rendering checks and SQL tests for payment-gated status changes, private photo access, checklist persistence, annual rules, schedule conflicts, and concurrent blocking/acceptance. The week-page rendering check covers the custom `TimeOnly` hour format as well as the week date heading.
