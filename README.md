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
| **Notifications** | Flat: `Models` / `Data`, and no `Endpoints` | The first consumer of a published event, and the only module with no HTTP surface at all — it is reached by Inventory's outbox dispatcher, not by a client. `DECISIONS.md` 055. |
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
│   ├── Encore.Modules.Notifications        flat, no routes — consumes SeatSoldV1
│   ├── Encore.Modules.Inventory.Domain     the hexagon's interior — no packages
│   ├── Encore.Modules.Inventory            ports, adapters, use cases, the outbox
│   └── Encore.Modules.Inventory.Contracts  its public face — zero packages, zero refs
└── tests/
    ├── Encore.Modules.Inventory.UnitTests         domain + handlers, in memory
    ├── Encore.Modules.Inventory.IntegrationTests  adapters + the outbox, via Testcontainers
    ├── Encore.Modules.Catalog.IntegrationTests    schema + pricing projection
    ├── Encore.Modules.Orders.UnitTests            the HTTP mapping, no database
    ├── Encore.Modules.Orders.IntegrationTests     checkout, against real Postgres
    ├── Encore.Modules.Payments.UnitTests          the state machine + the gateway
    ├── Encore.Modules.Payments.IntegrationTests   the one-live-attempt index
    ├── Encore.Modules.Notifications.IntegrationTests  the consumer, and its idempotency
    └── Encore.ArchitectureTests                   the boundaries, over metadata and csprojs
