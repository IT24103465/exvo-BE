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

The repository-local tool manifest (`dotnet-tools.json`) pins Stryker.NET 4.16.0.
The application and tests target .NET 8, and Stryker 4.x requires the .NET 8
runtime. Install the .NET 8 SDK (which includes the runtime), and ensure NuGet is
reachable for the initial restores. No database, Kafka, credentials, or running
Catalog service is needed. See the [Stryker 4.x to 5.x migration guide](https://stryker-mutator.io/docs/stryker-net/migration-guide/)
for runtime requirements and the [Stryker configuration reference](https://stryker-mutator.io/docs/stryker-net/configuration/)
for option syntax.

Mutation scope is deliberately limited to
`src/services/BookingService/Services/EventInventoryCatalogClient.cs`. Its direct
contract tests cover ticket quantities, tier values and aliases, general-admission
fallback, Catalog HTTP errors, and event snapshots. Stryker filters to
`EventInventoryCatalogClientTests`, `TicketQuantityUnitTests`, and
`CatalogErrorUnitTests`; the full BookingService test project is run first. Startup,
API infrastructure, migrations, generated code, and unrelated services are not in
the mutation scope.

Run from the repository root in PowerShell:

```powershell
dotnet tool restore
dotnet restore tests\BookingService.Tests\Exvo.BookingService.Tests.csproj
dotnet test tests\BookingService.Tests\Exvo.BookingService.Tests.csproj --configuration Release --no-restore
Set-Location tests\BookingService.Tests
dotnet stryker --config-file stryker-config.json --log-to-file
```

Stryker writes HTML and JSON reports to
`tests/BookingService.Tests/StrykerOutput/<run-timestamp>/reports/` and diagnostic
logs to the sibling `logs/` directory. Open `mutation-report.html` to inspect
individual mutations and the tests that killed them. Generated output is ignored
by Git; the manifest and `stryker-config.json` are tracked.

The configured break threshold is **80%** (high: 90%, low: 80%). The first fresh
run against the original 27-test suite scored 23.26% (20 killed, 26 survived, 40
uncovered). Tests were added for missing observable mappings and edge cases; the
final local run scored 83.72% (72 killed, 14 survived, 0 uncovered, 0 timeouts).
The 80% gate is based on that measured result, not on the older ignored reports;
it is not lowered to accommodate CI. Five target-file mutants failed to compile
and 13 redundant block mutants were ignored by Stryker; neither is included in
the score. The 14 remaining survivors appear equivalent through the public
client contract: duplicate casing fallbacks are equivalent because property
lookup is case-insensitive, and a missing-property default `JsonElement` has the
same downstream result. Keep them visible and reassess if the parsing contract
changes; do not treat every survivor as a defect.

The **BookingService mutation tests** job is part of **Backend CI and Release
Validation** in `.github/workflows/backend-ci.yml`. It runs on pushes to
`develop` and `main`, pull requests targeting either branch, and manual
`workflow_dispatch`. The release gate requires this job to pass. To view a GitHub
result, open **Actions**, select that workflow and a run, then download the
`booking-mutation-<run-id>` artifact for
HTML/JSON reports and logs. No GitHub Actions run has been verified from this
local checkout; the results above are local only.

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
