# Exvo Backend

Exvo is an event experience platform built as a distributed .NET application. The backend includes service boundaries, an API gateway, authentication, MySQL persistence, migrations, and the initial test structure.

## Features

- .NET 8 solution in `ExvoPlatform.sln`.
- YARP API gateway routing `/api/auth/*` to AuthService and `/api/catalog/*` to CatalogService.
- Auth service with registration, login, JWT bearer authentication, and protected current-user access.
- BCrypt password hashing and default `Attendee` role assignment.
- Entity Framework Core MySQL persistence and the initial `Users` migration.
- Catalog service persistence in the `exvo_catalog_db` MySQL database.
- Swagger/OpenAPI support for ASP.NET Core services.
- xUnit test projects for Auth and Booking services.
- Docker Compose configuration for a local MySQL 8 database.

## Service Overview

| Component | Responsibility | Status |
| --- | --- | --- |
| API Gateway | Client entry point and request routing | Auth and Catalog routes configured |
| Auth Service | Registration, login, JWT, and user profile access | Implemented |
| Catalog Service | Categories, event browsing, and event management | Implemented |
| Booking Service | Booking and reservation workflows | Scaffolded |
| Ticket Service | Ticket lifecycle and access | Scaffolded |
| Check-In Service | Event check-in workflows | Scaffolded |
| Exvo.Shared | Shared project for cross-service code | Established |

```text
Client / Frontend
        |
        v
API Gateway :5000
        |
        +--> Auth Service :5284 --> MySQL :3306
        +--> Catalog Service :5255 --> MySQL :3306
Other scaffolded services (not routed by the gateway):
  Booking :5063, Ticket :5230, Check-In :5028

```

## Technology Stack

- .NET 8 and ASP.NET Core Minimal APIs
- Entity Framework Core 8 with Pomelo MySQL provider
- MySQL 8.0
- YARP Reverse Proxy
- JWT Bearer authentication
- BCrypt.Net-Next
- Swagger/OpenAPI
- xUnit and Microsoft.NET.Test.Sdk
- Docker Compose

## Prerequisites

- .NET 8 SDK
- Docker Desktop with Docker Compose (only for the local MySQL fallback)
- Visual Studio 2022 or VS Code with C# Dev Kit

```powershell
dotnet --version
```

## Local development startup

From this backend root (`exvo-be`), using Windows PowerShell 5.1 or PowerShell 7
and the .NET 8 SDK:

1. Copy the template: `Copy-Item .env.example .env.local`.
2. Edit `.env.local` and replace the placeholder with the Azure MySQL password.
3. Run:

```powershell
.\start-dev.ps1
```

If Windows reports that scripts are disabled, run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\start-dev.ps1`
instead. This applies only to that process and does not change the saved policy.

`.env.local` is ignored by the repository's Git rules and must never be committed.
Only `.env.example`, containing placeholders, belongs in source control. Use one
`AZURE_MYSQL_PASSWORD=...` line. Optional matching outer quotes are removed; the
value is otherwise literal (no variable expansion or inline comments). Quote the
value if leading or trailing spaces are part of the password.

The script sets SSL-required Azure MySQL connection strings for `exvo_auth_db`
and `exvo_catalog_db` in the launched processes and restores the calling terminal's
previous environment afterward. Secrets are not placed in command-line arguments.
It opens three PowerShell windows with the existing `http` launch profiles:

- AuthService: `src/services/AuthService/ExvoAuthService.csproj` — `http://localhost:5284`
- CatalogService: `src/services/CatalogService/Exvo.CatalogService.csproj` — `http://localhost:5255`
- API Gateway: `src/gateway/ApiGateway/ApiGateway.csproj` — `http://localhost:5000`

The `ApiGateway` project implements YARP routing; `Exvo.ApiGateway` is a separate
weather-forecast scaffold, despite its inclusion in the solution. Stop each service
with Ctrl+C in its window. Check each window for startup errors; launching a window
does not guarantee database connectivity or service readiness. Azure access and
existing database schemas are prerequisites. This script runs no migrations or
Azure resource commands. Catalog's existing startup category seeding still runs.
The manual local/Docker workflow below and its connection-string fallbacks remain
available when the service-specific environment variables are unset.

