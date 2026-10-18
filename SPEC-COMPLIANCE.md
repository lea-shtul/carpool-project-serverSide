# Spec compliance — §79 final checklist

Every item from the teacher's final requirements checklist (spec §79), with the file(s)
that satisfy it. Paths are relative to the solution root.

## Architecture

| Item | Where |
| --- | --- |
| Four projects | `Carpool.sln` — `Carpool.Core`, `Carpool.Data`, `Carpool.Service`, `Carpool.API` |
| Correct dependency direction | `*.csproj` `ProjectReference`s: API → Service/Data/Core, Service → Core, Data → Core. Nothing points outward. |
| Core independent from EF | `Carpool.Core.csproj` has **no** package references; entities are POCOs, `Ride.RowVersion` is a plain `uint`. |
| Controllers do not inject DbContext | `Carpool.API/Controllers/*` — constructors take service interfaces only; `ApiControllerBase` has no data dependency. |
| Constructor injection | Every service, repository, controller, middleware and the background service takes its dependencies via the constructor. No `new` of a collaborator anywhere. |
| Correct DI lifetimes | `Program.cs` — `DbContext`, all repositories, `IUnitOfWork` and all services `AddScoped`; `RideStatusBackgroundService` `AddHostedService` (singleton) resolves scoped services via `IServiceScopeFactory` per tick. |

## REST

| Item | Where |
| --- | --- |
| Plural resource names | `/api/users`, `/api/vehicles`, `/api/rides`, `/api/bookings`, `/api/ratings`, `/api/tags` |
| Correct HTTP methods | `GET` reads, `POST` create, `PUT` vehicle update, `PATCH` state transitions (ride `status`, user `status`), `DELETE` vehicle / booking (soft). No action verbs in the URL — no `/cancel`, `/complete` (§13, §67). |
| 200 | every `GET` and the `PATCH` transitions |
| 201 + Location | `VehiclesController.Create`, `RidesController.Create`, `BookingsController.Create` (→ `GetById`); `AuthController.Register` (→ `UsersController.GetMe`); `RatingsController.Create` (→ `GetForUser`); `TagsController.Create` (→ `GetAll`) — all via `CreatedAtAction` |
| 204 | `VehiclesController.Delete`, `BookingsController.Cancel` → `NoContent()` |
| 400 | `[ApiController]` model validation → `ApiBehaviorOptions.InvalidModelStateResponseFactory` (`Program.cs`, same `ErrorResponse` shape); `BusinessRuleException` → `ExceptionHandlingMiddleware` |
| 401 | JWT bearer challenge + `UnauthorizedException` |
| 403 | `ForbiddenException` (ownership checks in services) |
| 404 | `NotFoundException` |
| 409 | `ConflictException` / `ConcurrencyConflictException` / Postgres `23505` in `ExceptionHandlingMiddleware` |
| Swagger documentation | `Program.cs` `AddSwaggerGen` + `Carpool.API/Swagger/DefaultErrorResponsesOperationFilter.cs`; every action has an XML `<summary>` |

## Model binding

| Item | Where |
| --- | --- |
| FromRoute | `[FromRoute] int id` on every id-taking action |
| FromQuery | `RidesController.Search([FromQuery] RideQueryParameters query)` |
| FromBody | every create/update action; `[ApiController]` infers it and it is written explicitly |

## Validation

| Item | Where |
| --- | --- |
| Data Annotations | `Carpool.Core/DTOs/**` — `[Required]`, `[EmailAddress]`, `[Range]`, `[StringLength]`, `[Phone]` |
| ModelState | `[ApiController]` auto-400, re-shaped to the API's own `ErrorResponse` by `ApiBehaviorOptions.InvalidModelStateResponseFactory` (`Program.cs`) instead of the default `ValidationProblemDetails` |
| Business validation in Service | all rules (ownership, seats, state transitions, rating eligibility) live in `Carpool.Service/Services/*`; controllers contain none |

## Database

| Item | Where |
| --- | --- |
| PostgreSQL | `UseNpgsql` in `Program.cs` |
| Npgsql EF Core provider | `Npgsql.EntityFrameworkCore.PostgreSQL` in `Carpool.Data.csproj` |
| Code First | `Carpool.Data/CarpoolDbContext.cs` + `Migrations/` |
| DbContext | `Carpool.Data/CarpoolDbContext.cs` |
| DbSet | one `DbSet<T>` per entity in `CarpoolDbContext` |
| At least 2 migrations | `Carpool.Data/Migrations/` — `InitialSchema`, `SeedData`, `AddTagOwnership` |
| Configuration-based connection string | `builder.Configuration.GetConnectionString("CarpoolDb")`; value in User Secrets / env var, empty placeholder in `appsettings.json` |
| Seed data | `Carpool.Data/Seed/SeedData.cs` via `HasData` (the `SeedData` migration) — 4 tags, 4 users, 3 vehicles, 2 rides |

