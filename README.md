# Exvo Backend

Exvo is an event platform built with .NET 8 microservices, a YARP API gateway,
MySQL persistence, JWT authentication, and Kafka messaging. Run the backend with
Docker Compose; each service runs in a container in the background.

## Service Overview

| Component | Responsibility | Current implementation |
| --- | --- | --- |
| API Gateway | Frontend entry point | Routes Auth, Catalog, Booking, Check-In, coordinator, reports, and analytics requests |
| Auth Service | Accounts and authentication | Registration, login, JWT, and profiles |
| Catalog Service | Events and categories | Event browsing and organizer event management |
| Booking Service | Reservations and tickets | Seating plans, seat holds, booking confirmation, ticket retrieval, and email notifications |
| Check-In Service | Event access and reporting | Ticket validation, check-in, coordinator access, reports, and analytics |
| Ticket Service | Booking message consumer | Kafka consumer; separate ticket issuance is not implemented |
| Exvo.Shared | Shared code | Cross-service project |

The gateway project is `src/gateway/ApiGateway/ApiGateway.csproj`.
`src/gateway/Exvo.ApiGateway` is a separate scaffold and is not used by Compose.

## Prerequisites

- Docker Desktop running with Linux containers and Docker Compose v2.
- Access to the Azure MySQL server configured in `.env.local`, including network/firewall access.
- Existing Auth and Catalog database schemas on that server. Compose does not provision Azure resources or apply their initial migrations.
- Node.js and npm to run the frontend; see the [frontend README](../exvo-FE/README.md).

The host .NET SDK is only needed for running .NET tests or development tools.
Docker builds the backend using the SDK images in the Dockerfiles.

## Setup and run with Docker

Run the following from `exvo-BE`, the directory containing `docker-compose.yml`.
Commands below use PowerShell.

### 1. Configure the environment

On first setup, copy the template:

```powershell
Copy-Item .env.example .env.local
```

If `.env.local` already exists, edit it instead of overwriting it. Set
`AZURE_MYSQL_PASSWORD` and check `AZURE_MYSQL_HOST`, `AZURE_MYSQL_PORT`, and
`AZURE_MYSQL_USER`. Keep `.env.local` private; it is ignored by Git.

The database layout is:

| Service | Database | Location |
| --- | --- | --- |
| Auth | `exvo_auth_db` | Azure MySQL |
| Catalog | `exvo_catalog_db` | Azure MySQL |
| Booking | `exvo_booking_db` | Compose `bookingdb` container |
| Check-In | `exvo_checkin_db` | Compose `checkindb` container |

Compose supplies the database connection strings to the services. Booking and
Check-In use the configured `AZURE_MYSQL_PASSWORD` for their local database
credentials too. Booking applies its migrations at startup in Development;
Check-In creates its schema when absent. Auth and Catalog require their existing
base schemas; their startup schema patches do not replace initial provisioning.
Azure connections require SSL.

Email is optional. Leave `EMAIL_HOST` empty to write booking emails to
`notifications-outbox` inside the Booking container. To send email, configure
the `EMAIL_*` SMTP settings in `.env.local`. The outbox is not volume-mounted;
copy messages out before recreating the container if you need to keep them:

```powershell
docker cp exvo_bookingservice:/app/notifications-outbox ./notifications-outbox
```

### 2. Build and start the backend

```powershell
docker compose --env-file .env.local up --build -d
docker compose --env-file .env.local ps
```

This starts the gateway, five services, Kafka, and the Booking and Check-In
databases. The optional `mysql-dev` container is not part of normal startup.
No PowerShell launcher or separate service terminals are needed.

### 3. Check the backend

```powershell
Invoke-RestMethod http://localhost:5000/health
Invoke-RestMethod http://localhost:5255/health
Invoke-RestMethod http://localhost:5266/health
Invoke-RestMethod http://localhost:5028/health
Invoke-RestMethod http://localhost:5000/api/catalog/categories
```

Health endpoints are liveness checks; they do not prove database or downstream
availability. The categories request checks the gateway-to-Catalog path and a
database-backed endpoint. If startup or requests fail, inspect the logs:

