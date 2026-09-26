# Encore: one hard problem, taken seriously

I built Encore to answer one question: **where is software architecture actually worth paying
for?** The easy answer is to apply one pattern everywhere and call it consistency. Encore
applies ports and adapters in exactly one place, and spends the rest of its effort showing
what that buys and what it costs.

That place is the flash sale. Tickets go on sale, a few thousand clients click at once, and
plenty of them want the same seat. Nearly everything interesting in this repository follows
from two people clicking one seat in the same millisecond. Everything else is plain, and
meant to be.

This is the short version. [DECISIONS.md](DECISIONS.md) has each choice with the alternative
it beat and what it cost, and the [README](README.md) covers running it.

## Architecture is a cost, paid for optionality

Encore is a modular monolith with five modules. Four of them (Catalog, Orders, Payments and
Notifications) are flat: endpoints, a `DbContext` and some models. They're CRUD over rows
nobody fights over. Ports and adapters there would mean an interface whose only job is to say
"save this row", protecting nothing and never swapped for anything.

Inventory is hexagonal, and its domain is a separate assembly that the build proves is free of
infrastructure. Three MSBuild rules fail the build if a package, a framework reference or
anything transitive reaches it, and architecture tests check the same thing again over
compiled metadata. The payoff is concrete: the seat rules are tested against a fake clock in
microseconds, and the mechanism that settles a race changed several times without the rules
moving.

**The asymmetry is the argument.** Optionality is worth paying for only where you'll spend it,
and in this system that's one module. The flat modules pay in their own currency.
`CheckoutService` can only be tested against real Postgres, because an in-memory provider
would fake away the partial unique index that stops a client opening two checkouts.

## The seat is the consistency boundary

A hold isn't an entity. It's two columns on the seat row, `HeldByClientId` and
`HoldExpiresAt`, so taking, losing or converting a hold always changes one row. `Seat` is the
aggregate root and the only consistency boundary. It starts `Available`, and a closed set of
transitions moves it: hold, release, sell. A seat's refusal carries a closed reason enum all
the way out to the `409` and its machine-readable `reason`.

Expiry is lazy. A `Held` row whose `HoldExpiresAt` has passed counts as `Available` on every
read and write path, whatever the column says. A background sweep tidies the rows, but it's
cleanup only, and one test class exists purely to run the system with the sweep switched off
and show that nothing depends on it. The rule I held the design to: **if a test can't pass
with the sweep disabled, the design is broken.** The outbox dispatcher later got the same
rule.

## Postgres settles the race

Two clients, one seat, one millisecond. The seat's concurrency token is Postgres's `xmin`
system column, so every write is a conditional `UPDATE` on a token the database maintains
itself, and exactly one of the two writes wins. The loser reloads and asks again **once**.
That turns a bare "you lost a race" into the more useful "somebody has it". Retrying harder
when the system is at its busiest is how a thundering herd gets worse.

There used to be a per-seat Redis lock as well. It excluded nobody: every handler went ahead
whatever the lock said, because `xmin` would decide anyway. What it did do was cost two round
trips per action. Removing it bought 18% more throughput and raised lost races from 0.10% to
0.23% of attempts, each one a retriable `409`. I took that trade.

**Correctness survives Redis disappearing entirely**, and that's been shown under load, not
just argued: with 250 virtual users and Redis stopped, holds kept being won and nothing
oversold.

## The one rule no row can enforce

A client may hold four seats per event. That rule spans four rows, so no single aggregate can
enforce it. It lives in the application layer instead, as a Postgres count of the client's
live holds, serialised by a lock on the client and event.

The lock has three possible answers, and the third is where the real decision sits.
`Acquired` proceeds. `HeldByAnother` refuses the request as retriable. `Unavailable`, meaning
Redis didn't answer, **proceeds anyway and accepts that the cap may be breached**. A cap
breach costs a refund email. An oversell leaves a customer outside a sold-out venue. Refusing
every hold in the system because Redis blinked would turn the smaller harm into an outage.

The first version enforced nothing at all, and a test caught it: twelve simultaneous requests
from one client produced twelve holds. The lock returned a token or `null`, and the handler
carried on when it got `null`. The bug hid itself well, because the lock is only contended in
exactly the case the cap exists for.

