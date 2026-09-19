# Encore

An event-ticketing platform, built as a modular monolith. A portfolio project
whose real subject is *where* architecture is worth paying for.

## The shape of it

Four modules behind one ASP.NET Core host. Three of them are deliberately
plain, and one is deliberately not:

| Module | Shape | Why |
|---|---|---|
| **Catalog** | Flat: `Endpoints` / `Data` / `Models` | Read-mostly CRUD. No contention, no invariants. |
| **Orders** | Flat: `Endpoints` / `Data` / `Models` | A record of what was bought. The hard parts live elsewhere. |
| **Payments** | Flat, plus `Simulation/` | A fake gateway that declines and hangs on demand, so the rest of the system has to cope with an unreliable dependency. `Payment` owns a guarded state machine — a factory and no public setters — without any of the layering: `DECISIONS.md` 029. |
| **Inventory** | Hexagonal: `Domain` / `Ports` / `Adapters` / `Application` | Seat contention under flash-sale load — the one genuinely hard problem. |

That asymmetry is the argument, not an accident. Architecture is a cost you pay
for optionality, and it is only worth paying where the optionality will actually
be spent. The reasoning behind every choice here, including the ones I would
expect to be challenged, is in [DECISIONS.md](DECISIONS.md).

## Layout

```
Encore.sln
├── src/
│   ├── Encore.Api                          ASP.NET Core minimal API host
│   ├── Encore.Shared                       cross-cutting contracts, zero packages
│   ├── Encore.Modules.Catalog              flat CRUD
│   ├── Encore.Modules.Catalog.Contracts    its public face — zero packages, zero refs
│   ├── Encore.Modules.Orders               flat CRUD + the checkout
│   ├── Encore.Modules.Payments             flat, + a state machine and a fake gateway
│   ├── Encore.Modules.Payments.Contracts   its public face — zero packages, zero refs
│   ├── Encore.Modules.Inventory.Domain     the hexagon's interior — no packages
│   ├── Encore.Modules.Inventory            ports, adapters, use cases
│   └── Encore.Modules.Inventory.Contracts  its public face — zero packages, zero refs
└── tests/
    ├── Encore.Modules.Inventory.UnitTests         domain + handlers, in memory
    ├── Encore.Modules.Inventory.IntegrationTests  adapters, via Testcontainers
    ├── Encore.Modules.Catalog.IntegrationTests    schema + pricing projection
    ├── Encore.Modules.Orders.UnitTests            the HTTP mapping, no database
    ├── Encore.Modules.Orders.IntegrationTests     checkout, against real Postgres
    ├── Encore.Modules.Payments.UnitTests          the state machine + the gateway
    └── Encore.Modules.Payments.IntegrationTests   the one-live-attempt index
```

`Encore.Modules.Inventory.Domain` has no `PackageReference` items at all, and
its csproj fails the build (`ENCORE001`) if one is added. EF Core, Redis and
ASP.NET Core exist only on the far side of the ports.

## What Inventory actually does

The seat is the whole problem. `Seat` is the aggregate root and the sole
consistency boundary, and a hold is not a separate entity — it is the
`HeldByClientId` / `HoldExpiresAt` pair on the seat row, so acquiring, losing or
converting a hold is always a single-row write.

- **Postgres is the source of truth.** Atomicity comes from optimistic
  concurrency: `RowVersion` maps to the Postgres `xmin` system column, so the
  database maintains the token and no code has to remember to.
- **Redis is only a lock**, held for the duration of one write attempt. It never
  stores hold state and is never a source of truth. Correctness survives Redis
  being gone entirely — the lock reduces contention, it does not create
  correctness.
- **Expiry is lazy first.** A row reading `Held` whose `HoldExpiresAt` has passed
  is logically available on every read and write path, whatever the column says.
  A background sweep is cleanup only, and its timing is never load-bearing.
- **Hold duration is five minutes, owned by the aggregate.** Callers pass
  `utcNow`, never an absolute expiry — a caller-supplied expiry would let anyone
  reach a state the rules never approved.
- **A client may hold four seats per event.** That rule spans four rows, so the
  aggregate cannot enforce it; it is a policy in the Application layer, enforced
  best-effort-plus. If Redis is down a client could exceed it. That asymmetry is
  deliberate: a cap breach is a refund email, an oversell is a customer standing
  outside a sold-out venue.

The claim that matters is tested rather than asserted: fifty clients contend for
one seat, with no Redis lock anywhere in the test, and exactly one wins.

## HTTP surface

Actions, not resources — a hold has no identity of its own, so it gets no URI.
All three seat actions are idempotent, which is what makes retrying a POST safe.