```powershell
docker compose --env-file .env.local logs --tail 100
docker compose --env-file .env.local logs -f bookingservice checkinservice
```

Press Ctrl+C to stop following logs; containers keep running. For Azure errors,
check credentials, firewall access, and the existing Auth/Catalog schemas.
If a local database rejects credentials after a password change, remember that
MySQL initialization variables only apply when its volume is first created;
existing database users need their passwords updated to match.

### 4. Start the frontend

Follow the [frontend setup instructions](../exvo-FE/README.md). It runs locally
at `http://localhost:5173` and calls the gateway at `http://localhost:5000`.
The gateway allows the Development origins `http://localhost:5173` and
`http://127.0.0.1:5173`. Use port 5173 for the frontend or configure an additional
allowed origin if using another port.

### Update or stop the backend

After changing backend code or `.env.local`, rebuild/recreate the containers:

```powershell
docker compose --env-file .env.local up --build -d
```

Stop and remove the containers and network while retaining database/Kafka volumes:

```powershell
docker compose --env-file .env.local down
```

Only when you intend to permanently discard local database and Kafka data:

```powershell
docker compose --env-file .env.local down --volumes
```

This does not delete Azure databases. The `local` profile is a legacy optional
MySQL container for host development; it does not redirect Auth or Catalog from
Azure. Normal Docker startup does not need that profile.

## Authentication API

The gateway forwards the following routes to the Auth service.

### Register

`POST /api/auth/register`

```json
{
  "fullName": "Alex Perera",
  "email": "alex@example.com",
  "password": "StrongPassword123!"
}
```

Returns `201 Created` with user details and a JWT. Attendee registration defaults to the `Attendee` role; company/organizer registration is also supported.

### Login

`POST /api/auth/login`

```json
{
  "email": "alex@example.com",
  "password": "StrongPassword123!"
}
```

Returns `200 OK` with user details and a JWT. Invalid credentials return `401 Unauthorized`.

### Current User

`GET /api/auth/me`

```http
Authorization: Bearer <jwt-token>
```

Returns the authenticated user's profile, role, and creation timestamp.

### Update Profile

`PUT /api/auth/profile` requires a bearer token and updates the current user's
profile details and profile picture.

## Catalog API

The gateway forwards `/api/catalog/*` to CatalogService.

| Method | Endpoint | Purpose | Authentication |
| --- | --- | --- | --- |
| GET | `/api/catalog/categories` | List categories | Public |
| POST | `/api/catalog/categories` | Create a category | Public in the current implementation |
| GET | `/api/catalog/events` | List events; optional `categoryId` query filter | Public |
| GET | `/api/catalog/events/{id}` | Get an event | Public |
| POST | `/api/catalog/events` | Create an event | Company/Organizer role |
| PUT | `/api/catalog/events/{id}` | Update an event | Company/Organizer role |
| DELETE | `/api/catalog/events/{id}` | Delete an event | Company/Organizer role |

## Docker service URLs

Use the gateway for frontend API requests. These are the published host ports;
containers communicate with each other by service name on port `8080`.
Compose exposes HTTP, not the HTTPS ports from IDE launch profiles.

| Service | Host URL or address |
| --- | --- |
| API Gateway | `http://localhost:5000` |
| Auth | `http://localhost:5284` |
| Catalog | `http://localhost:5255` |
| Booking | `http://localhost:5266` |
| Check-In | `http://localhost:5028` |
| Ticket | Internal only: `http://ticketservice:8080` |
| Booking MySQL | `localhost:3307` |
| Check-In MySQL | `localhost:3308` |
| Kafka | `localhost:29092` (host), `kafka:9092` (containers) |

Swagger is enabled in Development at `/swagger` on the individual services.
The gateway does not aggregate their Swagger pages.

## Testing

Install the .NET 8 SDK to run these commands on the host, from `exvo-BE`.

```powershell
dotnet test .\ExvoPlatform.sln
```

Test projects:

- `tests/AuthService.Tests`
- `tests/BookingService.Tests`
- `tests/CatalogService.Tests`
- `tests/CheckInService.Tests`
- `tests/TicketService.Tests`

The test projects use xUnit for automated verification.