Was Redis even the right home for this lock? The same port got a Postgres implementation on
`pg_try_advisory_lock`, and both ran twice per configuration. On an ordinary sale the Postgres
lock cost about 16% of throughput, because every hold now takes a second connection to the
busiest server. With Redis stopped, a purchase cost what it did with Redis up, and the cap
stayed enforced. **Redis stays the default**, since sale-day throughput is what the system is
for, and the advisory lock is one configuration key away for the day the cap has to survive
an outage.

## Selling an order: put the unrecoverable step in the middle

A confirm **authorises, sells, then captures**. A sold seat is final; money can be given
back. So the step that can't be undone goes between two that can. The obvious alternative is
to charge once and refund on failure, and it loses on one asymmetry: when a gateway times
out, a lost authorisation expires on its own, while a stray charge sits there until somebody
reconciles it.

An order's seats sell in one transaction, all or none. That knowingly breaks "one aggregate
per transaction", a rule meant for aggregates in different stores and transactions that hold
many locks for a long time. This is four rows written in one round trip, and each seat still
decides its own transition.

A cancel runs the same argument backwards: **it gives the seats back before touching the
money.** The first version voided first, and a cancel racing a confirm could end with seats
sold and nobody paying. Under load, across 1,943 orders (1,298 of them multi-seat, with
cancels fired at random points inside each confirm), no order was partly sold, none had seats
without money, and none had money without seats. In 92 of them the cancel arrived after the
sale and correctly stepped back. Those three invariants are now a test that composes the real
modules and races every confirm against its cancel, so they're checked on every run, not
only in that session.

The ordering has one failure it can't prevent: a gateway that authorised the money and then
refuses to hand it over. For most of the project the simulator never did that, and the code
would have called such an order `failed`, with its seats sold and nobody looking for the
money. Now the order reads **`payment_due`**: every seat sold, nothing held, and the
customer's next confirm authorises again and captures without selling anything twice.
Unselling the seats would break the one rule the ordering exists to protect, and a job that
re-charged the card would be charging a customer who hadn't asked.

## A sale and its announcement can't disagree

Every seat transition is written to an outbox table inside the seat's own transaction. The
unit of work does the writing, not the repository, because a four-seat checkout drives four
holds through one context. A dispatcher claims rows with `FOR UPDATE SKIP LOCKED` and
delivers at least once. Notifications is the consumer, kept idempotent by a unique index.
What crosses the wire is a versioned contract, never the domain record.

The dispatcher can be late, but it's never wrong. With it switched off under load, 21,948
events piled up undelivered and the sale still sold exactly 500 of 500 seats. Measured, the
drain inside the seat transaction cost almost nothing, because a refused hold never reaches
the save: only about 15,000 of 304,000 iterations wrote anything. **The dispatcher was the
whole cost**, as a second workload on the same database, and most of it was fixable. Its
claim read the entire due backlog every tick, and EF Core logged every statement. With both
fixed, three runs with the dispatcher on came in inside the pre-outbox spread or below it,
apart from one outlier purchase p99 ([019][d019]).

## Strangling Payments, and the four days it did nothing

I extracted Payments into its own service using the Strangler Fig pattern. Orders reaches it
through the same interface it called in process, and one configuration key picks the HTTP
adapter or the in-process one. Nothing inside Payments had to change, which is what a
modular monolith promises from the start.

For four days the extraction did nothing. Orders registered its HTTP adapter with
`services.Replace`, which removes the first *existing* registration. But Orders was
registered before Payments, so there was nothing to remove. Payments then appended its
in-process adapter, and because the last registration wins, it got every payment. Both
sides' tests were green and would have stayed green forever, since each side was tested
against a stub of the other. **A seam isn't proven by testing each side of it.** The chaos
rig found the bug by stopping the Payments service and watching confirms keep succeeding.
Every extraction now gets a composition test that resolves the seam from a real container, in
every registration order.

## Measure first, then break it on purpose