## Manual startup with local Docker MySQL

Run these commands from `exvo-be` (the directory containing `ExvoPlatform.sln`).

```powershell
docker compose up -d mysql-dev
dotnet restore .\ExvoPlatform.sln
dotnet build .\ExvoPlatform.sln
dotnet ef database update --project .\src\services\AuthService\ExvoAuthService.csproj
dotnet ef database update --project .\src\services\CatalogService\Exvo.CatalogService.csproj
```

The Compose file creates `exvo_auth_db` and mounts `docker/init` so MySQL can create
`exvo_catalog_db` from `docker/init/01-create-catalog-db.sql`. MySQL runs files in
`/docker-entrypoint-initdb.d` only when its data directory is initialized. If the
container already exists, use this non-destructive command once instead of deleting
the volume:

```powershell
docker exec -it exvo_mysql_dev mysql -uroot -p -e "CREATE DATABASE IF NOT EXISTS exvo_catalog_db;"
```

Enter the local MySQL root password when prompted. Check both databases directly:

```powershell
docker exec -it exvo_mysql_dev mysql -uroot -p -e "SHOW DATABASES;"
```

Only use the following reset when it is acceptable to delete all local MySQL data;
it causes the init script to run on the next startup:

```powershell
docker compose down -v
docker compose up -d mysql-dev
```

For Azure MySQL, use the local development startup script above.

Start the Auth service in one terminal:

```powershell
dotnet run --project .\src\services\AuthService\ExvoAuthService.csproj --launch-profile http
```

Start the Event Catalog service in another terminal:

```powershell
dotnet run --project .\src\services\CatalogService\Exvo.CatalogService.csproj --launch-profile http
```

The Catalog service adds missing frontend categories at startup: Music & Concerts,
Concert, Festival, Live Session, DJ Night, Acoustic, Stand-Up, and EDM Arena.
Existing categories and their IDs are preserved. Apply database migrations before
starting the service. Categories are available at `GET /api/catalog/categories`.

Start the gateway in another terminal:

```powershell
dotnet run --project .\src\gateway\ApiGateway\ApiGateway.csproj --launch-profile http
```

The gateway is available at `http://localhost:5000`. The Auth service is available directly at `http://localhost:5284`.

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

## Local Service URLs

| Service | HTTP | HTTPS |
| --- | --- | --- |
| API Gateway | `http://localhost:5000` | `https://localhost:7272` |
| Auth Service | `http://localhost:5284` | `https://localhost:7084` |
| Booking Service | `http://localhost:5063` | `https://localhost:7074` |
| Catalog Service | `http://localhost:5255` | `https://localhost:7247` |
| Check-In Service | `http://localhost:5028` | `https://localhost:7278` |
| Ticket Service | `http://localhost:5230` | `https://localhost:7175` |

Swagger is enabled in Development. Open `/swagger` on a running service that exposes Swagger.

## Testing

```powershell
dotnet test .\ExvoPlatform.sln
```

Test projects:

- `tests/AuthService.Tests`
- `tests/BookingService.Tests`

The test projects use xUnit for automated verification.

## Configuration and Security

The checked-in MySQL connection strings are local Docker development fallbacks. `EXVO_AUTH_MYSQL_CONNECTION_STRING` and `EXVO_CATALOG_MYSQL_CONNECTION_STRING` override them independently, so the Auth service uses only `exvo_auth_db` and the Event Catalog service uses only `exvo_catalog_db`. Azure connection strings must include `SslMode=Required`.

The root `docker-compose.yml` starts only the local MySQL fallback; it does not define backend application containers. If backend containers are added later, pass the same two service-specific variables into the corresponding container environments. Do not add the Azure password to Compose files or committed configuration.

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
docker-compose.yml             Local MySQL infrastructure
ExvoPlatform.sln               Solution file
start-dev.ps1                 Azure-backed local startup
.env.example                  Safe template for ignored .env.local
```