### Mutation testing with Stryker.NET

The pilot selects `BookingService` and
`tests/BookingService.Tests/Exvo.BookingService.Tests.csproj`. Unlike Auth's
placeholder test, this project references production code and has working unit
and API tests. The scope is deliberately limited to
`src/services/BookingService/Services/EventInventoryCatalogClient.cs`: ticket-tier
quantities (including unlimited inventory), pricing, general-admission fallback,
and event snapshots. This is a pilot score for one file, not the whole backend.
Only `EventInventoryCatalogClientTests` run during mutation testing; the API tests
replace this client with a stub and cannot detect mutations in it.

Install the .NET 8 SDK and runtime (including ASP.NET Core). The repository local
tool manifest pins Stryker.NET **4.16.0**, which runs on .NET 8; Stryker 5 requires
the .NET 10 runtime. Application and test target frameworks remain `net8.0`.
See the [4.16.0 package](https://www.nuget.org/packages/dotnet-stryker/4.16.0)
and [Stryker migration guide](https://stryker-mutator.io/docs/stryker-net/migration-guide/).
NuGet access is needed for the initial restores. No database, Kafka, credentials,
or running Catalog service is needed for the focused unit tests.

From the backend root:

```powershell
dotnet --info
dotnet tool restore
dotnet test tests/BookingService.Tests/Exvo.BookingService.Tests.csproj --configuration Release
cd tests/BookingService.Tests
dotnet stryker --config-file stryker-config.json --log-to-file
```

Run Stryker from the test directory so project discovery and relative mutation
paths resolve correctly. The checked-in configuration uses two workers and
outputs progress, HTML, and JSON reports, with a 10-second additional timeout
allowance. Coverage optimization is explicitly off for predictable execution of
this small suite: all 27 focused test cases run against every selected mutant.
Reports are generated under
`tests/BookingService.Tests/StrykerOutput/<run-timestamp>/reports/` as
`mutation-report.html` and `mutation-report.json`. Open the HTML file in a browser
and select the source file to inspect each mutation and the tests that detected it.
Generated output is ignored by Git; retain reports when comparing runs.

- **Killed**: a test failed when Stryker introduced the change, so assertions
  detected that fault.
- **Survived**: tests executed the mutated code but passed; inspect whether a
  meaningful assertion or input is missing, or whether the mutation is equivalent.
- **NoCoverage**: no selected test exercised the mutation; add a test of the
  corresponding behavior before expecting it to be detected.
- **Timeout**: counted as detected, but inspect for hangs rather than assuming
  an assertion caught it. Compile/runtime errors and ignored mutants are reported
  separately and do not establish test quality.

The score is detected mutants (`Killed` + `Timeout`) divided by detected plus
undetected (`Survived` + `NoCoverage`) mutants. Review the counts as well as the
percentage. Threshold colors are green at 80%, amber at 60%, and red below 60%.
The break threshold is 0: score alone does not fail this exploratory pilot;
configuration, build, and test failures still fail the mutation command.
Do not alter production behavior or exclude surviving mutants to inflate the score.

In GitHub Actions, run **Backend Mutation Testing** from the Actions tab using
**Run workflow** (`.github/workflows/backend-mutation.yml`). It is manual and
separate from the existing CI/CD workflows, so mutation work does not delay normal
builds or deploys. Its explicit matrix contains Booking, Catalog, Auth, and Ticket
(not CheckIn),
with independent jobs and `fail-fast: false`. Each job runs that service's full
tests first, then its scoped mutation command, with a 20-minute limit per job.
The `<service>-mutation-<run-id>` artifacts
retains HTML/JSON reports and available logs for 14 days and uploads even if a
prior step fails (a failure before report generation may leave no report).
The workflow also rejects reports with no killed or surviving mutants, preventing
an all-timeout result from masquerading as a useful 100% score.

If all mutants time out, inspect `StrykerOutput/<run-timestamp>/logs/` before
interpreting the score. During local validation, Windows Application Control
blocked the generated assembly (`0x800711C7`), leading to zero executed tests
and misleading all-timeout scores. Use an environment permitted to load generated
assemblies, such as the Linux CI runner; increasing timeouts does not fix this block.

#### Pilot results and surviving mutants

Validated on 12 October 2026 with SDKs 8.0.424 and 10.0.401 installed (.NET
8.0.30 runtime available; local commands selected SDK 10.0.401). The baseline
Booking suite passed 20 tests. After adding meaningful catalog-client coverage,
the full suite passed 46 tests, including 27 focused cases. Production code and
target frameworks were not changed.

The initial mutation score was **15.12%** (13 killed, 33 survived, 40 uncovered).
The validated final run scored **83.72%** (72 killed / 86 assessed, 14 survived,
0 timeouts), taking approximately 2 minutes 22 seconds. In the selected file,
5 additional mutants could not compile and 13 were automatically ignored by
Stryker's redundant-block filter; neither group contributes to the score.
With coverage optimization off, `NoCoverage` is not classified separately.
The final local HTML report is
`tests/BookingService.Tests/StrykerOutput/2026-10-12.08-53-53/reports/mutation-report.html`.
Earlier all-timeout reports are invalid for assessing test quality.

Representative initial gaps now covered include changing the unlimited quantity
boundary from `<= 0` to `< 0` (assert quantity zero remains unlimited), forcing
finite quantity to zero (assert positive quantities are retained), dropping tier
prices or names (assert complete tier values), deleting general-admission fallback
(assert positive inventory with missing tiers), and dropping snapshot fields or
their aliases (assert complete snapshots, fallback values, and precedence).
HTTP 404/500 and endpoint-routing tests also detect lost error handling and paths.

All 14 final survivors appear equivalent through the current public API:

| Representative mutants (IDs in this run) | Why they survive / test implications |
| --- | --- |
| 1441-1444, line 36; 1445-1448, line 37 | `GetInt`/`GetDecimal` lookups ignore casing, so the PascalCase lookup duplicates the camelCase lookup. Removing either duplicate does not change the result. Numeric and string-value tests already assert inventory and fallback pricing; no public input distinguishes these mutants. |
| 1490, 1492-1495, line 70 | Both tier-property lookups ignore casing and return the same result. Replacing the conjunction or removing either duplicate lookup preserves observable behavior. Tests cover uppercase properties, missing tiers, and array/string representations; another casing test would not kill an equivalent mutant. |
| 1528, line 118 | Returning `true` instead of `false` for a missing property still supplies a default/undefined `JsonElement`. The public callers treat that value as missing and produce the same defaults. Missing/unsupported-field tests already assert this behavior; testing the private Boolean directly would couple tests to implementation without improving the public contract. |

These are equivalence assessments, not exclusions: the survivors remain visible
in the report. Investigate other survivors when extending the scope or changing
the parsing contract. The local .NET 10 SDK also emitted a nonfatal newer-Razor-
analyzer warning; the manual CI workflow selects .NET 8. The workflow is prepared
but has not been dispatched on GitHub from this local checkout.

#### Additional completed-service scope

The extensions reuse the pinned local Stryker.NET 4.16.0 tool and .NET 8 target
frameworks. They add Catalog, Auth, and Ticket scopes without changing Booking's
configuration, tests, or saved pilot report. Scores describe the selected files, not entire
services or the solution.

| Service | Eligibility and mutation scope |
| --- | --- |
| Booking | Existing pilot retained: `Services/EventInventoryCatalogClient.cs`, using `EventInventoryCatalogClientTests` only. |
| Catalog | Existing 22 tests passed before edits. Mutate `EventAccess.cs` and `EventSchedule.cs`, using all tests in `tests/CatalogService.Tests/Exvo.CatalogService.Tests.csproj`. Covers organizer access, schedule validation, UTC conversion, expiry queries, and persisted expiry visibility. |
| Auth | `Services/TokenServices.cs`, using `TokenServiceTests`. The former empty test was replaced with real JWT signature, issuer/audience, identity/role, optional-field, identifier, and expiry checks; the test project now references production. |
| Ticket | `Services/BookingConfirmedConsumer.cs`, using `BookingConfirmedConsumerTests` in the new test project. Covers disabled-consumer configuration and cancellation before entering the configured Kafka loop. Kafka processing remains uncovered; this is not a score for complete ticket issuance. |
| CheckIn | Explicitly excluded because the service is incomplete; neither its tests nor production code are part of this workflow. |

No candidate baseline tests failed. Catalog's suite contains both direct unit
tests and API tests backed by EF's in-memory provider; its SQL-translation test
uses the MySQL provider without connecting to MySQL. No external services or
credentials are required. The mutation scope excludes startup, migrations,
database configuration, and the background worker.

From the backend root, restore the existing tool and run the service baseline:

```powershell
dotnet tool restore
dotnet test tests/CatalogService.Tests/Exvo.CatalogService.Tests.csproj --configuration Release
cd tests/CatalogService.Tests
dotnet stryker --config-file stryker-config.json --log-to-file
```

For Booking, use the existing commands above. Always run mutation commands from
the corresponding test directory. Separate project directories and timestamped
output keep reports from overwriting each other:

- Booking: `tests/BookingService.Tests/StrykerOutput/<run-timestamp>/reports/mutation-report.html`.
- Catalog: `tests/CatalogService.Tests/StrykerOutput/<run-timestamp>/reports/mutation-report.html`.
- Auth: `tests/AuthService.Tests/StrykerOutput/<run-timestamp>/reports/mutation-report.html`.
- Ticket: `tests/TicketService.Tests/StrykerOutput/<run-timestamp>/reports/mutation-report.html`.
- JSON reports are adjacent to the HTML; diagnostic logs are under each run's
  `logs/` directory. Generated output remains ignored by Git.

Catalog uses two workers, a 10-second additional timeout, and `perTest` coverage
analysis, which completed successfully locally. This distinguishes `NoCoverage`
from executed survivors and selects relevant tests for each mutant. Booking
retains its existing coverage-off setting, so its zero reported `NoCoverage`
count does not establish coverage of every branch. Both stop on an initial test
failure, use the same 80/60/0 thresholds, and generate separate HTML/JSON reports.

The matrix workflow remains manual-only. Dispatch **Backend Mutation Testing**
in GitHub Actions to run all four configured services, then download each service's
artifact and open its HTML report. Artifacts retain reports/logs for 14 days and
upload even on failure. GitHub Actions has not been dispatched from this checkout;
the results documented here are local runs.

#### Extension validation and Catalog survivors

Catalog's baseline passed 22 tests. Its first scoped mutation run scored 65.91%
(29 killed, 14 survived, 1 uncovered, 0 timeouts). Adding 24 direct domain cases
in `EventDomainTests.cs` brought the suite to 46 passing tests. They check Company
role access, authentication, subject-claim fallback and precedence, valid offset
limits, exact-time boundaries, actionable validation errors, date-range overflow,
UTC conversion, and complementary expired/upcoming queries. No production code
was changed.

| Service / report | Score | Killed | Survived | NoCoverage | Timeout |
| --- | --- | --- | --- | --- | --- |
| Booking preserved pilot and local recheck | 83.72% | 72 | 14 | 0 (not classified with coverage off) | 0 |
| Catalog final local run | 97.73% | 43 | 1 | 0 | 0 |

Catalog's final report is
`tests/CatalogService.Tests/StrykerOutput/2026-10-12.10-22-24/reports/mutation-report.html`.
The run took approximately 1 minute 16 seconds. The two selected files also
contain 8 compile-error mutants and 5 mutants automatically ignored by Stryker's
redundant-block filter; these do not enter the score. Both HTML reports are kept
in their own test directories; the original Booking pilot report is retained.
Booking's unchanged mutation command was also rerun successfully, producing
the same 72-killed/14-survived result in approximately 1 minute 29 seconds at
`tests/BookingService.Tests/StrykerOutput/2026-10-12.10-24-36/reports/mutation-report.html`.
Its original `2026-10-12.08-53-53` report remains byte-for-byte intact.

The remaining Catalog survivor is **104**, `EventSchedule.cs:24`: removing the
early `return` when `expiredEvents.Count == 0`. Existing tests see the same event
data, but the mutant calls `SaveChangesAsync` even when nothing expired. A test
would distinguish it by creating only future events, making an unrelated pending
edit in that context, calling `HideExpiredAsync`, and reading through a fresh
context to assert that the pending edit was not persisted. Establish that
no-expiry/no-write guarantee as an intended contract before adding that regression
test; asserting only the number of internal save calls would not add useful
behavior coverage. The survivor remains included, without a production change
or mutation exclusion.

Representative initial survivors that the added tests now kill include removing
the `Company` role name (54), dropping or changing `sub` fallback and identity
precedence (56/58/59), admitting an unauthenticated organizer (64), rejecting the
valid -840-minute boundary (79), rejecting the exact current instant (88), hiding
an event exactly at its start rather than after it (97), and adding instead of
subtracting the stored UTC offset or always using the legacy offset (107/108).
The previously uncovered validation-error return (91) is now exercised by a
date/offset combination whose UTC conversion would underflow.

Local validation uses the installed .NET 8 runtime and the pinned Stryker tool.
The selected local SDK is 10.0.401 and emits the previously documented nonfatal
Razor-analyzer warning. Mutation commands ran outside the sandbox through the
approval mechanism to avoid the Windows generated-assembly block. CI installs
.NET 8 and uses Linux; that GitHub execution remains unverified until dispatch.

#### Auth and Ticket setup

Auth's original placeholder test passed before it was replaced. The new production
reference and 10 unit-test cases exercise JWT signing and validation with explicit
test credentials, the existing fallback configuration, claims, optional company
details, unique token IDs, and eight-hour expiry. Auth production code is unchanged.

Ticket previously had no test project. Its existing service built successfully
before edits. `BookingConfirmedConsumer` was moved unchanged from `Program.cs`
into `Services/BookingConfirmedConsumer.cs` to provide a stable mutation scope.
`InternalsVisibleTo` allows the new test assembly to exercise the internal worker;
no runtime behavior or target framework was changed. The four unit-test cases
cover missing/empty/whitespace Kafka configuration and cancellation before the
configured loop starts. The new test project is registered in `ExvoPlatform.sln`.
The weather scaffold and application startup are excluded from the mutation scope.

From the backend root:

```powershell
dotnet tool restore
dotnet test tests/AuthService.Tests/Exvo.AuthService.Tests.csproj --configuration Release
dotnet test tests/TicketService.Tests/Exvo.TicketService.Tests.csproj --configuration Release
cd tests/AuthService.Tests
dotnet stryker --config-file stryker-config.json --log-to-file
cd ../TicketService.Tests
dotnet stryker --config-file stryker-config.json --log-to-file
dotnet build Exvo.TicketService.Tests.csproj --configuration Release --no-restore -t:Rebuild
```

Neither suite requires MySQL or Kafka. The cancellation test uses an already
cancelled token and does not start a broker connection. Both configurations use
`perTest` coverage, two workers, a 10-second additional timeout, and the existing
80/60/0 thresholds. A low score remains informational, while build/test errors
and an all-timeout report still fail validation.

The manual workflow now uploads separate `auth-mutation-<run-id>` and
`ticket-mutation-<run-id>` artifacts alongside Booking and Catalog. After mutation
it rebuilds normal test assemblies even on failure: one local Ticket run briefly
left a mutated output DLL in place because a test host held a file lock. Rebuilding
resolved that lock and restored the normal assembly without changing the report.
This cleanup step does not rerun mutation testing or overwrite saved reports.

#### Auth and Ticket local results

| Service / selected scope | Score | Killed | Survived | NoCoverage | Timeout | Unit tests |
| --- | --- | --- | --- | --- | --- | --- |
| Auth JWT TokenService | 100.00% | 35 | 0 | 0 | 0 | 10 passed |
| Ticket BookingConfirmedConsumer | 25.93% | 7 | 0 | 20 | 0 | 4 passed |

The final separate HTML reports are:

- Auth: `tests/AuthService.Tests/StrykerOutput/2026-10-12.10-38-52/reports/mutation-report.html`.
- Ticket: `tests/TicketService.Tests/StrykerOutput/2026-10-12.10-39-05/reports/mutation-report.html`.

Auth's final run took approximately 1 minute 36 seconds; Ticket's took 1 minute
15 seconds. The selected scopes contain 4 and 8 automatically ignored redundant
block mutants respectively. No production behavior was changed to raise either
score. These are local results, not dispatched GitHub Actions runs.

Auth's first run had one survivor (321, `TokenServices.cs:33`) that changed the
explicit `role` claim name to an empty string. Standard role validation still
passed because the other role claim remained. The added assertion that generated
claim names are nonempty detects that malformed output; the final run kills it.
Other tests detect changed signing/configuration keys, issuer/audience values,
identity and optional-claim names, and the expiry calculation. Registration,
login/password verification, and profile endpoints are outside this JWT scope.

Ticket has **no executed survivors**, but **20 uncovered mutants** in the Kafka
processing path. This low score is a coverage gap, not a failed mutation run or
an all-timeout result. Representative uncovered changes include removing consumer
configuration (36), enabling auto-commit (38), removing topic subscription (39/40),
dropping explicit commits for null or valid records (50/54), removing cancellation
handling (56), and removing `Close()` (60). Detecting them needs broker-backed
integration tests or a separately designed injectable consumer factory: verify the
configured group/topic and manual commit policy, publish valid/null/malformed
messages, inspect committed offsets and continued processing, then verify graceful
cancellation and group shutdown. Those tests are not implemented in this unit-only
setup. There is currently no ticket-issuance implementation to test.

Both final commands completed with no timeouts, and normal assemblies were rebuilt
and retested afterward. Formatting checks, solution registration, workflow
YAML/matrix checks, and the workflow's JSON report-validation commands were also
run locally. Booking/Catalog configurations and saved reports remain preserved;
CheckIn is not in the mutation matrix and its files are unchanged.

## Configuration and Security

Compose supplies `EXVO_AUTH_MYSQL_CONNECTION_STRING`,
`EXVO_CATALOG_MYSQL_CONNECTION_STRING`, `EXVO_BOOKING_MYSQL_CONNECTION_STRING`,
and `EXVO_CHECKIN_MYSQL_CONNECTION_STRING` to their respective services.
Auth and Catalog connect to Azure with `SslMode=Required`; Booking and Check-In
connect to their dedicated Compose MySQL containers. Appsettings and IDE launch
profiles are host-development defaults; they do not set Compose ports or routing.

Backend containers run in Development for local use. Never commit passwords,
tokens, or `.env.local`. Production requires appropriate secrets and configuration.

### Azure Container Apps: Gateway and Catalog

Both Dockerfiles use .NET 8 and listen on container port `8080`. Build from this
backend repository root and configure Container Apps ingress to target port `8080`.
Set `ASPNETCORE_ENVIRONMENT=Production` for both applications.

Configure these environment variables on the indicated Container App:

| Application | Variable | Value |
| --- | --- | --- |
| Gateway | `Cors__AllowedOrigins__0` | `https://<frontend-name>.azurestaticapps.net` |
| Gateway | `ReverseProxy__Clusters__catalog-cluster__Destinations__catalogServiceDestination__Address` | `http://exvo-catalog` |
| Catalog | `EXVO_CATALOG_MYSQL_CONNECTION_STRING` | Secret reference containing `Server=<mysql-host>;Port=3306;Database=exvo_catalog_db;User ID=<mysql-user>;Password=<mysql-password>;SslMode=Required;` |

Specify the frontend origin exactly (scheme and hostname, normally without a
trailing slash or path). Additional origins use indices `__1`, `__2`, and so on.
Production startup fails if no nonempty origin is configured. Localhost origins
are supplied only by Development settings; Production does not add them automatically.
Credentialed CORS requires explicit origins, not `*`.

Store the actual database connection string in an Azure Container App secret and
reference it from the Catalog environment variable. Keep `SslMode=Required`.
Do not commit real connection strings, passwords, tokens, or environment files.
The YARP destination remains configurable; Compose retains `http://catalogservice:8080`.

Anonymous liveness endpoints return HTTP 200 with the service name and
`status: healthy`, without querying MySQL or downstream services:

- Gateway: `http://localhost:5000/health`
- Catalog: `http://localhost:5255/health`

These are liveness checks, not database readiness checks. Catalog's existing
database seeding runs before the HTTP listener starts and can delay initial startup
when MySQL is unavailable; subsequent health requests do not access the database.
For a focused Compose smoke test with existing safe database settings, use
`docker compose --env-file .env.local up --build -d --no-deps catalogservice apigateway`
to avoid starting the existing Auth/Kafka dependencies, then check both health URLs
and `http://localhost:5000/api/catalog/events`.

### Inspecting Profile Pictures in MySQL Shell

`Users.ProfilePicture` stores the full Base64 data URL so the frontend can render the image. Do not replace that value with `YES`; use this query when checking whether an image exists:

```sql
USE exvo_auth_db;

SELECT
  Id,
  FullName,
  Email,
  CASE
    WHEN NULLIF(TRIM(ProfilePicture), '') IS NULL THEN 'NULL'
    ELSE 'YES'
  END AS HasProfilePicture
FROM Users
ORDER BY Id DESC;
```

To hide MySQL Shell's table borders while inspecting results, run this before the query:

```text
\option resultFormat tsv
```

Restore the normal table display with:

```text
\option resultFormat table
```

The gateway permits the configured local frontend origins. The Auth service currently uses a permissive development CORS policy; narrow it when the frontend and deployment environments are finalized.

## Repository Structure

```text
src/
  gateway/ApiGateway/          YARP gateway
  services/AuthService/        Authentication and persistence
  services/BookingService/     Booking domain service
  services/CatalogService/     Catalog domain service
  services/CheckInService/     Check-in domain service
  services/TicketService/      Ticket domain service
  shared/Exvo.Shared/          Shared project
tests/
  AuthService.Tests/           Auth tests
  BookingService.Tests/        Booking tests
  CatalogService.Tests/        Catalog tests
  CheckInService.Tests/        Check-In tests
  TicketService.Tests/         Ticket consumer tests
docker-compose.yml            Backend services, Kafka, and local database containers
ExvoPlatform.sln               Solution file
.env.example                  Safe template for ignored .env.local
```

## SE3112 — Unit Testing & Test Doubles

Owner: Dasun — IT24103461

### Tools and setup
- .NET 8 SDK
- xUnit
- Custom HTTP stubs implementing IHttpClientFactory and HttpMessageHandler
- Run commands from the repository root.
- dotnet test automatically restores the required NuGet packages.

### Individual contribution
Added seven unit test cases for BookingService's
EventInventoryCatalogClient:

- Four parameterized ticket-quantity cases: null, zero, one and ten.
- Two parameterized server-error cases: HTTP 500 and 503.
- One missing-event case: HTTP 404 returns null.

The tests call the production client directly and replace its HTTP
dependency with controlled stub responses. These seven tests do not
require a running CatalogService, database or Azure deployment.

### Run my seven tests
```bash
dotnet test tests/BookingService.Tests/Exvo.BookingService.Tests.csproj --filter "FullyQualifiedName~TicketQuantityUnitTests|FullyQualifiedName~CatalogErrorUnitTests"
```

### Run all BookingService tests
```bash
dotnet test tests/BookingService.Tests/Exvo.BookingService.Tests.csproj
```

### Automation
The existing backend-ci.yml workflow discovers test projects under
tests/ and runs them on pushes to develop/main and pull requests
targeting develop/main. It uploads TRX test results as artifacts.

### Scope
These unit tests verify client mapping and error propagation using
controlled responses. They do not verify communication with the real
CatalogService or the complete booking workflow.

### Additional contribution — CheckInService

Owner: Dasun

Added five parameterized unit test cases for
EventAnalyticsService.ResolveVenueCapacity:

- Sum ticket-tier quantities.
- Fall back to available tickets when tiers are missing.
- Ensure capacity is at least the number of sold tickets.
- Handle zero capacity and zero sales.
- Fall back safely when ticket-tier JSON is invalid.

This method has no external dependencies, so these tests need no mocks.

Run:
```bash
dotnet test tests/CheckInService.Tests/Exvo.CheckInService.Tests.csproj --filter FullyQualifiedName~VenueCapacityUnitTests
```

Total new individual test cases: 12
- BookingService: 7
- CheckInService: 5