| Route | Purpose |
|---|---|
| `GET /health` | Liveness. |
| `POST /catalog/venues` | Creates a venue. Returns `201` and the new venue. |
| `GET /catalog/venues` · `GET /catalog/venues/{venueId}` | Browses venues. |
| `POST /catalog/events` | Creates an event at an existing venue, with its price. Returns `201`. |
| `GET /catalog/events` · `GET /catalog/events/{eventId}` | Browses events, soonest first. |
| `POST /events/{eventId}/seats` | Creates an event's seats. Body `{ "count": 100 }`, returns `201` with the new seat ids. |
| `POST /events/{eventId}/seats/{seatId}/hold` | Holds the seat for five minutes. Returns `holdExpiresAt`. |
| `POST /events/{eventId}/seats/{seatId}/release` | Gives a held seat back. |
| `POST /events/{eventId}/seats/{seatId}/purchase` | Converts this client's live hold into a sale. |
| `POST /orders` | Opens a checkout: prices the event, holds every seat, returns `201` and the order. |
| `GET /orders/{orderId}` | Reads one of the calling client's orders. |
| `POST /orders/{orderId}/confirm` | Authorises the total, converts the order's holds into sales, then captures. |
| `POST /orders/{orderId}/cancel` | Ends the order because the customer said so, releasing the seats and the money. |
| `GET /payments/{paymentId}` · `GET /payments?orderId=` | Reads back what happened to a payment. Read-only on purpose: Orders drives payment, because Orders is the thing that knows what is owed (`DECISIONS.md` 033). |

The three seat actions and every `/orders` route require an `X-Client-Id` header
carrying a GUID. **It is a claimed identity, not authentication** — anyone can
change it and reset their own cap. It is a deliberate stand-in for the Identity
module that does not exist yet, so the flow can be exercised end to end without
first inventing an auth story. That is also why no route returns `401` or `403`.

Refusals are `409` with a machine-readable `reason` and a `retriable` flag; only
an addressed thing that is not there — a seat, an order — is `404`. A request
that is wrong on its own terms, such as more seats than the published cap, is
`400`, and carries a `reason` too. Clients branch on `reason`, not on status
code:

```json
{
  "title": "Seat unavailable",
  "status": 409,
  "reason": "already_held",
  "retriable": true
}
```

## Running it

```bash
docker compose up -d
dotnet build
dotnet run --project src/Encore.Api
```

The run profiles set `Catalog__`, `Inventory__` and `Orders__MigrateOnStartup`,
so a fresh `docker compose up` gets its schema from `dotnet run`. Nothing
deployed does that — applying migrations is a deliberate step
(`dotnet ef database update`), for the reasons in `DECISIONS.md` 013.

## Running the tests

```bash
docker compose run --rm tests
```

305 tests: 212 unit, 93 integration against real Postgres and Redis via
Testcontainers.

The suite runs in a container rather than on the host, and that is a host problem
rather than a design one. Windows Smart App Control is a Code Integrity policy in
enforcement mode: it admits code that is either signed by a publisher it trusts
or vouched for by a cloud reputation service, and a freshly compiled assembly is
neither, so `dotnet test` fails to load its own dependencies with a
`FileLoadException` that looks like a broken build and is not one. Rebuilding
cannot help — reputation is keyed on file hash — and the policy has no exclusion
mechanism. A Linux container is not subject to it. The side benefit is the part
worth keeping: the suite now runs identically on any machine with Docker.
`DECISIONS.md` 015 has the full diagnosis.

Arguments append, so the usual filters work:

```bash
docker compose run --rm tests --filter "FullyQualifiedName~SeatTests"
```

## Status

**Mid Load-In.** Inventory is complete and proven end to end — aggregate, ports,
adapters, four use cases, HTTP surface, migrations, and a concurrency test that
passes. It is the deep module and it is done.

Catalog is implemented and flat: entities, schema, migration and CRUD routes, with
no layering ceremony anywhere in it. Orders is implemented and flat too, and it is
the module that proves the point of the three `.Contracts` assemblies — it prices
through Catalog, holds through Inventory and charges through Payments without
referencing any of them.

Payments is real. It became real during Load-In rather than at Soundcheck, because
Strangler Fig needs something to strangle and a module with no behaviour cannot be
extracted (`DECISIONS.md` 004). A confirm now authorises the order total, sells the
seats and then captures — in that order, because a sold seat is terminal and money
is the one of the two that can be given back. A sale that does not complete releases
the authorisation, so a customer is never charged for an order they did not get.
Notifications and Identity do not exist.

The honest gap: a gateway call that times out is recorded, not resolved. The attempt
keeps its idempotency key so a retry asks the same question rather than a second one,
but nothing yet reconciles an authorisation that may or may not have landed. That
needs the outbox, which is the next phase (`DECISIONS.md` 031).

Deliberately absent, by roadmap phase rather than oversight: the outbox and the
expired-hold sweep, MediatR, MassTransit, SignalR, observability and any
deployment story.

| Phase | Weeks | Focus |
|---|---|---|
| **Load-In** | 1–3 | Modular monolith, DDD tactical patterns, TDD foundation |
| Soundcheck | 4–6 | Extract Payments and Notifications via Strangler Fig + Outbox |
| Showtime | 7–10 | Inventory concurrency, load testing, chaos experiments |
| On Tour | 11–12+ | Cloud deploy, observability, write-up |