```

`Encore.Modules.Inventory.Domain` has no `PackageReference` items at all, and
neither do `Encore.Shared` or the three `.Contracts` assemblies. That is checked
rather than asserted: all five opt into three build rules in
`Directory.Build.targets` — `ENCORE001` for a direct package, `ENCORE002` for a
framework reference, `ENCORE003` for anything outside the BCL reaching the
resolved reference closure, which is the transitive case the first rule cannot
see. `tests/Encore.ArchitectureTests` asserts the same boundaries again over
compiled metadata and the declared project graph. EF Core, Redis and ASP.NET Core
exist only on the far side of the ports.

`Encore.Shared` holds exactly two things, and that is the shape of the rule: `IDomainEvent`,
and `IIntegrationEventHandler<T>` — how a module is told that something happened elsewhere.
Both are pure BCL, so the assembly the domain depends on stays as empty as it was.

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
- **Events leave through an outbox, in the seat's own transaction.** Every transition
  a seat makes is drained into `inventory.outbox_messages` by
  `InventoryDbContext.SaveChanges`, so the sale and the announcement of the sale
  cannot disagree — they commit together or not at all. A background dispatcher
  claims rows with `FOR UPDATE SKIP LOCKED`, delivers at-least-once, backs off on
  failure and dead-letters after five attempts. What crosses the wire is a published
  contract in `Inventory.Contracts`, never the domain record, so renaming a field in
  the aggregate cannot break a consumer or a row already written.
- **Delivery is not load-bearing for anything.** The concurrency tests never register
  a dispatcher and pass unchanged. That is the same rule the expiry sweep lives under:
  if a test cannot pass with it disabled, it has become load-bearing and the design is
  broken.

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

The run profiles set `Catalog__`, `Inventory__`, `Orders__` and `Payments__MigrateOnStartup`,
so a fresh `docker compose up` gets its schema from `dotnet run`. Nothing
deployed does that — applying migrations is a deliberate step
(`dotnet ef database update`), for the reasons in `DECISIONS.md` 013.

**Postgres is published on `55432`, not `5432`.** The container still listens on 5432
internally; only the host side moved. 5432 is the likeliest port on any developer machine
to be occupied already — by a native PostgreSQL service, most often — and when it is, the
symptom is a `28P01: password authentication failed` against a connection string that is
perfectly correct, because a different server answered. Docker will still report the
mapping while losing the port, so the check that settles it is which process owns the
listener. `DECISIONS.md` 050. Redis is unmoved on `6379`.

To run the host in a container instead — which is also what the load harness points at:

```bash
docker compose --profile load up -d --build api --wait
```

## API documentation

Interactive documentation is served by the host itself at **`/docs/`**, and both launch
profiles open it on start: F5 in Visual Studio (or `dotnet watch`) lands on
[http://localhost:5107/docs/](http://localhost:5107/docs/) rather than a bare 404 at the
root. Plain `dotnet run` ignores `launchBrowser`, so open it yourself there. The
containerised host serves the same page at
[http://localhost:8080/docs/](http://localhost:8080/docs/).
Swagger UI over [`src/Encore.Api/wwwroot/docs/openapi.json`](src/Encore.Api/wwwroot/docs/openapi.json),
with every route, every `reason` code and the `X-Client-Id` header wired into the
Authorize box so **Try it out** works — the page and the API share an origin, so there is
no CORS policy to configure and none exists.

The document is written by hand and verified against a running instance, not generated.
`Encore.Api` holds zero `PackageReference` items on purpose, which rules out both
Swashbuckle and `Microsoft.AspNetCore.OpenApi` (`DECISIONS.md` 014). The honest cost is
that nothing regenerates it and no test fails when a route changes and the document does
not — `DECISIONS.md` 049 records that, and the two ways to close it.

## Running the tests

```bash
docker compose run --rm tests
```

397 tests: 282 unit and architecture, 115 integration against real Postgres and
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

## Running the load test

```bash
docker compose run --rm --build load
```

That brings up Postgres, Redis and a freshly built API image, then drives k6 at it
over HTTP. `--build` is not optional: the API image is compiled from source, and a
run without it reports last time's binaries as though they were today's.

Two scenarios, one after the other rather than at once, because they answer
different questions:

| Scenario | Shape | What it shows |
|---|---|---|
| `contention` | 50 clients, 5 seats, hold then release immediately | The hot path at full pressure. The pool never drains, so every request meets a seat somebody else wants. |
| `flash_sale` | 100 clients, 500 seats, hold then purchase | Inventory draining the way it would on sale day, with the refusal mix shifting from `already_held` to `already_sold`. |

Two of its thresholds are assertions rather than reporting, and either one fails
the run: **no oversell** — successful purchases can never exceed the seats that
exist — and **no unexpected responses**, where a 409 is the system working and a
500 or a timeout is not. There is deliberately no latency threshold yet; an SLO
invented before the first measurement is a guess wearing a test's clothing
(`DECISIONS.md` 048).

### What the outbox cost

This is the measurement 048 built the harness to make possible, and it separates the
outbox's two halves. `OUTBOX_ENABLED=false` runs the drain without the dispatcher:

```bash
OUTBOX_ENABLED=false docker compose run --rm --build load
```

| | hold p99, contention | purchase p99 | iterations |
|---|---|---|---|
| Before the outbox (three runs) | 41.1 / 41.9 / 50.2 ms | 44.2 / 55.5 / 60.5 ms | 425,299 |
| Drain only | 48.4 ms | 57.4 ms | 398,048 |
| Drain and dispatcher | 78.2 ms | 141.4 ms | 304,071 |

**The half that cannot be turned off is nearly free; the half that can is the whole
cost.** Writing an outbox row inside every seat transaction lands inside the spread
three pre-outbox runs produced. Running the dispatcher alongside the API adds 62% to
hold p99 and 146% to purchase p99 — not because it writes to the hot path, but because
it puts a second workload on the database the hot path is contending on.

The prediction going in was the opposite, and the refusal counts say why: only about
15,000 of 304,000 iterations write anything at all, because a refused hold throws
before it ever reaches a save. The write path was never where the volume was.

**The more important result:** with the dispatcher off, 21,948 events piled up
undelivered and the run still sold 500 of 500 seats with no oversell. Delivery is late;
nothing is wrong. That is the same rule the expiry sweep lives under, demonstrated under
sustained load rather than in a unit test. `DECISIONS.md` 056 has the full numbers and
the caveats — one laptop, one run per configuration, and a baseline whose own p99 spread
was 22%.

The knobs are environment variables, because k6 ignores `--vus` when a script
defines scenarios:

```bash
docker compose run --rm --build -e CONTENTION_VUS=200 -e SALE_SEATS=2000 load
```

Each run writes a JSON summary to `load/results/`, which is gitignored — one
laptop's numbers on one day are worth comparing against the next run and worth
nothing to a reader of the repo.

## Status

**Load-In closed; Soundcheck started.** Inventory is complete and proven end to end —
aggregate, ports, adapters, four use cases, HTTP surface, migrations, a concurrency
test that passes, and now an outbox. It is the deep module and it is done.

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

**The outbox is built, and Notifications is the fifth module** (`DECISIONS.md` 051–055).
Every seat transition is drained into `inventory.outbox_messages` inside the seat's own
transaction, and a dispatcher delivers it at-least-once. Notifications consumes `SeatSoldV1`
and is the first module reached by an event rather than by a request — it has no routes, so
it has an `Add` seam and no `Map` one.

Two of those entries are worth reading before changing anything here. **052** records a
duplicate-write bug that the design avoids only because the drain clears a seat's events
after a successful save: the context is scoped, so a four-seat checkout would otherwise
publish the first seat's hold four times. **051** states the ordering guarantee precisely —
per transaction, not globally — because an outbox that looks like it orders everything is one
somebody will trust too far.

The load harness came before all of it on purpose. The outbox writes into the same
transaction as every seat write, so a baseline taken afterwards could never say what it cost
(`DECISIONS.md` 048); three pre-outbox runs establish the spread and **056** records the
delta.

The honest gap: a gateway call that times out is still recorded rather than resolved. The
attempt keeps its idempotency key so a retry asks the same question rather than a second one,
but nothing yet reconciles an authorisation that may or may not have landed. 031 parked that
behind the outbox, which now exists — so it is unblocked rather than deferred.

Deliberately absent, by roadmap phase rather than oversight: the Strangler Fig extraction of
Payments, the expired-hold sweep, MediatR, MassTransit, SignalR, observability and any
deployment story.

Nothing is open inside Load-In. The last gap — `ENCORE001` inspecting only direct
`PackageReference` items, so infrastructure arriving transitively through a
`ProjectReference` sailed past it — is closed, along with three others found while
closing it: framework references went unchecked, and `Encore.Shared` and the contracts
assemblies had no guard at all despite being where a back-door dependency would actually
arrive. Everything else on the list above belongs to a later phase.

| Phase | Weeks | Focus |
|---|---|---|
| Load-In | 1–3 | Modular monolith, DDD tactical patterns, TDD foundation |
| **Soundcheck** | 4–6 | Extract Payments and Notifications via Strangler Fig + Outbox |
| Showtime | 7–10 | Inventory concurrency, load testing, chaos experiments |
| On Tour | 11–12+ | Cloud deploy, observability, write-up |