## EF Core

| Item | Where |
| --- | --- |
| Relationships / One-to-many | `Carpool.Data/Configurations/*` — `User`→`Vehicles`/`Rides`/`Bookings`, `Ride`→`Bookings`, etc. |
| Many-to-many | `Ride` ↔ `Tag` through `RideTag` (`RideTagConfiguration` — composite key + two FKs) |
| Fluent API | `Carpool.Data/Configurations/*.cs` (one `IEntityTypeConfiguration<T>` per entity), applied via `ApplyConfigurationsFromAssembly` |
| Include / ThenInclude | `RideRepository.GetDetailedByIdAsync` / `SearchAsync` — `Include(Driver)`, `Include(Vehicle)`, `Include(RideTags).ThenInclude(Tag)` |
| No N+1 | the graph is loaded in one query per the above; verified in the Stage 6 repository smoke test |
| AsNoTracking | every read-only repository query (`GetAll`, `GetByOwner`, `GetDetailedById`, `Search`, `GetByDriver`, …) |
| LINQ filtering | `RideRepository.SearchAsync` — conditional `Where` on origin/destination (`EF.Functions.ILike`), tag, price range, `availableOnly` |
| LINQ sorting | `RideRepository.SearchAsync` — `OrderBy`/`OrderByDescending` on `departureTime` / `price` / `availableSeats`, stable `ThenBy(Id)` |
| Database-level Skip/Take pagination | `RideRepository.SearchAsync` — `CountAsync` then `.Skip((page-1)*pageSize).Take(pageSize)` before `ToListAsync` |

## DTO

| Item | Where |
| --- | --- |
| No entity exposure | controllers and services return `*Response` DTOs only |
| Request DTOs | `Carpool.Core/DTOs/**/{Create,Update,…}Request.cs`, `RideQueryParameters` |
| Response DTOs | `Carpool.Core/DTOs/**/*Response.cs`; `UserResponse` has no `PasswordHash` |
| AutoMapper Profiles | `Carpool.Service/Mapping/*Profile.cs` (6 profiles); registered in `Program.cs`; validated by `AssertConfigurationIsValid` in tests/smoke |

## Async

| Item | Where |
| --- | --- |
| Controller async | every action is `public async Task<ActionResult<T>>` |
| Service async | every service method is `async Task<…>` |
| Repository async | every repository method is `async Task<…>` / returns `Task` |
| DbContext async | `ToListAsync`, `FirstOrDefaultAsync`, `AnyAsync`, `SaveChangesAsync`, `MigrateAsync` |
| CancellationToken throughout | declared on every interface method and flowed controller → service → repository → EF; no `.Result` / `.Wait()` |

## Authentication

| Item | Where |
| --- | --- |
| Registration / Login | `AuthController` + `AuthService` (`Carpool.Service/Services/AuthService.cs`) |
| Password hashing | `IPasswordHasher<User>` (`PasswordHasher<User>`, PBKDF2) in `AuthService`; seed hashes too |
| JWT | `Carpool.Service/Security/JwtTokenGenerator.cs`; `AddJwtBearer` in `Program.cs` |
| Claims | `sub` (user id), `email`, `role` (`ClaimTypes.Role`), `jti` |
| Server-side validation | `TokenValidationParameters` — issuer, audience, lifetime, signing key; `MapInboundClaims = false` |
| User role / Admin role | `UserRole` enum; `[Authorize]` and `[Authorize(Roles = "Admin")]` |
| Genuine authorization differences | user → own resources only; Admin → `GET /api/users`, `PATCH /api/users/{id}/status`, act on any vehicle/ride. Role is never taken from the request (`AuthService` forces `UserRole.User` on register). |

## Middleware

| Item | Where |
| --- | --- |
| Global exception middleware | `Carpool.API/Middleware/ExceptionHandlingMiddleware.cs` |
| Consistent JSON errors | `Carpool.API/Middleware/ErrorResponse.cs` — `{ statusCode, message, correlationId }` |
| Correlation ID | `Carpool.API/Middleware/CorrelationIdMiddleware.cs` — honour/generate, `X-Correlation-ID` header, `ILogger` scope |
| Correct middleware order | `Program.cs` — Exception → CorrelationId → RequestLogging → HttpsRedirection → Authentication → Authorization → MapControllers |

