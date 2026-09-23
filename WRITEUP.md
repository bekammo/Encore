# Encore: one hard problem, taken seriously

Encore is a ticketing platform I built to answer one question honestly: **where is software
architecture worth paying for?** Most portfolio projects answer by applying the same pattern
everywhere. This one applies it in exactly one place, and spends the rest of its effort showing
what that buys and what it costs.

The place is the flash sale: tickets go on sale, a few thousand clients click at once, and
many of them want the same seat. Everything interesting in this repository follows from two
people clicking one seat in the same millisecond. Everything else is deliberately plain.

This is the short version. [DECISIONS.md](DECISIONS.md) has every choice with the alternative it
beat and what it cost, and the [README](README.md) has how to run it.

## Architecture is a cost you pay for optionality

Encore is a modular monolith with five modules. Four of them — Catalog, Orders, Payments and
Notifications — are flat: endpoints, a `DbContext` and some models. They are CRUD over rows
nobody fights over, and ports and adapters there would be an interface whose only job is to
say "save this row", with nothing protected and nothing ever substituted.

Inventory is hexagonal, and its domain is a separate assembly that the build proves has no
infrastructure in it. Three MSBuild rules fail the build if a package, a framework reference
or anything transitive reaches it, and architecture tests check the same thing again over
compiled metadata. The seat rules are tested against a fake clock in microseconds, and the
mechanism that settles a race changed several times without them moving.

**The asymmetry is the argument.** Optionality is only worth paying for where it will be
spent, and in this system that is one module. The flat modules pay in their own currency:
`CheckoutService` can only be tested against real Postgres, because an in-memory provider would
fake away the partial unique index that stops a client opening two checkouts.

## The seat is the consistency boundary

A hold is not an entity. It is two columns on the seat row, `HeldByClientId` and
`HoldExpiresAt`, so acquiring, losing or converting a hold is always a change to one row. `Seat`
is the aggregate root and the only consistency boundary; it is born `Available`, and a closed
set of transitions moves it: hold, release, sell. Refusals are one exception type with a closed
reason enum, which the layer above maps to closed outcomes and HTTP maps to a `409` with a
machine-readable `reason`.

Expiry is lazy. A `Held` row whose `HoldExpiresAt` has passed is logically `Available` on every
read and write path, whatever the column says. A background sweep tidies the rows, but it is
cleanup only, and there is a test class whose whole purpose is to run the system with the sweep
switched off and show that nothing depends on it. **If a test cannot pass with the sweep
disabled, the design is broken.** The same rule later governs the outbox dispatcher.

## Postgres settles the race; Redis only thins the crowd

Two clients, one seat, one millisecond. Postgres's `xmin` system column is the seat's
concurrency token, so every write is a conditional `UPDATE` the database maintains the token
for, and exactly one of the two wins. The loser reloads and asks again **once**, which turns a
bare "you lost a race" into the accurate "somebody has it". Retrying harder when the system is
busiest is how a thundering herd gets worse.

There used to be a per-seat Redis lock as well. It excluded nobody — every handler proceeded
whatever it said, because `xmin` would decide anyway — and it cost two round trips per action.
Removing it bought 18% more throughput and raised lost races from 0.10% to 0.23% of attempts,
each one a retriable `409`. That trade was taken on purpose.

**Correctness survives Redis being gone entirely**, and that is demonstrated rather than
claimed: under 250 virtual users with Redis stopped, holds kept being won and nothing oversold.

## The one rule no row can enforce

A client may hold four seats per event. That rule spans four rows, so the aggregate cannot
enforce it; it lives in the application layer as a Postgres count of the client's live holds,
serialised by a lock on client and event.

The lock returns one of three answers, and the third is the decision. `HeldByAnother` refuses
the request as retriable. `Unavailable` — Redis did not answer — **proceeds, and accepts that
the cap may be breached**. A cap breach is a refund email; an oversell is a customer outside a
sold-out venue. Refusing every hold in the system because Redis blinked would turn the lesser
harm into an outage.

The first version of this enforced nothing at all, and a test found it: twelve simultaneous
requests from one client produced twelve holds, because the lock returned a token or `null` and
the handler proceeded on `null`. The failure hid itself, since the lock is only contended in
exactly the case the cap exists for.

The open question was whether Redis is the right place for that lock at all, so the same port
got a Postgres implementation on `pg_try_advisory_lock`, and both ran twice per configuration.
The Postgres lock costs where the lock does nothing: on a sale where no client contends with
itself, throughput fell about 16%, because every hold now takes a second connection to the
busiest server. It pays where Redis fails: with Redis stopped, a purchase cost what it did
with Redis up, and the cap stayed enforced rather than best-effort. **Redis stays the default**
— sale-day throughput is what the system is for — and the advisory lock is one configuration
key away for the day the cap has to hold through an outage.

## Selling an order: put the unrecoverable step in the middle

A confirm **authorises, sells, then captures**. A sold seat is terminal; money can be given
back. So the step that cannot be undone goes between two that can. The obvious alternative —
charge once, refund on failure — loses on one asymmetry: when a gateway times out, a lost
authorisation expires on its own, while a stray charge sits there until someone reconciles it.

An order's seats sell in one transaction, all or none. "One aggregate per transaction" was
considered and not followed: the rule exists for aggregates in different stores and for
transactions that hold many locks for long, and neither applies to four rows written in one
round trip. The transaction adds atomicity, not an invariant. Each seat still decides its own
transition.

