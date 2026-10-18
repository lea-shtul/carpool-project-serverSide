# Manual API walkthrough (Swagger)

The ordered steps to exercise the whole API through Swagger UI — the Stage 15 manual test.
Covers every endpoint plus the important negative cases (401 / 403 / 404 / 409).

## Start

```bash
dotnet run --project Carpool.API
```

Open **`https://localhost:7276/swagger`**.

Request bodies below are ready to paste. Two seeded accounts are used:

| | email | password |
| --- | --- | --- |
| Admin | `admin@carpool.dev` | `Passw0rd!` |
| Driver (has a vehicle + rides) | `alice@carpool.dev` | `Passw0rd!` |

---

## 1. Anonymous endpoints

| Step | Call | Expect |
| --- | --- | --- |
| 1.1 | `GET /api/tags` | **200** — `Quiet`, `Music`, `PetsAllowed`, `SmokingForbidden` |
| 1.2 | `GET /api/rides` (no token) | **401** — protected |

---

## 2. Register + log in

| Step | Call | Body | Expect |
| --- | --- | --- | --- |
| 2.1 | `POST /api/auth/register` | see below | **201** + `Location: /api/users/me` — `role: "User"`, `isActive: true`, no `passwordHash` field |
| 2.2 | `POST /api/auth/register` again, same email | same | **409** — email already registered |
| 2.3 | `POST /api/auth/login` | `{ "email": "walk@carpool.test", "password": "Passw0rd!1" }` | **200** — `accessToken`, `expiresAtUtc`, `user` |
| 2.4 | `POST /api/auth/login` wrong password | `{ "email": "walk@carpool.test", "password": "nope" }` | **401** — "Invalid email or password." |

```json
// 2.1
{
  "firstName": "Walk",
  "lastName": "Through",
  "email": "walk@carpool.test",
  "password": "Passw0rd!1",
  "phoneNumber": "+972500001234"
}
```

**Now click `Authorize`** (top right), paste the `accessToken` from 2.3, confirm. Every
call below is sent with `Authorization: Bearer <token>`.

---

## 3. Users + role differences

| Step | Call | Expect |
| --- | --- | --- |
| 3.1 | `GET /api/users/me` | **200** — the "Walk Through" profile |
| 3.2 | `GET /api/users` (as this User) | **403** — Admin only |
| 3.3 | Re-`Authorize` with an **admin** token (log in as `admin@carpool.dev`), then `GET /api/users` | **200** — full list |
| 3.4 | `PATCH /api/users/3/status` `{ "isActive": true }` (admin) | **200** |

Switch back to the **Walk Through** token before section 4.

---

## 4. Vehicles (full CRUD + ownership)