## Logging

| Item | Where |
| --- | --- |
| NLog | `NLog.Web.AspNetCore` + `Carpool.API/nlog.config`; `builder.Host.UseNLog()` |
| ILogger | all logging goes through `ILogger<T>` |
| Request logging | `Carpool.API/Middleware/RequestLoggingMiddleware.cs` — method, path, status, duration, correlation id |
| Correlation ID in logs | `${scopeproperty:item=CorrelationId}` in the `nlog.config` layout |
| Errors logged | `ExceptionHandlingMiddleware` — `LogError` with stack trace for 500s |
| Conflicts logged as Warning | `ExceptionHandlingMiddleware` — domain exceptions (400/401/403/404/409) `LogWarning` |
| No passwords/tokens logged | `nlog.config` layout logs path only; middleware never logs bodies; framework noise suppressed |

## Concurrency

| Item | Where |
| --- | --- |
| Optimistic concurrency | `RideConfiguration` — `RowVersion` `IsConcurrencyToken()` |
| PostgreSQL-compatible concurrency token | `RowVersion` mapped to the `xmin` system column (`HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate()`) |
| No SQL Server rowversion | no `[Timestamp]`, no `byte[]`, no `rowversion` anywhere |
| DbUpdateConcurrencyException handled | `CarpoolUnitOfWork.SaveChangesAsync` catches it → `ConcurrencyConflictException` |
| 409 Conflict | `ExceptionHandlingMiddleware` maps `ConcurrencyConflictException` → 409 |
| Booking race condition protected | `BookingService.CreateAsync` decrements seats on the tracked ride inside `ExecuteInTransactionAsync`; the check constraint `AvailableSeats >= 0` is the DB backstop |
| Two-DbContext concurrency test | `Carpool.Tests/Concurrency/RideSeatConcurrencyTests.cs` |

## Business Logic

| Item | Where |
| --- | --- |
| Limited-resource booking | `BookingService.CreateAsync` |
| Seat validation | `BookingService.CreateAsync` (`AvailableSeats < NumberOfSeats` → 400); `RideService.CreateAsync` (`TotalSeats <= vehicle.PassengerCapacity`) |
| Duplicate active booking protection | `BookingService.CreateAsync` — `HasActiveBookingAsync` → 409; filtered unique index `Booking(RideId, PassengerId) WHERE Status='Active'` |
| Booking cancellation returns seats | `BookingService.CancelAsync` — `booking.Ride.AvailableSeats += NumberOfSeats` in a transaction |
| Ride cancellation cancels active bookings | `RideService.CancelAsync` — ride `Cancelled` + each active booking `Cancelled` in one transaction |
| Ride completion completes active bookings | `RideService.CompleteAsync` + `RideStatusService.CompleteDueRidesAsync` — ride `Completed` + each active booking `Completed` in one transaction |
| Automatic status transitions | `Carpool.API/BackgroundServices/RideStatusBackgroundService.cs` + `Carpool.Service/Services/RideStatusService.cs` |
| Manual completion | `RidesController.UpdateStatus` (`{"status":"Completed"}`) → `RideService.CompleteAsync` (driver-or-Admin, eligibility, transaction) |
| Rating only after completed booking | `RatingService.CreateAsync` — ride `Completed` + reviewer has a `Completed` booking |
| One rating per ride/reviewer | `RatingService.CreateAsync` — `ExistsForReviewerAsync` → 409; unique index `Rating(RideId, ReviewerId)` |
| Vehicle deletion protection | `VehicleService.DeleteAsync` — `IsReferencedByAnyRideAsync` → 409, any ride status |
| Historical data preserved | bookings never deleted (status only); all FKs `DeleteBehavior.Restrict` except the `RideTag` join |

## Extensions beyond the base spec

Features added on top of the §79 checklist, at the student's initiative — called out here so
it's clear these go beyond, rather than fill a gap in, the teacher's requirements.

