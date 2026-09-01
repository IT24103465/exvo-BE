# Exvo Platform

Exvo is an event experience platform built as a distributed .NET application. Sprint 1 establishes the backend foundation: service boundaries, an API gateway, authentication, MySQL persistence, migrations, and the initial test structure.

## Sprint 1 Deliverables

- .NET 8 solution in `ExvoPlatform.sln`.
- YARP API gateway with `/api/auth/*` routing to the Auth service.
- Auth service with registration, login, JWT bearer authentication, and protected current-user access.
- BCrypt password hashing and default `Attendee` role assignment.
- Entity Framework Core MySQL persistence and the initial `Users` migration.
- Swagger/OpenAPI support for ASP.NET Core services.
- xUnit test projects for Auth and Booking services.
- Docker Compose configuration for a local MySQL 8 database.

## Service Overview

| Component | Responsibility | Sprint 1 status |
| --- | --- | --- |
| API Gateway | Client entry point and request routing | Auth route configured |
| Auth Service | Registration, login, JWT, and user profile access | Implemented |
| Catalog Service | Event and experience catalog | Scaffolded |
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
        +--> Catalog Service :5255       (future route)
        +--> Booking Service :5063       (future route)
        +--> Ticket Service :5230        (future route)
        +--> Check-In Service :5028      (future route)
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
- Docker Desktop with Docker Compose
- Visual Studio 2022 or VS Code with C# Dev Kit

```powershell
dotnet --version
```

## Run Locally

Run these commands from the repository root.

```powershell
docker compose up -d mysql-dev
dotnet restore .\ExvoPlatform.sln
dotnet build .\ExvoPlatform.sln
dotnet ef database update --project .\src\services\AuthService\ExvoAuthService.csproj
```

Start the Auth service in one terminal:

```powershell
dotnet run --project .\src\services\AuthService\ExvoAuthService.csproj --launch-profile http
```

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

Returns `201 Created` with user details and a JWT. New accounts receive the `Attendee` role.

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

Sprint 1 test projects:

- `tests/AuthService.Tests`
- `tests/BookingService.Tests`

The projects currently provide the xUnit foundation for expanding unit and integration coverage in later sprints.

## Configuration and Security

The checked-in MySQL connection string, database password, and JWT key are development-only values. Replace them with environment variables or a secret store before any deployment, and do not reuse them in staging or production.

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
```

## Next Sprint Candidates

- Implement Catalog, Booking, Ticket, and Check-In domain APIs.
- Add gateway routes for each implemented service.
- Add validation, consistent error responses, health checks, and integration tests.
- Move all secrets and environment-specific settings out of source-controlled configuration.
- Add containerization and deployment configuration for the application services.
