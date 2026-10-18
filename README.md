# Carpool Web API

A carpool / ride-sharing REST API built with **ASP.NET Core (.NET 9)**, **EF Core (Code
First)** and **PostgreSQL**. Final academic project.

The central requirement is the **limited-resource booking**: seats on a ride are a scarce
resource protected by PostgreSQL-compatible **optimistic concurrency** (the `xmin` system
column), so two passengers can never both take the last seat and `AvailableSeats` can never
go negative.

---

## The limited resource — seats on a ride

**The resource.** Every ride has a fixed number of seats (`Ride.AvailableSeats`). When the
last seat is taken, nobody else may book it.

**Where the contention happens.** Two passengers call `POST /api/rides/{rideId}/bookings`
on the same ride at the same moment, and both see that one seat is still free. Without
protection, both requests would pass the check, both would save, and the ride would end up
with more passengers than seats. Cancelling a booking (which returns seats to the ride) and
cancelling or completing the ride itself update the same row, so they compete too.

**How it is protected.** `Ride` uses optimistic concurrency with PostgreSQL's `xmin` system
column as its concurrency token (`RideConfiguration`). Inside a single transaction,
`BookingService` loads the ride, runs every business check that depends on its current
state (status, departure time, free seats, duplicate booking), decreases `AvailableSeats`
and saves. EF Core adds the original `xmin` to the `UPDATE … WHERE` clause, so if another
request changed the ride in the meantime, no row matches and EF Core throws
`DbUpdateConcurrencyException`.

**How the system responds.** `CarpoolUnitOfWork` catches that specific exception and turns
it into a `ConcurrencyConflictException`. The exception middleware returns **409 Conflict**
with a clear JSON message, and the event is logged at `Warning` level with its correlation
id. The first request succeeds; the second gets 409 and the client tells the user the seat
was just taken and to try again. Two database rules add a second line of defence: a check
constraint keeps `AvailableSeats` between 0 and `TotalSeats`, and a filtered unique index
allows only one active booking per passenger per ride.

**Proof.** `Carpool.Tests/Concurrency/RideSeatConcurrencyTests.cs` opens two separate
`DbContext` instances on the same ride: both change the seats, the first save succeeds and
the second throws `DbUpdateConcurrencyException` (requires a local PostgreSQL).

---

## Documentation

| Document | What |
| --- | --- |
| [`SPEC-COMPLIANCE.md`](SPEC-COMPLIANCE.md) | every item of the assignment's final checklist mapped to the file that satisfies it |
| [`docs/SWAGGER-WALKTHROUGH.md`](docs/SWAGGER-WALKTHROUGH.md) | ordered manual test script — every endpoint plus the 401/403/404/409 cases |
| [`Carpool.Tests/TESTS.md`](Carpool.Tests/TESTS.md) | full per-test catalogue and the mocking approach |
| **Carpool API Atlas** (interactive) | layer-by-layer reference — architecture, every entity, every endpoint, the concurrency flow — <https://claude.ai/code/artifact/444c3699-f57d-4565-94fb-53ac6376e226> |
| **Reading the Test Suite** (interactive) | how the tests are written and reviewed, with a checklist — <https://claude.ai/code/artifact/348b6284-0609-4e39-9262-8737dc5f880a> |

> The two interactive pages are private Claude artifacts — open them while signed in as the
> repo owner, or use the *Share* menu on each page to publish a public link.

---

## Architecture

Four projects, dependencies pointing inward only:

```
Carpool.API  ─►  Carpool.Service  ─►  Carpool.Core  ◄─  Carpool.Data
     └───────────────────────────────────────────────────┘
        (API references Data only to register EF Core in Program.cs)
```

| Project | Responsibility | Depends on |
| --- | --- | --- |
| `Carpool.Core` | Entities, enums, DTOs, repository/service **interfaces**, domain exceptions, `JwtOptions`. **No EF Core, no ASP.NET.** | — |
| `Carpool.Data` | `CarpoolDbContext`, Fluent API configurations, migrations, seed data, repository **implementations**, `CarpoolUnitOfWork`. | Core |
| `Carpool.Service` | Business logic and validation, AutoMapper profiles, `AuthService` + `JwtTokenGenerator`, `RideStatusService`. | Core |
| `Carpool.API` | Controllers, exception/correlation middleware, JWT bearer pipeline, NLog, Swagger, the ride-status `BackgroundService`, DI wiring. | Core, Data, Service |
| `Carpool.Tests` | xUnit + Moq service unit tests and the two-`DbContext` concurrency test. | Core, Data, Service |