A k6 harness drives two scenarios: contention (50 clients fighting over 5 seats) and a flash
sale (100 clients, 500 seats). It asserts invariants, not latencies: **no oversell**, and
**no unexpected responses**. A `409` is the system doing its job; a `500` or a timeout isn't.
There's no latency threshold, and that's intentional. An SLO invented before the first
measurement is a guess dressed up as a test.

The chaos rig injects one fault per run and reads the aftermath out of Postgres. When a fault
asks a question about a number, it runs next to a control window of the same shape.

| Fault | Invariants | What it exposed |
|---|---|---|
| Payments stopped | held | first, that the extraction was inert; then, that a stopped container swallows connections, so every confirm waited out a 10 s timeout (a one-second connect timeout now bounds it) |
| Two reconcilers | held | the simulated gateway's memory was per process: a restart settled 120 of 121 timed-out payments as abandoned |
| Redis stopped | held; no oversell | a hold cost 85× without Redis |
| Dispatcher stalled 20 s | held | the claim transaction stayed open the whole time |
| Customer races cancel against confirm | held | 92 cancels stepped back after the sale |

Getting the Redis cost down took four sessions, and the order is the lesson. First, the
connection pools were over the database's limit. Cutting the timeouts from 5 s to 250 ms took
the cost to 22×. Tripling the connect timeout moved nothing, which ruled it out. `FailFast`
removed the rest of the per-attempt second, and a hold's median cost of losing Redis fell to
6%. **A change is never measured by the session that motivated it**, so each fix got a run
of its own.

The last step, a one-second cooldown after a refusal, was measured against its own control.
A purchase's median cost of losing Redis fell from 1.53–1.73× to 1.42–1.47×: real, since the
ranges don't overlap, but small. What's left belongs to losing the lock rather than asking
for it, and I haven't attributed it. I'd rather stop there than quote a number I can't
explain.

The most useful run came after the project felt finished. Before quoting the numbers again I
re-measured them, and a purchase with Redis gone now cost 2.6–4.3× instead of 1.8×. Running
the same fault against older commits in the same session traced the cause to an architecture
audit. The audit had merged a hold's two reads into one query and, to sort the rows it
returned, compiled an expression tree on every request. The audit's own baseline ran 50
clients, where that cost hid; the Redis fault runs 250. Counting the rows instead doubled the
baseline's throughput in that session, to 730,000–770,000 hold attempts a run. The regression
was a performance change nobody had measured under load, and only running the harness again
could have found it.

## Watching it

Telemetry follows the same asymmetry. Every module gets the framework's spans (HTTP in and
out, every Npgsql query), but only Inventory has instruments of its own: what each seat
answered, who answered for the lock, and how far behind the outbox is. They're recorded
through the BCL's `ActivitySource` and `Meter` at the adapters' edges, so the domain never
learns they exist. The OpenTelemetry packages live in one host-side project, and the
architecture tests forbid any module to reference it.

A confirm is one trace across both processes. An outbox delivery gets a trace of its own,
**linked** to the request that raised the event rather than nested under it. That request
finished long before, and a redelivery would give it a second child. The first live run
showed every background poll arriving as its own one-span trace each second, burying the
requests, so a root sampler now drops client spans that nothing started.

Telemetry is off unless a collector is configured. Measured with every export checked for
arrival, exporting everything cost about 25% of throughput on one laptop, where the SDK and
the collector share a CPU. The dashboard paid for itself on the first look: the outbox-lag
histogram used buckets sized for milliseconds, so every delivery landed in the first one and
p99 read 5 s. With buckets in seconds, deliveries take 0.1–2.5 s, which is just the
dispatcher's one-second poll.

## What isn't here

No MediatR, no message bus, no Polly, no frontend and no standing deployment, and none of
that is an oversight. The client identity is a header the client claims, standing in for an
Identity module that doesn't exist. Payments still shares the database: the process boundary
moved, the data boundary didn't.

Everything above was measured on one laptop, mostly one run per configuration. The counts,
and the mechanisms behind them, are solid. Latency comparisons across sessions aren't. I
planned a repeat with the load generator on a machine of its own and then dropped it
([035][d035]), so that caveat stays.

[d019]: DECISIONS.md#019--measure-first-then-break-it-on-purpose
[d035]: DECISIONS.md#035--the-multi-host-run-is-dropped
