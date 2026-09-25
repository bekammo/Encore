# Encore

[![tests](https://github.com/bekammo/Encore/actions/workflows/tests.yml/badge.svg)](https://github.com/bekammo/Encore/actions/workflows/tests.yml)
[![licence: MIT](https://img.shields.io/badge/licence-MIT-blue.svg)](LICENSE)

**A flash-sale ticketing backend that sells every seat exactly once, even with Redis killed
mid-sale.**

- **What:** a .NET 10 modular monolith for the moment thousands of clients click the same seats.
  Payments also runs as its own service.
- **Stack:** C# · ASP.NET Core minimal APIs · EF Core 10 on PostgreSQL 16 · Redis 7 · xUnit
  with Testcontainers · k6 · OpenTelemetry and Grafana · Docker Compose · GitHub Actions.
- **Result:** 500 of 500 seats sold and none oversold across 410,000–450,000 hold attempts a
  run. Still no oversell with Redis stopped, and no order confirmed unpaid with the payment
  service stopped.
- **Run it:** `docker compose up -d && dotnet run --project src/Encore.Api`, then
  http://localhost:5107/docs/.

An event-ticketing backend in .NET 10, built around one hard problem: the flash-sale moment
when thousands of clients contend for the same seats. It is a modular monolith in which four
modules are deliberately plain and one is deliberately not, because the subject of the
project is **where architecture is worth paying for**.

- [WRITEUP.md](WRITEUP.md) tells the story in one read: what was built, what broke under
  load, and what that changed.
- [DECISIONS.md](DECISIONS.md) records 32 decisions, each with the alternative it beat and
  what it costs.
- [docs/CONFIGURATION.md](docs/CONFIGURATION.md) lists every setting, its default and where
  it is set.

## Results

Measured with k6 against the containerised system, with faults injected by
[`load/chaos.sh`](load/chaos.sh) and the aftermath read back from Postgres.

| Scenario | Outcome |
|---|---|
| Flash sale: 100 clients, 500 seats | 500 of 500 sold, **none oversold**, 410,000–450,000 hold attempts per run across two one-minute scenarios. Contended hold p99 34–44 ms. |
| Redis stopped mid-sale | **No oversell.** Seats kept selling on Postgres alone; a hold costs 6% more at the median. |
| Payments service stopped | **No order confirmed without payment**, and every seat still held maps to an open order. |
| Event dispatcher stalled for 20 s | Request path unaffected (hold p99 23.6 ms). Events arrived late; none were lost. |
| Two payment reconcilers on one table | **No payment settled twice.** |
| 1,943 orders with confirm and cancel racing | **None partly sold, sold unpaid, or paid unsold.** |

These are single-machine numbers, useful for comparing one run with the next rather than as
a capacity claim. The same order invariants are asserted on every CI run by
`CheckoutCompositionTests`, and the chaos script exits non-zero if any of them breaks.

## Architecture

```mermaid
flowchart TB
    client(["HTTP clients · k6"])

    subgraph monolith["Encore.Api"]
        orders["Orders"]
        catalog["Catalog"]
        inventory["Inventory (hexagonal)"]
        payments["Payments"]
        notifications["Notifications"]
    end

    papi["Encore.Payments.Api"]
    redis[("Redis: lock only")]
    pg[("PostgreSQL: one schema per module")]

    client --> orders
    client --> inventory
    orders -->|price| catalog
    orders -->|hold, sell, release| inventory
    orders -->|authorise, capture, void| payments
    orders -.->|or over HTTP| papi
    inventory -->|outbox| notifications
    inventory --> redis
    inventory --> pg
    papi --> pg
```

| Module | Shape | Why |
|---|---|---|
| **Inventory** | Hexagonal: `Domain` / `Ports` / `Adapters` / `Application` | Seat contention under load, the one genuinely hard problem. |
| **Orders** | Flat: `Endpoints` / `Data` / `Models` | Orchestrates checkout across the other modules through their contracts. |
| **Payments** | Flat, with a guarded `Payment` state machine | A simulated gateway that declines, hangs and loses requests on demand. |
| **Catalog** | Flat | Read-mostly CRUD: venues, events, prices. |
| **Notifications** | Flat, no HTTP surface | Consumes `SeatSold` from Inventory's outbox. |

The asymmetry is the argument. Architecture is a cost paid for optionality, and it is paid
here only where the optionality gets spent. Inventory has ports because its storage,
locking and messaging each have a real alternative that was measured. The other four have
no contention and no invariants that span rows, so layering them would buy nothing.

**Boundaries are enforced, not asserted.**
- Inventory's domain lives in its own assembly with **zero package references**. Three
  MSBuild rules (`ENCORE001`–`003`) fail the build if a package, a framework reference or a
  transitive dependency reaches it. The same applies to the shared kernel and the three
  `.Contracts` assemblies.
- Modules reach each other only through `.Contracts` assemblies.
- A host composes each module through exactly one seam: `Add{Module}Module` /
  `Map{Module}Module`.
- `Encore.ArchitectureTests` checks all of this again over project files, source and
  compiled metadata.

**Payments runs in process or as its own service.** `Encore.Payments.Api` composes the
same module behind three internal routes. Orders switches between the in-process and HTTP
adapters with one configuration key, and a composition test runs Orders' HTTP client
against the real routes (Strangler Fig, `DECISIONS.md` 018).

<details>
<summary>Solution layout</summary>

```
src/
  Encore.Api                          monolith host
  Encore.Payments.Api                 Payments as its own service
  Encore.Shared                       two BCL-only interfaces every module may see
  Encore.Telemetry                    the hosts' OpenTelemetry and health-check wiring
  Encore.Modules.Shared.Persistence   migrator and schema wiring; may not name a module
  Encore.Modules.Shared.Http          endpoint filters and per-IP rate limiting; may not name a module
  Encore.Modules.Inventory.Domain     Seat aggregate, events, exceptions; no packages
  Encore.Modules.Inventory            ports, adapters, use cases, outbox
  Encore.Modules.{Catalog,Orders,Payments,Notifications}
  Encore.Modules.{Inventory,Catalog,Payments}.Contracts   public faces; no packages
tests/
  Encore.ArchitectureTests            boundaries over csprojs, source and metadata
  *.UnitTests                         domain, handlers, HTTP mapping, state machines
  *.IntegrationTests                  real Postgres and Redis via Testcontainers
load/
  flash-sale.js, chaos.sh             the k6 harness and the fault injector
```

</details>

## How a seat is sold

**`Seat` is the aggregate and the only consistency boundary.** A hold is not an entity; it
is the `HeldByClientId` / `HoldExpiresAt` pair on the seat row, so taking, losing or
converting a hold is always a change to one row.

- **Postgres settles every race.** `RowVersion` maps to Postgres's `xmin`, so optimistic
  concurrency needs no code to maintain it. A test has fifty clients contend for one seat
  with no lock anywhere, and exactly one wins.
- **Redis is a lock, never state.** It serialises only the per-client hold cap, for one
  write attempt. With Redis gone the system stays correct, and the cap degrades to
  best-effort by design: a cap breach is a refund, while an oversell is a customer outside a
  sold-out venue.
- **Expiry is lazy.** A lapsed hold is available on every read and write path, whatever the
  row says. The background sweep only tidies rows. With the sweep disabled every test still
  passes, and `ExpiryWithoutTheSweepTests` exists to prove it.
- **Hold rules live in the aggregate.** Holds last five minutes. Callers pass the current
  time and never an expiry, so no caller can reach a state the rules did not approve.
- **Events leave through a transactional outbox.** Each transition is written to
  `inventory.outbox_messages` in the seat's own transaction, so a sale and its announcement
  commit together or not at all. A dispatcher claims rows with `FOR UPDATE SKIP LOCKED`,
  delivers at least once, backs off, and dead-letters after five attempts. No seat invariant
  depends on it running.

**Checkout** in Orders authorises the payment, sells every seat in one transaction, then
captures:
- Any path that does not end with every seat sold voids the authorisation.
- A cancel releases the seats before the money, so it can never refund a seat it could not
  release.
- Once money has moved, no step is abandoned halfway, even if the client disconnects.
- Two background jobs resolve what a crash leaves behind: a reconciler settles payments the
  gateway never answered, and a capture sweep finishes orders that sold but were never
  captured. Both are cleanup, and the system stays correct with either switched off.
- A third ends what a customer abandons. An expiry sweep takes `Pending` orders whose holds
  lapsed, gives their seats back, then voids their money, asking Inventory about each seat
  first (031).

## Running it

Requires Docker and the .NET 10 SDK.

```bash
docker compose up -d                          # Postgres 16 and Redis 7
dotnet run --project src/Encore.Api           # API at http://localhost:5107, docs at /docs/
```

Or run the whole stack in containers:

```bash
docker compose --profile load up -d --build api --wait    # http://localhost:8080/docs/
```

To try a checkout from `/docs/`, enter two headers under **Authorize**:
- `X-Operator-Key: local-operator-key` creates a venue, an event and its seats. The
  development launch profile sets this key, and the containers default to it
  (`OPERATOR_API_KEY` overrides it).
- `X-Client-Id` can be any non-empty GUID. Use it to open an order, then confirm it.

Postgres is published on host port **55432** rather than 5432, which is often taken by a
local installation.

| Task | Command |
|---|---|
| All tests | `docker compose run --rm --build tests` |
| Filtered tests | `docker compose run --rm --build tests --filter "FullyQualifiedName~SeatTests"` |
| Load test | `docker compose run --rm --build load` |
| Chaos runs | `bash load/chaos.sh` (or `bash load/chaos.sh orders`) |
| Telemetry | `docker compose --profile telemetry up -d lgtm`, then Grafana on `:3000` |

## API

Interactive documentation is served at `/docs/`, from a hand-written OpenAPI document. A
test fails if the document and the mapped routes disagree in either direction, and it pins
every documented status enum to the C# enum behind it.

| Route | Purpose |
|---|---|
| `GET /health` · `GET /health/ready` | Liveness; readiness, voted by each module's health check. |
| `POST /catalog/venues` · `POST /catalog/events` | Create a venue, or a priced event at one. Needs `X-Operator-Key`. |
| `GET /catalog/venues[/{id}]` · `GET /catalog/events[/{id}]` | Browse the catalogue. |
| `POST /events/{eventId}/seats` | Create an event's seats. Needs `X-Operator-Key`. |
| `POST /events/{eventId}/seats/{seatId}/hold` · `release` · `purchase` | Seat actions, each idempotent. Off unless `Inventory:ExposeSeatRoutes` is set, since they sell without an order. |
| `POST /orders` | Open a checkout: price the event and hold every seat. Rate-limited per IP. |
| `GET /orders/{orderId}` | Read one of the caller's orders. |
| `POST /orders/{orderId}/confirm` · `cancel` | Pay for the order, or end it. |
| `GET /payments/{paymentId}` · `GET /payments?orderId=` | Read what happened to a payment. |

Refusals are `409` with a machine-readable `reason` and a `retriable` flag, so clients
branch on `reason` rather than on status codes:

```json
{ "title": "Seat unavailable", "status": 409, "reason": "already_held", "retriable": true }
```

## Testing

Over 700 tests: unit tests for the domain and handlers, integration tests against real
Postgres and Redis through Testcontainers, and architecture tests. The suite runs in a
container, the same way locally and in CI. CI does not rely on the exit code of
`docker compose run`, which is 0 even when nothing ran. Instead it counts one summary line
per test project, so a project that never executed fails the build.

Beyond the usual layers:
- **Concurrency tests** race real clients against one seat, and against one order's confirm
  and cancel.
- **Falsification tests** switch the background jobs off and check that nothing correct
  depended on them.
- **Composition tests** run the real modules together across each seam, because testing
  each side of a seam separately did not prove the seam (`DECISIONS.md` 018).

## Observability

OpenTelemetry traces, metrics and logs go to a provisioned Grafana dashboard. Telemetry is
off unless `OTEL_EXPORTER_OTLP_ENDPOINT` is set, so load runs stay comparable. A confirm is
one trace across both processes. Only Inventory defines its own instruments, at its adapter
edges:

| Metric | What it answers |
|---|---|
| `encore.inventory.seat.outcomes` | What each seat answered: held, lost the race, already sold… |
| `encore.inventory.lock.attempts` | What Redis answered, and when the client stopped asking it |
| `encore.inventory.outbox.deliveries` | Delivered, failed, dead-lettered, by event type |
| `encore.inventory.outbox.delivery.lag` | How late delivery runs, from seat change to handler |

## Trade-offs and limitations

Scope was chosen to keep the depth in one place. These are deliberate, and each is reasoned
in `DECISIONS.md`:

- **No customer authentication.** `X-Client-Id` is a claimed identity, which is enough to
  exercise contention and per-client rules. What that leaves open is closed another way (030):
  - The direct seat routes the load harness drives are off by default.
  - Operator writes need a shared key.
  - Checkout and holds are rate-limited per IP address. That is a floor against one machine,
    not a defence against a botnet; identity and a waiting room would be.
- **Redis outages still cost something.** Losing Redis costs a purchase about 1.45× at the
  median. A Postgres advisory lock removes that cost and keeps the cap enforced without
  Redis, but it costs about 16% throughput while Redis is healthy. It is one configuration
  key away, and Redis stays the default (005).
- **The payments data boundary did not move.** Payments runs as its own process, but its
  schema still lives in the shared database (018).
- **No message bus.** Events are delivered in process from the outbox. Cross-process
  delivery, and with it `Payment` domain events, waits for a consumer that needs it (013).
- **The payment gateway is simulated.** It can decline, time out and lose requests, but it
  never refuses a capture, so that failure is untested end to end (014).

## How this was built

I built Encore between 18 and 25 September 2026 with an AI coding assistant, Claude Code.
I chose the problem and the architecture:
- the flash sale as the one hard problem;
- the invariants the system must never break;
- the thesis that only Inventory earns ports and adapters.

I reviewed the assistant's changes as they came, accepting, rejecting and redirecting them.
Each change landed as a pull request that ran the full suite in CI before it merged.
[DECISIONS.md](DECISIONS.md) records the reasoning behind those calls, including the wrong
turns, and [WRITEUP.md](WRITEUP.md) tells the story in one read.