| Step | Call | Body | Expect |
| --- | --- | --- | --- |
| 4.1 | `POST /api/vehicles` | see below | **201** + `Location` header; `ownerId` = your id (from the token, not the body) |
| 4.2 | `POST /api/vehicles` same `licensePlate` | same | **409** — plate already registered |
| 4.3 | `GET /api/vehicles/my` | | **200** — your one vehicle |
| 4.4 | `GET /api/vehicles/1` (alice's vehicle) | | **403** — not owner |
| 4.5 | `GET /api/vehicles/{yourId}` | | **200** |
| 4.6 | `PUT /api/vehicles/{yourId}` | change `manufacturer` / `passengerCapacity` | **200** |
| 4.7 | `DELETE /api/vehicles/{yourId}` (before you create a ride on it) | | **204** |

```json
// 4.1  — then recreate one after 4.7 for section 5
{
  "manufacturer": "Kia",
  "model": "Rio",
  "licensePlate": "WALK-001",
  "passengerCapacity": 4
}
```

---

## 5. Rides (create, search, cancel, complete)

Recreate a vehicle first (4.1) and note its `id` as `{vehicleId}`.

| Step | Call | Body | Expect |
| --- | --- | --- | --- |
| 5.1 | `POST /api/rides` | see below | **201** — `availableSeats == totalSeats`, `status: "Scheduled"`, `driver` = you, `tags` resolved |
| 5.2 | `POST /api/rides` with `totalSeats` > vehicle capacity | `totalSeats: 9` | **400** |
| 5.3 | `POST /api/rides` with a `tagIds` entry that doesn't exist | `"tagIds": [1, 999]` | **400** |
| 5.4 | `GET /api/rides?sortBy=price&sortDirection=desc&pageSize=5` | | **200** — `{ items, page, pageSize, totalCount, totalPages }`, your ride included |
| 5.5 | `GET /api/rides?origin=tel&availableOnly=true` | | **200** — case-insensitive partial match |
| 5.6 | `GET /api/rides/{yourRideId}` | | **200** — full driver/vehicle/tags projection |
| 5.7 | `PATCH /api/rides/{yourRideId}` | `{ "status": "Cancelled" }` | **200** — `status: "Cancelled"` |
| 5.8 | `PATCH /api/rides/{yourRideId}` (now Cancelled) | `{ "status": "Completed" }` | **400** — cannot complete a cancelled ride |

```json
// 5.1  — {vehicleId} from your recreated vehicle
{
  "vehicleId": {vehicleId},
  "origin": "Tel Aviv",
  "destination": "Eilat",
  "departureTime": "2031-06-01T07:00:00Z",
  "totalSeats": 3,
  "pricePerSeat": 40.0,
  "estimatedDurationMinutes": 300,
  "tagIds": [1, 2]
}
```

---

## 6. Bookings + the concurrency rule

Book **alice's seeded ride #1** (Tel Aviv → Jerusalem, 3 seats) — you are not its driver.

| Step | Call | Body | Expect |
| --- | --- | --- | --- |
| 6.1 | `GET /api/rides/1` | | note `availableSeats` (3) |
| 6.2 | `POST /api/rides/1/bookings` | `{ "numberOfSeats": 2 }` | **201** + `Location`; booking `status: "Active"` |
| 6.3 | `GET /api/rides/1` | | `availableSeats` is now **1** |
| 6.4 | `POST /api/rides/1/bookings` again | `{ "numberOfSeats": 1 }` | **409** — one active booking per ride |
| 6.5 | `GET /api/bookings/my` | | **200** — your one Active booking |
| 6.6 | `GET /api/bookings/{id}` | | **200** (owner) |
| 6.7 | `DELETE /api/bookings/{id}` | | **204** — booking row kept, `status: "Cancelled"`, `cancelledAt` set |
| 6.8 | `GET /api/rides/1` | | `availableSeats` back to **3** |
| 6.9 | Log in as **alice**, `POST /api/rides/1/bookings` | `{ "numberOfSeats": 1 }` | **400** — the driver cannot book their own ride |

The seat race itself (two callers, one seat, second gets **409**) is proven by the
automated test `Carpool.Tests/Concurrency/RideSeatConcurrencyTests.cs`.

---

## 7. Ratings

Ratings need a **Completed** ride with a **Completed** booking by the reviewer — the
seeded data has none, so this section is best verified by the automated
`RatingServiceTests`. To see it end to end manually you would: create a ride departing a
minute ago with a short duration, book it as another user, wait for the background service
to complete it (or `PATCH /api/rides/{id}` with `{ "status": "Completed" }` as the driver), then:

| Step | Call | Body | Expect |
| --- | --- | --- | --- |
| 7.1 | `POST /api/rides/{completedRideId}/ratings` (as the passenger) | `{ "score": 5, "comment": "Great ride" }` | **201** + `Location: /api/users/{driverId}/ratings` |
| 7.2 | same again | | **409** — one rating per ride per reviewer |
| 7.3 | `POST /api/rides/{id}/ratings` as the **driver** | | **400** — cannot rate your own ride |
| 7.4 | `GET /api/users/{driverId}/ratings` | | **200** — the rating |

---

## 8. Cross-cutting checks

- Every response carries an **`X-Correlation-ID`** header; send your own and it comes back
  unchanged.
- Every error body is `{ "statusCode", "message", "correlationId" }`.
- The console / `logs/carpool-*.log` shows one `INFO` line per request with the correlation
  id, and a `WARN` line for each 4xx business outcome — no request bodies, no tokens.

---

## Cleanup

The walkthrough leaves a test user + vehicle + booking in `carpool`. To reset to the
seeded state, drop and re-create:

```bash
dotnet ef database drop --project Carpool.Data --startup-project Carpool.API --force
dotnet ef database update --project Carpool.Data --startup-project Carpool.API
```