- Controllers never inject `DbContext`; they call services through interfaces.
- The Service layer never references EF Core — `IUnitOfWork` is the transaction boundary and
  the one place EF's `DbUpdateConcurrencyException` is translated to a domain exception.
- All repository/service methods are `async` and flow a `CancellationToken` end to end.

---

## Prerequisites

- **.NET 9 SDK**
- **PostgreSQL 14+** running locally (default port `5432`)
- **EF Core CLI tools:** `dotnet tool install --global dotnet-ef`
  (or `dotnet tool update --global dotnet-ef`)

---

## Configuration

No secrets are committed. Both values below live in **User Secrets** on the `Carpool.API`
project in Development, or as environment variables elsewhere.
`appsettings.json` holds only non-secret settings (issuer, audience, token lifetime,
background-service interval) with empty placeholders for the secrets.

### 1. Database connection string

```bash
cd Carpool.API
dotnet user-secrets set "ConnectionStrings:CarpoolDb" \
  "Host=localhost;Port=5432;Database=carpool;Username=postgres;Password=YOUR_PASSWORD"
```

Replace `YOUR_PASSWORD` (and `Username` if different) with your local PostgreSQL
credentials. The `carpool` database does not need to exist beforehand — EF creates it.
Outside Development, use the `ConnectionStrings__CarpoolDb` environment variable.

### 2. JWT signing key

```bash
cd Carpool.API
dotnet user-secrets set "Jwt:Key" "$(openssl rand -base64 64)"
```

(Any string of ~256 bits or more works; the app refuses to start if `Jwt:Key` is missing.)
Outside Development, use the `Jwt__Key` environment variable.

---

## Database — migrations

EF Core Code First, **three migrations**:

1. **`InitialSchema`** — all tables, keys, foreign keys, unique indexes (`User.Email`,
   `Vehicle.LicensePlate`, `Tag.Name`, `Rating(RideId, ReviewerId)`, filtered
   `Booking(RideId, PassengerId)` where `Status = 'Active'`), check constraints, the
   `RideTag` composite key, the `Ride` ↔ `Tag` many-to-many, and the PostgreSQL `xmin`
   optimistic-concurrency token on `Ride`.
2. **`SeedData`** — demo tags, users, vehicles and rides (see below).
3. **`AddTagOwnership`** — adds `Tag.OwnerId` (nullable FK to `User`) for the global/private
   tags extension (see *Tags — global + private* below); replaces the single unique index on
   `Tag.Name` with two filtered ones, scoped to global tags and to each owner's private set.

Apply them:

```bash
# from the solution root
dotnet ef database update --project Carpool.Data --startup-project Carpool.API
```

…or just run the API in Development — pending migrations are applied automatically on
startup (`Database.MigrateAsync()` in `Program.cs`, Development only).

Add a new migration:

```bash
dotnet ef migrations add <Name> --project Carpool.Data --startup-project Carpool.API
```

---

## Running the API

```bash
dotnet run --project Carpool.API
```

Default URLs (see `Carpool.API/Properties/launchSettings.json`):
`https://localhost:7276` · `http://localhost:5096`

- **Swagger UI:** `https://localhost:7276/swagger` (Development only). Every endpoint is
  documented; use the **Authorize** button to paste a JWT and exercise the protected
  endpoints. A step-by-step manual test script is in
  [`docs/SWAGGER-WALKTHROUGH.md`](docs/SWAGGER-WALKTHROUGH.md).
- **Auth quick-start:** `POST /api/auth/register` → `POST /api/auth/login` → copy
  `accessToken` from the response → **Authorize** → `Bearer` is applied to every request.

---

## Seed / demo data