| Item | Spec says | What was added | Where |
| --- | --- | --- | --- |
| Global + private tags | §30: tags are predefined; §63: "Tags are predefined and therefore do not require user CRUD." | Every tag still carries a nullable `OwnerId` — `null` keeps the 4 seeded tags global and read-only for everyone, exactly as §30 describes. A tag with `OwnerId` set is a **private** tag an authenticated user created for themselves via the new `POST /api/tags`; `GET /api/tags` now requires authentication and returns the global tags plus the caller's own. A ride's tags (global or private) are visible to anyone viewing that ride, but a private tag can only be *attached* to a new ride by its owner. Tag names are unique within their scope — once among global tags, independently once per owner's private set. | `Carpool.Core/Entities/Tag.cs`, `Carpool.Core/DTOs/Tags/*`, `Carpool.Data/Configurations/TagConfiguration.cs`, `Carpool.Data/Migrations/AddTagOwnership`, `Carpool.Data/Repositories/TagRepository.cs`, `Carpool.Service/Services/TagService.cs`, `Carpool.Service/Services/RideService.cs` (ownership check on ride creation), `Carpool.API/Controllers/TagsController.cs` |
| Ride departure must be in the future | Not addressed — §11/§12 cover seat and ownership rules for `POST /api/rides` but say nothing about `DepartureTime` itself. | `RideService.CreateAsync` rejects a `DepartureTime` at or before the current UTC instant with a `BusinessRuleException` → `400`, alongside the existing seat/ownership checks. The React client mirrors this for UX: the date picker's `min` is set to "now", and a matching client-side check shows the same message as a fallback if that's ever bypassed — but the backend check is authoritative. | `Carpool.Service/Services/RideService.cs`; `carpool-client/src/components/rides/CreateRideForm.jsx` |
| Docker + single-service cloud deploy | §76 lists the fixed stack and explicitly excludes "Docker, cloud deploy, caching, SignalR, CI/CD, external services." | A multi-stage `Dockerfile` builds the React client and the API and serves both from one ASP.NET Core process (React's build output copied into `wwwroot`), for a one-URL deploy on Render with a Render PostgreSQL database. Supporting `Program.cs` changes: migrations run on startup in every environment (not just Development, since a container has no separate migration step); Swagger stays enabled in Production; `UseHttpsRedirection` only runs in Development (Render terminates TLS at its edge); `UseDefaultFiles`/`UseStaticFiles`/`MapFallbackToFile("index.html")` serve the SPA and let client-side routes survive a hard refresh; and when `ConnectionStrings:CarpoolDb` is empty, a connection string is built from Render's `DATABASE_URL` URI instead via `DatabaseUrlConverter` (Npgsql needs key=value format, not a URI; handles Render's Internal Database URL omitting the port, and uses `SSL Mode=Prefer` since Render's internal connections aren't necessarily TLS). | `Dockerfile`, `.dockerignore`, `Carpool.API/Program.cs`, `Carpool.Core/Configuration/DatabaseUrlConverter.cs`, `Carpool.Tests/Configuration/DatabaseUrlConverterTests.cs` |
| Driver's view of a ride's bookings | §33 lists `BookingResponse` (passenger's own view: no passenger name) and §52's endpoint list has no "bookings for a ride" route at all. | New `GET /api/rides/{rideId}/bookings`, driver (or Admin) only — a passenger, or any other driver, gets `403`. Returns the new `RideBookingResponse` (adds the passenger's name, which the passenger-facing `BookingResponse` deliberately omits). `RideDetailsPage` shows a "View Bookings" toggle only to the ride's own driver. | `Carpool.Core/DTOs/Bookings/RideBookingResponse.cs`, `Carpool.Core/Interfaces/{Repositories/IBookingRepository.cs,Services/IBookingService.cs}`, `Carpool.Data/Repositories/BookingRepository.cs`, `Carpool.Service/Services/BookingService.cs`, `Carpool.Service/Mapping/BookingProfile.cs`, `Carpool.API/Controllers/BookingsController.cs`, `Carpool.Tests/Services/BookingServiceTests.cs`; `carpool-client/src/{api/bookings.js,components/bookings/RideBookingsPanel.jsx,pages/RideDetailsPage.jsx}` |

## Submission

| Item | Where |
| --- | --- |
| Separate Server GitHub repository | this repository (`git init` done; push steps in `README.md`) |
| Separate Client GitHub repository | the React client is a **separate** deliverable/repository, not in this solution |
| Correct .gitignore | `.gitignore` — `bin/`, `obj/`, `.vs/`, `logs/`, `*.log`, `appsettings.Development.json`, `secrets.json`, `*.secrets` |
| No secrets committed | connection string and `Jwt:Key` live only in User Secrets / env vars; `appsettings.json` placeholders are empty |
| README | `README.md` |
| Local PostgreSQL setup instructions | `README.md` → *Prerequisites* + *Configuration* |
| Migration instructions | `README.md` → *Database — migrations* |
| Demo users/roles documented | `README.md` → *Seed / demo data* |