A cancel runs the same argument backwards: **it gives the seats back before the money.** The
first version voided first, and a cancel racing a confirm could end with seats sold and nobody
paying. Under load, across 1,943 orders — 1,298 of them multi-seat, with cancels fired at
random points inside each confirm — no order was partly sold, none had seats without money and
none had money without seats. In 92 of them the cancel arrived after the sale and correctly
stepped back.

## The sale and its announcement cannot disagree

Every seat transition is written to an outbox table in the seat's own transaction, by the unit
of work rather than the repository, because a four-seat checkout drives four holds through one
context. A dispatcher claims rows with `FOR UPDATE SKIP LOCKED` and delivers at least once;
Notifications is the consumer, idempotent on a unique index. What crosses the wire is a
versioned contract, never the domain record.

The dispatcher delivers late, never wrong. With it switched off under load, 21,948 events piled
up undelivered and the sale still sold exactly 500 of 500 seats. Measured, the drain inside the
seat transaction cost almost nothing — a refused hold never reaches the save, so only about
15,000 of 304,000 iterations wrote anything — and **the dispatcher was the whole cost**, as a
second workload on the same database.

## Strangling Payments, and the four days it did nothing

Payments was extracted into its own service with Strangler Fig. Orders reaches it through the
same interface it called in process; one configuration key picks the HTTP adapter or the
in-process one. Nothing inside Payments changed, which was the modular monolith's claim from
the start.

For four days the extraction was inert. Orders registered its HTTP adapter with
`services.Replace`, which removes the first *existing* registration — and Orders was registered
before Payments, so there was nothing to remove. Payments then appended its in-process adapter,
and last-registration-wins gave it every payment. Both sides' tests were green and would have
stayed green forever: each was tested against a stub of the other. **A seam is not proven by
testing each side of it.** It was found by the chaos rig, which stopped the Payments service and
watched confirms keep succeeding. Every extraction now gets a composition test that resolves the
seam from a real container in every registration order.

## Measure first, then break it on purpose

A k6 harness drives a contention scenario (50 clients fighting over 5 seats) and a flash sale
(100 clients, 500 seats), and asserts two invariants rather than latencies: **no oversell**, and
**no unexpected responses** — a `409` is the system working; a `500` or a timeout is not. There
is deliberately no latency threshold: an SLO invented before the first measurement is a guess
wearing a test's clothing.

The chaos rig injects one fault per run, in the gaps between scenario windows, each with a
control window of identical shape beside it, and reads the aftermath out of Postgres:

| Fault | Invariants | What it exposed |
|---|---|---|
| Payments stopped | held | the extraction was inert; then, that a stopped container swallows connections, so every confirm waited out a 10 s timeout |
| Two reconcilers | held | the simulated gateway's memory was per process: a restart settled 120 of 121 timed-out payments as abandoned |
| Redis stopped | held; no oversell | a hold cost 85× without Redis |
| Dispatcher stalled 20 s | held | the claim transaction stayed open the whole time |
| Customer races cancel against confirm | held | 92 cancels stepped back after the sale |

The Redis cost took four sessions to remove, and the order is the lesson. Connection pools were
over the database's limit; timeouts were cut from 5 s to 250 ms and the cost fell to 22×; tripling
the connect timeout moved nothing, which ruled it out; `FailFast` removed the rest of the
per-attempt second, taking a hold's median cost of losing Redis to 6%. **A change is never
measured by the session that motivated it** — each fix got its own run.

The last step was a one-second cooldown after a refusal, measured against its own control:
a purchase's median cost of losing Redis fell from 1.53×–1.73× to 1.42×–1.47×. Real, because
the ranges do not overlap, and small. What remains belongs to losing the lock rather than to
asking for it, and I have not attributed it — which is a more honest place to stop than a
number I cannot explain.

## Watching it

OpenTelemetry follows the same asymmetry. Every module gets the framework's spans — HTTP in and
out, every Npgsql query — and only Inventory has instruments of its own: what each seat
answered, who answered for the lock, and how late the outbox is. They are recorded through the
BCL's `ActivitySource` and `Meter` at the adapters' edges, so the domain never learns they
exist, and the OpenTelemetry packages live in one host-side project that the architecture tests
forbid any module to reference.

A confirm is one trace across both processes. An outbox delivery is a trace of its own,
**linked** to the request that raised the event rather than joined to it: that request ended
long before, and a redelivery would give it a second child. The first live run showed every
background poll arriving as its own one-span trace each second, burying the requests; a root
sampler now drops client spans that nothing started.

Telemetry is off unless a collector is configured, and what it costs was measured rather than
assumed. With the collector warm in both arms and every export checked for arrival, exporting
everything cost about 25% throughput on one laptop, where the SDK and the collector share a CPU.
The dashboard earned its place on the first look: the outbox-lag histogram was using buckets
sized for milliseconds, so every delivery landed in the first one and it read p99 = 5 s. With
buckets in seconds, deliveries take 0.1–2.5 s, which is the dispatcher's one-second poll.

## What is not here

No MediatR, no message bus, no Polly, no frontend and no deployment, by choice rather than
oversight. The client identity is a claimed header, a stand-in for an Identity module that does
not exist. Payments still shares the database: the process boundary moved and the data boundary
did not. And everything above was measured on one laptop, mostly one run per configuration —
the counts and the mechanisms behind them are strong; latency comparisons across sessions are
not.