All demo users share the password **`Passw0rd!`** (demo only — not a real secret). The
stored value is a PBKDF2 / ASP.NET Core Identity V3 hash; plaintext is never stored or
logged.

| Email | Role | Vehicle |
| --- | --- | --- |
| `admin@carpool.dev` | **Admin** | — |
| `alice@carpool.dev` | User | Toyota Corolla (`111-11-111`, 4 seats) |
| `bob@carpool.dev` | User | Honda Civic (`222-22-222`, 3 seats) |
| `carol@carpool.dev` | User | Mazda 3 (`333-33-333`, 5 seats) |

Seeded tags (global, available to everyone): `Quiet`, `Music`, `PetsAllowed`,
`SmokingForbidden`. See *Tags — global + private* below for how users can add their own.

Seeded rides (both `Scheduled`, departing 2030-01-01 so the background service leaves them
alone):

| Route | Driver | Seats | Price/seat | Tags |
| --- | --- | --- | --- | --- |
| Tel Aviv → Jerusalem | alice | 3 | 25.00 | Quiet, SmokingForbidden |
| Haifa → Tel Aviv | bob | 2 | 30.00 | Music |

**Role differences:** a normal user manages only their own vehicles, bookings and rides;
`Admin` can list all users, activate/deactivate accounts (`PATCH /api/users/{id}/status`),
and act on any vehicle/ride.

---

## Endpoints

| Area | Endpoints |
| --- | --- |
| Auth | `POST /api/auth/register`, `POST /api/auth/login` |
| Users | `GET /api/users/me`; `GET /api/users`, `PATCH /api/users/{id}/status` *(Admin)* |
| Vehicles | `POST /api/vehicles`, `GET /api/vehicles/my`, `GET /api/vehicles/{id}`, `PUT /api/vehicles/{id}`, `DELETE /api/vehicles/{id}` |
| Rides | `POST /api/rides`, `GET /api/rides` *(filter/sort/page)*, `GET /api/rides/{id}`, `PATCH /api/rides/{id}` *(`{"status":"Cancelled"\|"Completed"}`)* |
| Bookings | `POST /api/rides/{rideId}/bookings`, `GET /api/bookings/my`, `GET /api/bookings/{id}`, `DELETE /api/bookings/{id}` *(soft cancel)* |
| Ratings | `POST /api/rides/{rideId}/ratings`, `GET /api/users/{userId}/ratings` |
| Tags | `GET /api/tags`, `POST /api/tags` |

Errors use one consistent body — `{ "statusCode", "message", "correlationId" }` — and map
expected business outcomes to `400 / 401 / 403 / 404 / 409` (never `500`).

---

## Tags — global + private

> This goes beyond the base assignment spec, which treats tags as predefined and read-only
> (see [`SPEC-COMPLIANCE.md`](SPEC-COMPLIANCE.md) → *Extensions beyond the base spec*). Added
> at the student's initiative as a deliberate enhancement.

`GET /api/tags` requires authentication (nothing else in this API is reachable
anonymously) and returns two kinds of tag:

- **Global** — the 4 seeded tags (`OwnerId` is `null`), visible to and usable by every user,
  exactly as the base spec describes.
- **Private** — created by a user for themselves via `POST /api/tags`, visible only in
  *their own* `GET /api/tags` response and only *they* can attach one to a new ride. Once
  attached, the tag's name is still shown to anyone viewing that ride.

Tag names are unique within their scope: once among the global tags, and independently once
per user's private set — two different users (or a user and the global list) may reuse the
same name without conflict.

---

## Ride status automation

A hosted `BackgroundService` reconciles ride status on a timer
(`RideStatusAutomation:IntervalSeconds` in `appsettings.json`, default 30 s, floor 5):

