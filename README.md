# dotnet-hotel

Room types, the rooms left on each night, and the stays that draw them down. The scarce number is **rooms left on a given night of a room type**, and the **Rooms** module is its only writer. A stay is all-or-nothing: one conditional `UPDATE` decrements every night of the stay that still has enough rooms, and the hold is kept only if it touched every night. When two guests fight for the last room, one commits a hold and the other is short, with no hold written and no night touched. A guest reference that contains the poison token `POISON` is committed as rejected by the Vouchers module, the message is dead-lettered, and a compensating command gives every night back. A later cancellation also gives every night back, but it does not roll back the voucher that was already issued.

One ASP.NET Core process, one Postgres database. An HTTP write saves the booking and an outbox row in one local transaction; a `BackgroundService` relay publishes outbox rows to RabbitMQ (the work queue) and Kafka (the event log); the next module consumes from RabbitMQ, claims an inbox key in the same transaction as its own write, and writes its own row and outbox row. Saga steps never call each other in-process. Delivery is at-least-once: the relay can publish and crash before it marks a row sent, so the row goes out again, and every consumer dedupes with its inbox key. Each saga step only moves a booking forward from the status it expects.

Everything behind the saga is signed in: **ASP.NET Core Identity** with **JWT access tokens and rotating refresh tokens** (see [Authentication and authorization](#authentication-and-authorization)). Customers see and cancel only their own bookings; staff see everything and open roomTypes.

> The demo credentials in `compose.yaml` and `appsettings.Development.json` are for local use only. Set `JWT_KEY` and `ADMIN_PASSWORD` (see `.env.example`) before putting this anywhere shared. Compose binds every port to `127.0.0.1`.

![The hotel UI signed in as the administrator, after two bookings fought for the last nights: one booking confirmed, one short](docs/screenshot.png)

*The UI reads everything from the API; shown signed in as the seeded administrator, on the roomType where two bookings fought for the last nights: one booking was confirmed, the other was told it is short.*

## Booking statuses

```mermaid
flowchart LR
    Requested -->|every night held| Held
    Requested -->|short on any night: nothing written| Rejected
    Held -->|voucher issued| Confirmed
    Held -->|poison reference: voucher rejected| Rejected
    Held -->|cancellation| Cancelled
    Confirmed -->|cancellation| Cancelled
```

`Cancelled` and `Rejected` (after a hold) give every night back through a `ReleaseRooms` command, and Rooms releases a hold at most once. The voucher has its own status (`Issued` or `Rejected`) and a cancellation never changes it. If the cancellation gets there before the final mark, the late mark finds the booking is no longer `Held` and does nothing.

## How the modules talk

```mermaid
sequenceDiagram
    participant C as Client
    participant B as Bookings
    participant R as Rooms
    participant V as Vouchers

    C->>B: POST /api/bookings
    Note over B: booking + outbox row, one transaction
    B-->>C: 202 Requested
    B->>R: rooms.reserve (relay → RabbitMQ, copy to Kafka)
    Note over R: inbox claim, conditional UPDATE on every night, hold, outbox row
    alt every night has enough rooms
        R->>V: rooms.held
        R->>B: rooms.held (Requested → Held)
        alt reference is clean
            Note over V: voucher, outbox row
            V->>B: voucher.issued (Held → Confirmed)
        else reference contains POISON
            Note over V: committed Rejected, then the message is dead-lettered
            V->>R: rooms.release (compensating command)
            V->>B: voucher.rejected (Held → Rejected)
            R->>B: rooms.released
        end
    else any night short
        Note over R: rolled back, nothing decremented
        R->>B: rooms.short (Requested → Rejected, no hold)
    end
    C->>B: POST /api/bookings/{id}/cancellation
    B->>R: rooms.release (Held or Confirmed → Cancelled)
    R->>B: rooms.released
```

| Module | Owns | Consumes | Emits |
| --- | --- | --- | --- |
| **Identity** (`libs/identity`) | `identity` schema: users, roles, refresh tokens. | | |
| **Bookings** (`libs/bookings`) | `Bookings`: status, stay dates, rooms, total, guest reference. Validation, the poison rule, the price. | `rooms.held`, `rooms.short`, `voucher.issued`, `voucher.rejected`, `rooms.released` | `rooms.reserve`, `rooms.release` (cancellation) |
| **Rooms** (`libs/rooms`) | `RoomTypes`, `RoomNights.RoomsLeft` (sole writer after a room type opens) and `RoomHolds`. | `rooms.reserve`, `rooms.release` (prefetch 1) | `rooms.held`, `rooms.short`, `rooms.released` |
| **Vouchers** (`libs/vouchers`) | `Vouchers`. | `rooms.held` | `voucher.issued`, `voucher.rejected`, `rooms.release` (poison) |
| **Shared** (`libs/shared`) | The relay, the consumer loop with retry and dead-letter, the Kafka log, auth primitives, middleware. Each module owns its own `OutboxMessages` and `InboxMessages` tables. | | |

RabbitMQ topology is declared in code (`libs/shared/Messaging/Topology.cs`): exchange `hotel.work`, one queue per module (`rooms.commands`, `vouchers.work`, `bookings.events`), a `.retry` queue per module (2 s TTL, then back to the main queue, 3 attempts), and a `.dlq` per module behind `hotel.dlx`. A transient failure retries; a poison message goes straight to the dead-letter queue after its rejection is committed. Kafka has one topic, `hotel.events`, one partition, replication factor 1, keyed by booking id.

## Architecture

One deployable (`apps/api`), several libraries. Each business module is its own class library with its own `DbContext`, its own Postgres **schema** (`identity`, `bookings`, `rooms`, `vouchers`), its own migrations and its own outbox and inbox tables. No module references another; the only shared code is `Hotel.Shared`, and the one cross-module read (each night, and the stays that draw them down's existence and pricing inputs) goes through an interface, `IRoomTypeCatalog`, that Rooms implements and Bookings consumes.

```mermaid
flowchart TB
    api["apps/api: composition root, middleware, endpoints"]
    api --> identity & bookings & rooms & vouchers
    identity["libs/identity: users, roles, JWT, refresh tokens"]
    bookings["libs/bookings: schema bookings"]
    rooms["libs/rooms: schema rooms"]
    vouchers["libs/vouchers: schema vouchers"]
    identity & bookings & rooms & vouchers --> shared["libs/shared: messaging, outbox/inbox, auth primitives, middleware"]
    bookings -. "IRoomTypeCatalog" .-> rooms
```

Modules still talk only through messages: RabbitMQ carries the commands, Kafka keeps the event log, and the outbox and inbox make delivery at-least-once and consumption idempotent.

## Authentication and authorization

| Piece | How it works |
| --- | --- |
| Accounts | ASP.NET Core Identity. Passwords need 10+ characters with upper case, lower case and a digit, and 4 distinct characters. Five wrong passwords (or codes) lock the account for 15 minutes (`429`). A wrong password and an unknown email give the same `401`. |
| Email | Registration mails a confirmation link; with `Auth:RequireConfirmedEmail` on (the default) sign-in is refused (`403`) until it is opened. "Forgot password" mails a one-time reset link and answers `202` for every address, known or not. Messages go through `IAuthMailer`: SMTP when `Mail:Host` is set, the log otherwise. The demo stack also keeps them in a dev mailbox at `/api/dev/mailbox` (off by default). Link tokens are protected with data-protection keys stored in Postgres, so every instance and every restart can read them. |
| Two-factor | TOTP (any authenticator app): `POST /api/auth/mfa/setup` returns the key and an `otpauth://` URI, `enable` proves it with a code and returns eight one-time recovery codes, `disable` needs the password and a code. Once on, sign-in needs a code (`401 A verification code is required.`). |
| Access token | JWT, 15 minutes, held **in memory** by the Angular app and sent as `Authorization: Bearer`. HS256 with a key of at least 32 bytes, or **RS256** when `Jwt:RsaPrivateKeyPem` is set, in which case the public key is served at `/.well-known/jwks.json` so other services can verify tokens without a secret. The API refuses to start without an usable key. |
| Revocation | Each token carries the user's session version. A password change, a reset, turning two-factor off and **"sign out everywhere"** bump it (one atomic `UPDATE`), and every token confirmed earlier stops working on every instance within `Jwt:RevocationCacheSeconds` (default 5). |
| Refresh token | 256 random bits in an `HttpOnly`, `SameSite=Strict`, `Path=/api/auth` cookie (`Secure` over HTTPS). Only its SHA-256 is stored. Every refresh **rotates** it with a conditional `UPDATE`, so two concurrent refreshes with the same token cannot both win. Presenting a token that was already used revokes the whole family. Refresh and logout also need an `X-Requested-With: hotel-web` header, so another site cannot trigger them. |
| Roles | `Customer` (default at registration), `Staff`, `Admin`. An administrator is seeded from configuration on first start. |
| Rules | Anonymous: `/api/health`, `/api/auth/*`. Any signed-in user: read roomTypes, book, read and cancel **their own** bookings and vouchers. `Staff` and `Admin`: open roomTypes, read everything, read the event log. Another user's booking or voucher is `404`, not `403`, so ids do not leak. The owner of a booking is the caller; a body field cannot set it. |
| Messaging | The outbox is drained by any number of relays at once (`FOR UPDATE SKIP LOCKED`, so no row is published twice at the same time) and trimmed on a schedule. Staff can look inside the dead-letter queues and replay them from the API. |
| Hardening | A rate limit on `/api/auth/*` per client (`RateLimit:AuthPerMinute`), **counted in Postgres** so every API instance shares one count, security headers on every response (`nosniff`, `DENY`, strict CSP, `no-referrer`), a correlation id echoed in `X-Correlation-Id` and in every problem response, JSON logs (`Logging:Json`), forwarded headers only when `ForwardedHeaders:Enabled`, a fallback policy that makes every endpoint authenticated unless it opts out, and a readiness endpoint that checks every module schema. |

## Run it

Needs Node 22 (npm 10), .NET SDK 10.0.401 (`global.json`), Docker (arm64 is fine) and python3 for the smoke script.

```bash
npm ci
dotnet test tests/Hotel.UnitTests   # 124 tests, no Docker
docker compose up -d --wait db        # the integration tests use Postgres on 5476 (database hotel_test)
docker compose exec db createdb -U hotel hotel_test   # once
dotnet test tests/Hotel.IntegrationTests
docker compose up --build --wait      # db, kafka, rabbitmq, api, web
./scripts/smoke.sh                    # last line: hotel smoke passed
npx playwright install chromium       # once
npx playwright test                   # the browser specs against http://127.0.0.1:8096
docker compose down                   # keeps the Postgres volume; `down -v` drops it
```

| Service | Host port |
| --- | --- |
| Web (nginx, proxies `/api/`) | 8096 |
| API (`/scalar/v1`, `/api/health`) | 5216 |
| Postgres 17 | 5476 |
| Kafka 3.8.1 (KRaft, one broker) | 9126 |
| RabbitMQ 3.13 / management UI (`platform` / `platform`) | 5706 / 15706 |

Inside the Compose network the API and nginx listen on 8080 and Kafka's internal listener is 9092.

## Try it with curl

The web app is at <http://127.0.0.1:8096> (`admin@hotel.local` / `HotelAdmin2026` in the demo stack, or register a customer). From the shell:

```bash
API=http://127.0.0.1:5216
login() { curl -s -X POST $API/api/auth/login -H 'content-type: application/json' -d "{\"email\":\"$1\",\"password\":\"$2\"}" | python3 -c 'import sys,json; print(json.load(sys.stdin)["accessToken"])'; }
ADMIN="Authorization: Bearer $(login admin@hotel.local HotelAdmin2026)"

curl -s -X POST $API/api/auth/register -H 'content-type: application/json' \
  -d '{"email":"ana@example.com","password":"Passw0rd-Demo1"}'      # 201; its accessToken goes in $ANA below
ANA="Authorization: Bearer <accessToken>"

curl -s -H "$ADMIN" -X POST $API/api/room-types \
  -H 'content-type: application/json' \
  -d '{"code":"HARBOR-DLX-KING","name":"Harbor Deluxe King","city":"Lisbon","totalRooms":3,"rateCents":12000,"openNights":30}'

CHECKIN=$(python3 -c "import datetime; print(datetime.date.today() + datetime.timedelta(days=2))")
curl -s -H "$ANA" -X POST $API/api/bookings \
  -H 'content-type: application/json' \
  -d "{\"roomTypeCode\":\"HARBOR-DLX-KING\",\"reference\":\"GUEST-1001\",\"checkIn\":\"$CHECKIN\",\"nights\":3,\"rooms\":2}"
# The room type is open from today for 30 nights, so the check-in has to fall inside that window.
# 202, status Requested, totalCents 72000. Use the returned id:

curl -s -H "$ANA" $API/api/bookings/<id>                   # Confirmed after a few seconds, with a voucher number
curl -s -H "$ANA" $API/api/vouchers/<id>
curl -s -H "$ANA" "$API/api/room-types/HARBOR-DLX-KING/availability?from=$CHECKIN&days=7"   # rooms left per night
curl -s -H "$ADMIN" $API/api/bookings/<id>/events            # the Kafka log for this booking

curl -s -H "$ANA" -X POST $API/api/bookings/<id>/cancellation \
  -H 'content-type: application/json'                             # every night comes back; the voucher stays
```

A booking is validated at the door: guest reference required (at most 40 characters), a check-in that is not in the past, 1 to 14 nights, 1 to 3 rooms, and every night of the stay must be open for sale. The poison match is a case-sensitive substring: `GUEST-POISON-7` and any longer token containing `POISON` match.

## Develop with Nx

The repository is an Nx workspace (`nx.json`, plugin `@nx/dotnet`). `npx nx show projects` lists `api`, `web`, the four libraries and both test projects.

```bash
docker compose up -d --wait db kafka rabbitmq   # the brokers and Postgres, not the app
npx nx serve api                                # http://localhost:5216 (Development: seeded admin, public docs)
npx nx serve web                                # http://localhost:4290, proxies /api to the API
WEB_URL=http://localhost:4296 npx playwright test   # the same browser specs, against the dev servers
npx nx run-many -t typecheck lint -p web        # tsc --noEmit and ESLint (angular-eslint)
npx nx build web && npx nx build api            # what CI builds
```

## What is tested, and where it runs

- **Unit tests** (`tests/Hotel.UnitTests`, 124 tests, no Docker, about 3 seconds): the booking rules and fee, the poison rule including integer overflow, token creation and validation, password and lockout behaviour fails the auth service on an in-memory store, owner-or-staff access, the retry policy and topology, message serialization, the outbox `DbContext` conventions, and the HTTP middleware (problem details are `application/problem+json`, security headers, correlation ids).
- **Integration tests** (`tests/Hotel.IntegrationTests`, 68 tests, real Postgres): the rooms race (two tasks, two `DbContext`s, one unitthat are `Held`, one `Short`, a stay overlapping the loser) and the check constraint behind it; the saga handlers against the real schema (duplicate delivery claimed once, a release returns the unit once, the poison rejection committed before the dead-letter, cancellation then late final mark); and the **whole HTTP pipeline** through `WebApplicationFactory`: register, login, wrong password, lockout, expired and tampered tokens, refresh rotation, replay revoking the family, two concurrent refreshes (exactly one wins), logout, the same-city header, the auth rate limit, and 401/403/404 for every route and ownership case. Two mutations were run against a read-then-write version fails the race and overlap tests.
- **`scripts/smoke.sh`** drives the composed stack: authentication and ownership first (401, 403, 404), then validation, the happy path (fee, voucher, Kafka log), the last room with three concurrent bookings, the poison reference (rejected, `vouchers.work.dlq` gains one message, nights restored), and a cancellation that lands between the voucher and the final mark. To make that last window wide enough to hit on purpose, Compose sets `Bookings__FinalMarkDelayMs=1500`; it is a latency knob and defaults to 0.
- **Playwright** (`e2e/specs`, 14 tests, Chromium): `auth.spec.ts` (anonymous sees only the form, wrong password, password policy, a customer books and sees only their own bookings with no staff controls, sign-out, the session surviving a reload through the cookie while `document.cookie` stays empty); `book-space.spec.ts` (open each night, and the stays that draw them down, book, voucher, reload, cancel); `ui-flows.spec.ts` (refusal messages, the poison rejection, the loser of a race, the Kafka event log). They run against the composed web port and against the Nx dev servers.
- **CI** (`.github/workflows/ci.yml`) has two jobs. `verify`: `npm ci`, Prettier check, `dotnet format whitespace --verify-no-changes`, unit tests, integration tests (Postgres 17 service) with coverage uploaded, a NuGet vulnerability check, `npm audit`, `nx typecheck` and `nx lint` for web (zero warnings allowed) and `nx build`. `e2e`: builds the Compose stack, runs the smoke and every Playwright spec, and uploads traces and prints logs on failure. Dependabot watches NuGet, npm, Actions and Docker.

## Cloud mapping

This repository runs on Compose. Nothing in it calls a cloud account.

| Here | On AWS |
| --- | --- |
| Postgres 17 | RDS for PostgreSQL |
| Kafka 3.8 | MSK |
| RabbitMQ 3.13 | Amazon MQ, or SQS with a dead-letter queue |
| The API and the web app | ECS or App Service |

## API

JSON over HTTP. Everything except health and `/api/auth/register|login|refresh|logout` needs a bearer token. Interactive docs are at `http://127.0.0.1:5216/scalar/v1` (OpenAPI at `/openapi/v1.json`) when `Docs:Public` is true, which Compose and Development set; otherwise they need a staff token.

| Method and path | What it does | Responses |
| --- | --- | --- |
| `GET /api/health`, `GET /api/health/ready` | Liveness; readiness (every module schema reachable). | `200`, `503` |
| `POST /api/auth/register` | Creates a customer and mails a confirmation link. Signs them in unless confirmation is required. | `201` signed in, `202` check your inbox, `400` policy, `409` email taken, `429` |
| `POST /api/auth/login` | Signs in (with `code` when two-factor is on); sets the refresh cookie. | `200`, `401`, `403` unconfirmed, `429` locked or rate limited |
| `POST /api/auth/refresh`, `POST /api/auth/logout` | Rotate or revoke the refresh cookie. Need `X-Requested-With: hotel-web`. | `200`/`204`, `400` header missing, `401` |
| `POST /api/auth/confirm-email`, `resend-confirmation` | Confirm an address with the emailed token; ask for a new link (always `202`). | `204`/`202`, `400` |
| `POST /api/auth/forgot-password`, `reset-password` | Mail a reset link (always `202`); set a new password with it (ends every session). | `202`/`204`, `400` |
| `POST /api/auth/change-password`, `logout-everywhere` | Signed in: change the password (ends the other sessions and returns a fresh one); end every session on every device. | `200`/`204`, `400`, `401` |
| `GET /api/auth/mfa`, `POST /api/auth/mfa/setup`, `enable`, `disable` | Two-factor status and lifecycle (see above). | `200`/`204`, `400`, `409` |
| `GET /.well-known/jwks.json` | The public key set when RS256 is on. | `200`, `404` |
| `GET /api/auth/me` | The caller. | `200`, `401` |
| `POST /api/room-types` | Opens a room type for sale on a run of nights (default 30 from today). Staff. | `201`, `400` invalid, `403`, `409` code exists |
| `GET /api/room-types`, `GET /api/room-types/{code}` | RoomTypes with the first open night, 14 days, at most 60). | `200`, `404` |
| `POST /api/bookings` | Accepts a stay for the caller and starts the saga. | `202` Requested, `400` invalid, `404` unknown roomType, `409` duplicate reference |
| `GET /api/bookings?roomTypeCode=&page=&pageSize=` | The caller's bookings, newest first, a page at a time (`pageSize` at most 100, default 100). Staff: everyone's. The total is in `X-Total-Count`. | `200` |
| `GET /api/bookings/{id}` | One booking and its status. Owner or staff. | `200`, `404` |
| `POST /api/bookings/{id}/cancellation` | Moves a held or confirmed booking to `Cancelled` and gives the nights back. The voucher is not touched. Owner or staff. | `202`, `404`, `409` wrong status |
| `GET /api/bookings/{id}/events` | The Kafka event log for one booking. Staff. | `200`, `403` |
| `GET /api/admin/dead-letters`, `POST /api/admin/dead-letters/{queue}/replay` | What waits in each dead-letter queue, with the first messages; publish them again. Staff. A message that still fails for the same reason is dead-lettered again. | `200`, `403`, `503` brokers off |
| `GET /api/vouchers/{bookingId}` | The voucher row. Owner or staff. | `200`, `404` |

Errors come back as `application/problem+json` with a `title` and the `correlationId`.

## Configuration

Compose sets these on the API container. Defaults in `apps/api/appsettings.json` point at the published host ports, so `nx serve api` works against `docker compose up -d --wait db kafka rabbitmq`.

| Setting | Compose value | Meaning |
| --- | --- | --- |
| `ConnectionStrings__Hotel` | `Host=db;Port=5432;Database=hotel;Username=hotel;Password=hotel` | The one Postgres database. Migrations run on startup. |
| `RabbitMq__Host` / `__Port` / `__User` / `__Password` | `rabbitmq` / `5672` / `platform` / `platform` | Work queue. Virtual host `/`. |
| `Kafka__BootstrapServers` | `kafka:9092` | Event log. |
| `Jwt__Key` | `${JWT_KEY}` or a demo key | HS256 signing key, at least 32 bytes. Required; startup fails without it. |
| `Jwt__AccessMinutes` / `__RefreshDays` / `__SecureCookies` | `15` / `14` / `false` | Token lifetimes; set `SecureCookies` when TLS ends in front fails the API. |
| `Identity__SeedAdmin__Email` / `__Password` | `${ADMIN_EMAIL}` / `${ADMIN_PASSWORD}` | Administrator created on first start. |
| `Auth__RequireConfirmedEmail` / `Auth__PublicUrl` | `false` (demo) / `http://127.0.0.1:8096` | Sign-in waits for email confirmation (code default `true`); the address links in emails point to. |
| `Mail__Host` / `__Port` / `__User` / `__Password` / `__From` / `__StartTls` | unset | SMTP for confirmation and reset emails. Unset means the log only. |
| `Mail__DevMailbox` | `true` (demo) | Serve the last messages at `/api/dev/mailbox`. Never on in production. |
| `Jwt__RsaPrivateKeyPem` / `Jwt__KeyId` | unset | Sign with RS256 and publish the public key at `/.well-known/jwks.json`. |
| `Jwt__RevocationCacheSeconds` | `2` (code default `5`) | How long an instance trusts a cached session version before re-reading it. |
| `RateLimit__AuthPerMinute` | `120` (code default `20`) | Requests a minute per client to `/api/auth/*`. |
| `ForwardedHeaders__Enabled` | `true` | Trust `X-Forwarded-*` from the reverse proxy (nginx here). Leave off when the API is reached directly. |
| `Docs__Public` | `true` | Serve Scalar and OpenAPI without a token. |
| `Logging__Json` | `true` | Structured JSON console logs. |
| `Messaging__OutboxRetentionDays` / `Messaging__InboxRetentionDays` | `7` / `30` | Sent outbox rows and old inbox claims are deleted after this long. The inbox is kept longer because it must outlive any redelivery. |
| `Bookings__FinalMarkDelayMs` | `1500` | Milliseconds the final mark waits. A latency knob so the smoke can land a cancellation inside the window. `0` outside Compose. |
| `HOTEL_TEST_DB` (tests only) | | Postgres connection for `dotnet test`. Defaults to `localhost:5476`, database `hotel_test`. CI points it at its service container. |

## Look inside and fix things

```bash
docker compose ps                      # five services, all healthy
docker compose logs -f api             # relay, consumers, poison and retry lines
docker compose exec db psql -U hotel -d hotel -c 'select "Night","RoomsLeft" from "RoomNights" order by "Night" desc limit 10'
open http://127.0.0.1:15706        # RabbitMQ management, platform / platform
```

- **Dead-lettered messages** sit in `vouchers.work.dlq`. Nothing replays them; inspect them in the management UI. Retries wait in `<queue>.retry` for 2 seconds.
- **A port is already in use**: `lsof -nP -i :5216` shows who holds it. Compose publishes only on `127.0.0.1`.
- **`dotnet test` cannot connect**: start Postgres first with `docker compose up -d --wait db`.
- **The smoke cannot reach the API**: run `docker compose up --build --wait` first; override the target with `API=... RABBIT=... ./scripts/smoke.sh`.
- **Start clean**: `docker compose down -v` drops Postgres, Kafka and RabbitMQ data. Plain `down` keeps them, and the smoke is safe on kept data because every code carries a run id.
- **The Kafka log looks empty right after a booking**: the relay publishes within a second or so; reload the event log.

## Known limits

- Sign-in is email and password plus optional TOTP. There are no external identity providers (Google, Microsoft) and no WebAuthn passkeys.
- Revocation is eventual by design: an instance may honour a revoked access token for up to `Jwt:RevocationCacheSeconds` seconds. Set it to `0` to check the database on every request.
- Delivery is at-least-once, not exactly-once. The inbox makes repeats harmless for the saga; the Kafka log can hold a duplicate event after a relay crash.
- Compose runs one of each broker (one Kafka partition, replication factor 1) and one API instance. The code itself is built for several API instances: the relay claims outbox rows with `FOR UPDATE SKIP LOCKED`, and the rate limit and revocation live in Postgres. A production Kafka and RabbitMQ would be clustered; that is configuration, not code.
- `GET .../events` reads the Kafka topic for one key and gives up after ten seconds, so it scans the topic. Fine here, not something to expose at scale.
- Replaying a dead letter only helps when the cause was fixed: the poison reference fails again by design.
- The browser specs drive Chromium at a desktop size only, and the `e2e` CI job needs Docker and takes a few minutes.

## Layout

```
apps/api       thin host: composition root, middleware, endpoints
apps/web       Angular 21, styled with Tailwind 4 utilities only (sign-in, role-aware workspace)
libs/shared    messaging (outbox, inbox, relay, consumers), auth primitives, middleware
libs/identity  users, roles, JWT, refresh tokens          (schema identity)
libs/bookings  booking rules and the saga's first step    (schema bookings)
libs/rooms  the only writer fails the remaining count     (schema rooms)
libs/vouchers voucher and the nights drops                            (schema vouchers)
tests/         Hotel.UnitTests, Hotel.IntegrationTests
e2e/           Playwright
scripts/       smoke.sh
compose.yaml   Postgres, Kafka, RabbitMQ, API, web
```

Nx 23.2.1 orchestrates `nx build web` and `nx build api`; Each module's EF Core migrations are committed under its library and the API applies them on startup. Packages are pinned centrally in `Directory.Packages.props`, and warnings are errors (`Directory.Build.props`).

## Other systems in this .NET track

- [dotnet-freight](https://github.com/joshbarros/dotnet-freight): container space on ocean sailings.
- [dotnet-clinic](https://github.com/joshbarros/dotnet-clinic): appointment slots in a provider session.
- [dotnet-ledger](https://github.com/joshbarros/dotnet-ledger): outgoing payments from an account balance.
- [dotnet-warehouse](https://github.com/joshbarros/dotnet-warehouse): warehouse stock and outbound orders.
- [dotnet-academy](https://github.com/joshbarros/dotnet-academy): course cohort seats and enrollments.
- [dotnet-compute](https://github.com/joshbarros/dotnet-compute): GPU-hour reservations in a pool.
- [dotnet-tickets](https://github.com/joshbarros/dotnet-tickets): event ticket tiers and orders.
- [dotnet-carbon](https://github.com/joshbarros/dotnet-carbon): carbon credit batches and purchases.
- [dotnet-charging](https://github.com/joshbarros/dotnet-charging): EV charger slot reservations.
- [dotnet-promo](https://github.com/joshbarros/dotnet-promo): limited-use promo codes and redemptions.
