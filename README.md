# Encore

[![tests](https://github.com/bekammo/Encore/actions/workflows/tests.yml/badge.svg)](https://github.com/bekammo/Encore/actions/workflows/tests.yml)

An event-ticketing platform, built as a modular monolith. A portfolio project
whose real subject is *where* architecture is worth paying for.

## The shape of it

Five modules, four of them deliberately plain and one deliberately not — and,
since the Payments extraction, two ASP.NET Core hosts rather than one:

| Module | Shape | Why |
|---|---|---|
| **Catalog** | Flat: `Endpoints` / `Data` / `Models` | Read-mostly CRUD. No contention, no invariants. |
| **Orders** | Flat: `Endpoints` / `Data` / `Models` | A record of what was bought. The hard parts live elsewhere. |
| **Payments** | Flat, plus `Simulation/` | A fake gateway that declines and hangs on demand, so the rest of the system has to cope with an unreliable dependency. `Payment` owns a guarded state machine — a factory and no public setters — without any of the layering: `DECISIONS.md` 013. |
| **Notifications** | Flat: `Models` / `Data`, and no `Endpoints` | The first consumer of a published event, and the only module with no HTTP surface at all — it is reached by Inventory's outbox dispatcher, not by a client. `DECISIONS.md` 016. |
| **Inventory** | Hexagonal: `Domain` / `Ports` / `Adapters` / `Application` | Seat contention under flash-sale load — the one genuinely hard problem. |

That asymmetry is the argument, not an accident. Architecture is a cost you pay
for optionality, and it is only worth paying where the optionality will actually
be spent. The reasoning behind every choice here, including the ones I would
expect to be challenged, is in [DECISIONS.md](DECISIONS.md).

The second host is `Encore.Payments.Api`: the same Payments module, composed
through the same seam, plus the three `/internal/payments/*` routes the monolith
does not map. Orders reaches it over HTTP when `Orders:Payments:BaseAddress` is
set and in-process when it is not — one configuration key, and the reason the
extraction was cheap. It was also inert for four days before a chaos run noticed
(`DECISIONS.md` 018).

## Layout

```
Encore.sln
├── src/
│   ├── Encore.Api                          ASP.NET Core minimal API host
│   ├── Encore.Payments.Api                 the Payments service — one module, its own process
│   ├── Encore.Shared                       cross-cutting contracts, zero packages
│   ├── Encore.Modules.Shared.Persistence   startup migrator + schema wiring, names no module
│   ├── Encore.Telemetry                    the hosts' OpenTelemetry wiring, names no module
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
    ├── Encore.Modules.Orders.UnitTests            the HTTP mapping + the strangler switch, no database
    ├── Encore.Modules.Orders.IntegrationTests     checkout, against real Postgres
    ├── Encore.Modules.Payments.UnitTests          the state machine + the gateway
    ├── Encore.Modules.Payments.IntegrationTests   the one-live-attempt index, the ledger, reconciliation
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

`Encore.Shared` holds exactly three things, and that is the shape of the rule: `IDomainEvent`;
`IIntegrationEventHandler<T>`, which is how a module is told that something happened
elsewhere; and `IReadinessCheck`, which is how a module contributes to `/health/ready`
without the host learning that it has a database (`DECISIONS.md` 016). All three are pure
BCL, so the assembly the domain depends on stays as empty as it was.

`Encore.Modules.Shared.Persistence` is the one place the five modules share code, and the
rules on it are the interesting part. It holds `ModuleMigrator<TContext>` and the
Npgsql/history-table wiring — what every module used to carry a copy of — and the type
parameter is the whole of what used to differ. It is forbidden to name a module or a
contracts assembly, it declares no `ProjectReference` at all, and nothing zero-dependency
may reference it: it carries EF Core and Npgsql on purpose, and `Encore.Shared` reaching it
would put EF Core on the domain's compile surface. Each module still registers its own
migrator behind its own flag, so extraction still takes one line. See `DECISIONS.md` 017.

## What Inventory actually does

The seat is the whole problem. `Seat` is the aggregate root and the sole
consistency boundary, and a hold is not a separate entity — it is the
`HeldByClientId` / `HoldExpiresAt` pair on the seat row, so acquiring, losing or
converting a hold is always a change to one row. An order's seats are written in one
transaction (all sold or none sold), but each seat still decides its own transition and
carries its own concurrency token. The transaction makes the writes atomic and enforces no
rule of its own (`DECISIONS.md` 011).

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
  `ExpiredHoldSweeper` tidies the rows and `ExpiryWithoutTheSweepTests` is the
  falsification — no sweeper, no Redis, and a lapsed hold is still reclaimed, still
  unsellable by its lapsed holder, and still freeing the client's hold cap. The
  sweep goes through the aggregate rather than issuing a bulk UPDATE, so a hold
  nobody ever came back for still ends with a `SeatReleased(Expired)` in the log
  (`DECISIONS.md` 006).
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
| `GET /payments/{paymentId}` · `GET /payments?orderId=` | Reads back what happened to a payment. Read-only on purpose: Orders drives payment, because Orders is the thing that knows what is owed (`DECISIONS.md` 008). |

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
(`dotnet ef database update`), for the reasons in `DECISIONS.md` 017.

**Postgres is published on `55432`, not `5432`.** The container still listens on 5432
internally; only the host side moved. 5432 is the likeliest port on any developer machine
to be occupied already — by a native PostgreSQL service, most often — and when it is, the
symptom is a `28P01: password authentication failed` against a connection string that is
perfectly correct, because a different server answered. Docker will still report the
mapping while losing the port, so the check that settles it is which process owns the
listener. `DECISIONS.md` 020. Redis is unmoved on `6379`.

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
Swashbuckle and `Microsoft.AspNetCore.OpenApi` (`DECISIONS.md` 008). Nothing regenerates
it. Instead, a test fails when the document and the mapped routes disagree in either
direction, and it also checks which host serves each route (`DECISIONS.md` 008).

## Running the tests

```bash
docker compose run --rm --build tests
```

608 tests: 384 unit and architecture, 224 integration against real Postgres and
Testcontainers. `--build` is not optional: the image compiles the source into itself
with no bind mount, so a run without it reports on the last build's binaries as though
they were today's.

The suite runs in a container rather than on the host, and that is a host problem
rather than a design one. Windows Smart App Control is a Code Integrity policy in
enforcement mode: it admits code that is either signed by a publisher it trusts
or vouched for by a cloud reputation service, and a freshly compiled assembly is
neither, so `dotnet test` fails to load its own dependencies with a
`FileLoadException` that looks like a broken build and is not one. Rebuilding
cannot help — reputation is keyed on file hash — and the policy has no exclusion
mechanism. A Linux container is not subject to it. The side benefit is the part
worth keeping: the suite now runs identically on any machine with Docker.
`DECISIONS.md` 020 has the full diagnosis.

Arguments append, so the usual filters work:

```bash
docker compose run --rm --build tests --filter "FullyQualifiedName~SeatTests"
```

## Continuous integration

[`.github/workflows/tests.yml`](.github/workflows/tests.yml) runs that same command on
every push. Not `dotnet test` on the runner, which would be faster and would be a
different thing being tested: `tests/Dockerfile` copies each project file in by name, so
a new test project nobody added to it fails `dotnet restore` at solution level — a
failure that looks nothing like its cause, and one only this path can catch.

**The verdict comes from the summary lines, not the exit code**, because
`docker compose run` exits 0 when it cannot reach the daemon at all: nothing runs, and
the command still succeeds. So the workflow counts `Passed!` lines against the number of
`*.csproj` files under `tests/`, which fails the run in three different ways — an
assembly that failed, nothing having executed, and a test project that exists in the tree
but never reached the runner. `DECISIONS.md` 020.

What it deliberately does not do is re-assert the purity rules. `ENCORE001`–`003` already
fail the build if a package, a framework reference or a transitive arrival reaches a
zero-dependency project, and `Encore.ArchitectureTests` asserts the same boundaries again
over compiled metadata — so a `dotnet list package` step in CI would be a third check of
something two mechanisms already refuse to let through.

Load and chaos runs stay off CI. They are one-laptop measurements whose numbers mean
something relative to the run before them and nothing on a shared runner of unknown
neighbours.

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
(`DECISIONS.md` 019).

### What the outbox cost

This is the measurement the harness was built early to make possible, and it separates the
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
sustained load rather than in a unit test. `DECISIONS.md` 019 has the full numbers and
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

## Breaking it on purpose

```bash
bash load/chaos.sh
```

Four faults, one run each, against the extracted configuration, plus an orders run described
below. k6 drives the traffic
and asserts the invariants; it has no access to the Docker daemon and never breaks
anything. `load/chaos.sh` owns the timeline, stops the containers, takes the locks, and
reads the aftermath out of Postgres into one report. Faults are injected in the *gaps*
between scenario windows, and each fault that asks a question about a number runs a
control window of identical shape beside the broken one.

What the four runs showed (`DECISIONS.md` 019 has the tables and the caveats):

- **Payments stopped.** Every invariant held: no order confirmed, every confirm read
  `payment_timed_out`, no 5xx, and the 133 orders left `pending` map one-to-one onto the
  133 seats still held. The cost is that a stopped container swallows connections rather
  than refusing them, so every confirm pays the full ten-second client timeout.
- **Two reconcilers over one table.** No attempt was settled twice, 82 sweeps lost the
  race on `xmin` and wrote nothing, and no order ever had more than one live attempt. But
  the process that had never authorised anything settled 379 attempts as `abandoned`,
  because the simulated gateway kept its answered keys in memory per process. A lease was
  not the first thing missing — **the gateway's memory is a table now**
  (`DECISIONS.md` 014), because an ordinary restart was enough to produce the same
  failure with one reconciler.
- **Redis stopped.** No oversell either side, and holds kept being won with no lock in
  sight — the claim that correctness comes from `xmin` alone, demonstrated under 250 VUs.
  A hold cost about 85 times more without it, because discovering the lock is
  unavailable waited out a five-second client timeout twice.
- **The dispatcher stalled.** A twenty-second consumer outage produced a backlog of 2,200
  messages, a delivery-latency tail of 20,631 ms, a request path that did not notice
  (hold p99 23.6 ms) and no faults at all. Late is not wrong, measured.

**Run again after fixing what it found**, the session exited 0 on every run for the first
time:
- **Control window:** clean. It had zero unexpected responses, where the first session had 114.
- **Two reconcilers:** checked row by row against the gateway's ledger, no attempt was
  settled `Abandoned` while the gateway held funds.
- **Redis stopped:** a hold cost 22× rather than 85×. The remaining second per lock
  attempt turned out not to be `ConnectTimeout`: tripling that setting moved nothing.
  `BacklogPolicy.FailFast` removed it, and measured in a later session, losing Redis costs
  a hold **6% at the median** and the sale drains all 500 seats with the lock gone.
- **Lease:** the reconciler now takes an advisory lock for each sweep, and with the lease
  in place two reconcilers lost zero races between them.

**A sixth run, `bash load/chaos.sh orders`, injects nothing: the fault is the customer**
(`DECISIONS.md` 019). It places orders of one to four seats and then confirms, cancels, or
does both at once, with the cancel sent at a random point inside the confirm. That covers
two changes no earlier run had ever loaded: the all-or-none sale (011), and cancel returning
the seats before the money (012). k6 only sees the answers, so the rig checks the orders in
Postgres afterwards. Across 1,943 orders in two runs, 1,298 of them multi-seat:
- no order was partly sold;
- no order had seats sold without the money taken or held;
- no order had money taken for seats that did not sell.

In the second run, 92 cancels arrived after their confirm had sold the seats. Each heard
`SoldToYou` and stepped back.

Reports land in `load/results/` beside the summaries, and are gitignored for the same
reason.

## Watching it

```bash
docker compose --profile telemetry up -d lgtm
OTEL_ENDPOINT=http://lgtm:4317 docker compose --profile strangled up -d --build
```

Grafana is at [http://localhost:3000](http://localhost:3000), with Tempo, Prometheus and Loki
behind it. For `dotnet run`, set `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317`.
Telemetry is **off unless that variable names a collector**, so a load run measures the
system without it (`DECISIONS.md` 021).

The instrumentation follows the same asymmetry as the code. Every module gets the
framework's spans: HTTP in and out, and every Npgsql query. Only Inventory has instruments of
its own, recorded at its adapters' edges through the BCL's `ActivitySource` and `Meter`. The
Domain and the use cases do not know them.

| Metric | What it answers |
|---|---|
| `encore.inventory.seat.outcomes` | What each seat answered, by action: held, lost the race, already sold… |
| `encore.inventory.lock.attempts` | What Redis answered, and how often the cooldown answered for it |
| `encore.inventory.outbox.deliveries` | Delivered, failed, dead-lettered, by event type |
| `encore.inventory.outbox.delivery.lag` | How late "late is not wrong" is, from the seat change to delivery |

A confirm is one trace across both processes, with authorise and capture as server spans in
`encore-payments`. An outbox delivery is a trace of its own, **linked** to the request that
raised the event rather than joined to it: that request ended long before, and a
redelivery would give it a second child. Background polls are sampled out. Each would
otherwise be a one-span trace every second.

## Status

Load-In is closed, and Soundcheck's two outcomes — the outbox and the Payments extraction —
are built. The work since has been load and chaos testing.

- **Inventory is complete**: aggregate, ports, adapters, use cases, HTTP surface, outbox,
  expired-hold sweep, and the concurrency tests that prove it.
- **Catalog, Orders, Payments and Notifications are implemented and flat.** Orders prices
  through Catalog, holds through Inventory and charges through Payments without referencing
  any of them.
- **A confirm authorises, sells, then captures** (`DECISIONS.md` 010), and **no path ends
  with seats sold and nobody paying**: an order's seats sell in one transaction (011), and a
  cancel gives the seats back before the money (012).
- **Timed-out payments are reconciled** by asking the gateway what it actually did (014).
- **Payments runs as its own service** (018). The extraction was inert for four days — a
  `services.Replace` that ran before the registration it meant to replace — and the chaos rig
  found it by stopping the service and watching confirms keep succeeding.
- **OpenTelemetry**, brought forward from On Tour (021). Off by default. What exporting costs
  a flash sale has not been measured yet.

Open, and named rather than hidden:

- `Payment` raises no domain events, so nothing tells an order that the reconciler released
  its authorisation. That needs Payments to have an outbox of its own (013).
- Losing Redis still costs a purchase 1.78× at the median. The lock now stops asking Redis
  for a second after a refusal; what that saves has not been measured yet (019).
- The client lock has not been measured against a Postgres-side serialisation of the cap
  check (005).
- The `payments` schema still lives in the shared Postgres: the process boundary moved and
  the data boundary did not (018).
- Identity does not exist, so `X-Client-Id` remains a claimed identity.

Deliberately absent, by roadmap phase rather than oversight: MediatR, MassTransit, SignalR
and any deployment story.

| Phase | Weeks | Focus |
|---|---|---|
| Load-In | 1–3 | Modular monolith, DDD tactical patterns, TDD foundation |
| Soundcheck | 4–6 | Extract Payments and Notifications via Strangler Fig + Outbox |
| **Showtime** | 7–10 | Inventory concurrency, load testing, chaos experiments |
| On Tour | 11–12+ | Cloud deploy, observability, write-up |