- `Scheduled → InProgress` when `DepartureTime` passes (sets `StartedAt`);
- `InProgress → Completed` when `DepartureTime + EstimatedDurationMinutes` passes (sets
  `CompletedAt`, and completes all of that ride's active bookings in the same transaction).

It opens a fresh DI scope per tick (`IServiceScopeFactory`) — it never holds a scoped
`DbContext`.

---

## Logging

**NLog** under the standard `ILogger<T>` (`nlog.config` in `Carpool.API`).

- Console + a daily rolling file: `Carpool.API/bin/.../logs/carpool-YYYY-MM-DD.log`
  (14-day retention). The `logs/` folder is git-ignored.
- Every request is logged once (method, path, status, duration, correlation id).
- Every request gets a correlation id (`X-Correlation-ID` — honoured from the request if
  present, otherwise generated, and echoed back on the response). It appears on every log
  line.
- Levels: `Debug` for our own diagnostics, `Information` for normal events, **`Warning` for
  handled conflicts** (the domain-exception mappings), `Error` for unexpected exceptions.
- Passwords, password hashes, tokens and request bodies are never logged.

---

## Tests

```bash
# unit tests only — no database needed
dotnet test Carpool.Tests --filter "Category!=Integration"

# everything, incl. the 2 concurrency tests (needs a local PostgreSQL)
dotnet test
```

**60 tests** — 58 service-layer unit tests (repositories mocked with Moq, real AutoMapper)
covering the spec §70 list (successful booking, insufficient seats, driver-books-own,
duplicate active booking, cancellations, ride completion, rating eligibility, vehicle
rules) + 2 integration tests for optimistic concurrency (spec §69): two `DbContext`
instances race on the same ride's seats → first save wins, second throws
`DbUpdateConcurrencyException`, which `CarpoolUnitOfWork` surfaces as
`ConcurrencyConflictException` (→ HTTP 409).

The concurrency tests use a throwaway `carpool_test` database (created/migrated/dropped by
a fixture; override with the `CARPOOL_TEST_DB` connection-string env var) and skip cleanly
if PostgreSQL is unavailable. Full per-test catalogue:
[`Carpool.Tests/TESTS.md`](Carpool.Tests/TESTS.md).

---

## Tech stack

.NET 9 · EF Core 9 + Npgsql (Code First) · PostgreSQL · REST · JWT bearer
(`Microsoft.AspNetCore.Authentication.JwtBearer`) · `IPasswordHasher<User>` (PBKDF2) ·
AutoMapper · xUnit + Moq · NLog · Swashbuckle (Swagger/OpenAPI) · `BackgroundService`.

---

## Deployment (Docker, beyond the base spec)

> Not part of the assignment spec, which explicitly excludes Docker/cloud deploy — see
> [`SPEC-COMPLIANCE.md`](SPEC-COMPLIANCE.md) → *Extensions beyond the base spec*.

The root [`Dockerfile`](Dockerfile) builds the whole app — React client + API — into one
image: a Node stage builds `carpool-client`, a .NET SDK stage publishes `Carpool.API`, and
the final `aspnet:9.0` stage serves both from one process (the React build is copied into
`wwwroot`). That means one container, one URL, one Render web service.

```bash
docker build -t carpool .
docker run -p 8080:8080 \
  -e Jwt__Key="$(openssl rand -base64 64)" \
  -e DATABASE_URL="postgres://user:password@host:5432/dbname" \
  carpool
```

Key differences from local dev, all in `Program.cs`: migrations run on startup in every
environment (a container has no separate migration step); Swagger stays enabled in
Production; `UseHttpsRedirection` is skipped outside Development (the platform's edge
terminates TLS); and if `ConnectionStrings:CarpoolDb` is empty, a connection string is
built from a `DATABASE_URL` env var via `DatabaseUrlConverter` (`Carpool.Core/Configuration`)
instead — the `postgres://user:pass@host[:port]/db` shape most managed Postgres providers
hand out. The port is optional: Render's *Internal* Database URL omits it (defaults to
5432), and the connection uses `SSL Mode=Prefer` rather than `Require` since Render's
internal network connections aren't necessarily TLS.

---

## Submitting to GitHub

The `.gitignore` already excludes `bin/`, `obj/`, `.vs/`, `logs/`, `*.log`,
`appsettings.Development.json` and User Secrets — nothing secret is tracked.

```bash
git init
git add .
git commit -m "Carpool Web API — server"
git branch -M main
git remote add origin https://github.com/<you>/<carpool-server>.git
git push -u origin main
```

The React client lives in its **own separate repository** (not part of this solution).
