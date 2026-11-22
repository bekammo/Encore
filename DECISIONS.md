# Decisions

The fifteen choices in Encore most worth defending, each with the alternative it beat and what
it costs. Code comments cite entries by number, so a comment ending in `(011)` points here.

If you only read five, read [001](#001--inventory-is-hexagonal-and-everything-else-is-flat)
for the thesis, [005](#005--the-hold-cap-is-a-policy-not-an-invariant) for a rule that is
allowed to fail open,
[009](#009--authorise-sell-capture-and-a-cancel-runs-it-backwards) for why the sale sits
between authorise and capture,
[014](#014--payments-becomes-a-service-and-a-seam-is-not-proven-by-testing-each-side-of-it)
for an extraction that did nothing until a chaos run caught it, and
[015](#015--measure-first-then-break-it-on-purpose) for what measuring, and measuring again,
found.

A new decision gets a new entry; a change to an existing one is folded into that entry, and git
keeps the old text. On 2026-10-08 the log was cut from 35 entries to these fifteen
(`git show 51b8d25:DECISIONS.md`); the 80-entry working log it grew from is at
`git show 2e5ad70:DECISIONS.md`.

## Index

- [001](#001--inventory-is-hexagonal-and-everything-else-is-flat) — Inventory is hexagonal, and everything else is flat
- [002](#002--the-domains-purity-is-enforced-by-the-compiler-the-build-and-a-test) — The domain's purity is enforced by the compiler, the build and a test
- [003](#003--the-seat-state-machine) — The seat state machine
- [004](#004--postgres-alone-settles-a-race-for-a-seat) — Postgres alone settles a race for a seat
- [005](#005--the-hold-cap-is-a-policy-not-an-invariant) — The hold cap is a policy, not an invariant
- [006](#006--expiry-is-lazy-and-the-sweep-is-cleanup) — Expiry is lazy, and the sweep is cleanup
- [007](#007--the-http-surface-actions-one-status-rule-and-no-route-around-checkout) — The HTTP surface: actions, one status rule, and no route around checkout
- [008](#008--orders-records-the-expiry-inventory-decides-it) — Orders records the expiry; Inventory decides it
- [009](#009--authorise-sell-capture-and-a-cancel-runs-it-backwards) — Authorise, sell, capture, and a cancel runs it backwards
- [010](#010--an-orders-seats-sell-together-or-not-at-all) — An order's seats sell together or not at all
- [011](#011--one-live-payment-attempt-per-order-and-a-timeout-is-settled-by-asking-the-gateway) — One live payment attempt per order, and a timeout is settled by asking the gateway
- [012](#012--the-outbox-commits-with-the-seat-and-delivers-late-never-wrong) — The outbox commits with the seat and delivers late, never wrong
- [013](#013--modules-meet-through-contracts-and-shared-code-may-not-name-a-module) — Modules meet through contracts, and shared code may not name a module
- [014](#014--payments-becomes-a-service-and-a-seam-is-not-proven-by-testing-each-side-of-it) — Payments becomes a service, and a seam is not proven by testing each side of it
- [015](#015--measure-first-then-break-it-on-purpose) — Measure first, then break it on purpose

---

## 001 — Inventory is hexagonal, and everything else is flat

Inventory holds the one hard problem: two people clicking the same seat in the same
millisecond must not both get it. The mechanism that prevents it was always going to change,
and it did, several times. Ports and adapters let it change without touching the seat rules,
which are tested against a fake clock in microseconds. Catalog, Orders, Payments and
Notifications are CRUD over tables nobody contends for. The same structure there would be an
interface to say "save this row", protecting nothing and never swapped.

**Architecture is a cost paid for optionality, so pay it only where the optionality gets
spent.** That's the argument this repository makes, and the one I'd most like to be asked
about.

The flat modules don't get to borrow it. `CheckoutService` takes `OrdersDbContext` directly,
with no `IOrderRepository`; the interfaces Orders does use exist because they cross module
boundaries (013). The bill arrives in the tests: `CheckoutService` can only be tested against
real Postgres, because EF Core's in-memory provider doesn't enforce the partial unique index
that stops a client opening two checkouts.

**Money is a `decimal` and a currency code, not a value object**, stored as `numeric(19,4)`.
A `Money` type earns its keep by preventing something, and nothing here does more than sum one
order in one currency; it arrives with tax, fees or a second currency. `Payment` is the one
flat type with guarded transitions, and 011 explains why that doesn't break this rule.

**Telemetry follows the same asymmetry.** OpenTelemetry's packages live in `Encore.Telemetry`,
a host-side project that only the hosts may reference, so the hosts keep their no-packages
rule (002) and no module references OpenTelemetry. Modules emit through the BCL's
`ActivitySource` and `Meter` at the adapters' edges, so the Domain and the use cases never
learn telemetry exists, and the hosts subscribe to `Encore.*` by wildcard without naming a
module. Only Inventory has instruments of its own; the flat modules get the framework's spans.
It's off unless `OTEL_EXPORTER_OTLP_ENDPOINT` is set, so load runs measure the system without
it: exporting cost about 25% of throughput on a laptop where the SDK and the collector share a
CPU. The root sampler drops client spans nothing started, which had buried the requests under
one-span traces from every background poll.

---

## 002 — The domain's purity is enforced by the compiler, the build and a test

`Encore.Modules.Inventory.Domain` is its own assembly, not a folder. In a folder, "no
infrastructure in the domain" is a convention that lasts as long as everyone remembers it. In
its own project, code can't name a type from an assembly it doesn't reference.

**Three build rules in `Directory.Build.targets`.** `ENCORE001` fails the build on any
`PackageReference`, `ENCORE002` on a `FrameworkReference` beyond the BCL, and `ENCORE003` on
anything outside the BCL in the *resolved* reference closure. The third is the one that
matters, because infrastructure arriving transitively through a `ProjectReference` slips past
rules that only read what a csproj declares. It works because BCL assemblies carry
`NuGetPackageId = Microsoft.NETCore.App.Ref`, packages carry their own id, and project outputs
carry none. Adding `StackExchange.Redis` to `Encore.Shared` proves it: `ENCORE003` fails the
Domain, naming Redis and its dependencies. Projects opt in by property, not by name, so a
rename can't change what's checked, and `Encore.Shared` and the three `.Contracts` assemblies
opt in too.

**The architecture tests use no library**, since nearly every assertion is a
`GetReferencedAssemblies()` one-liner. They read both compiled metadata and the csprojs,
because only a csproj shows a declared but unused reference, and they compare module names
exactly: `Inventory.Contracts` starts with `Inventory`.

**A port never names an adapter's type**, not even in a doc comment. `ISeatRepository` once
documented EF Core's `DbUpdateConcurrencyException`, tying every caller that handles a lost
race to EF Core. It now throws the Domain's `ConcurrentSeatModificationException`, and the
adapter translates.

**Cost:** the build relies on SDK item metadata. If an SDK stopped populating
`NuGetPackageId`, the build would break loudly on the first run, which is the right way round.

---

## 003 — The seat state machine

`Seat` is the aggregate root and the only consistency boundary. A hold isn't an entity: it's
the `HeldByClientId` / `HoldExpiresAt` pair on the seat row. Every rule about when a seat
changes hands lives in the transitions, nothing above the aggregate re-decides one, and the
tests came first.

**The aggregate owns construction and duration.** `Seat.Create` is the only way in, so
nothing can write `new Seat { Status = Sold }`. `Hold(clientId, utcNow)` takes the current
instant, never an expiry, or a caller could hold a seat until the year 3000. Holds last five
minutes, and `utcNow` must be UTC: a time with no zone is a different instant in London and
Los Angeles.

**The rules a reviewer would push back on:**
- *Re-holding your own seat is a no-op, and the expiry doesn't move*, so a retry never says
  your own seat is taken and a hold can't be stretched forever.
- *Reclaiming a lapsed hold raises `SeatReleased(Expired)` before `SeatHeld`*, even for its
  own holder. One event alone would leave the old claim looking live, and history can't be
  backfilled.
- *There's no route from `Available` to `Sold`.* A sale that skipped the hold would be a
  double-sell `xmin` can't catch, because the two writers never contend on the same row
  version.
- *`Sold` keeps `HeldByClientId`*, so a repeated purchase succeeds and a cancel recognises its
  own confirm (009). `Sold` is terminal.
- *Only the holder may release.* Releasing an `Available` seat or a lapsed hold is a silent
  no-op, and the release path has no opinion about expiry (006).
- *Expiry is exclusive:* `HoldExpiresAt <= utcNow` has expired, pinned by four tests one tick
  apart.

**Exceptions inside the aggregate, closed outcomes above it.** Refusals throw one
`SeatTransitionException` with a closed reason enum, not a type per refusal, so callers branch
with a `switch` the compiler checks instead of `catch` blocks whose order matters. The handlers
catch, translate and return a closed outcome enum, because the layers disagree about what's
exceptional. Inside the aggregate an illegal transition means a rule was about to break, but
during a flash sale **losing a seat to somebody else is the most common outcome there is**,
and exceptions on the common path cost throughput when it's scarcest. Each handler catches
exactly the reasons its transition can produce and lets anything else propagate, because an
unknown reason means the aggregate's contract moved.

- **Buying a seat you already bought returns `Sold`**, since a lost response or a
  double-submitted form is asking for a state that already holds.
- **Every seat command carries its event id, checked and never trusted**, or any event's URL
  would buy any seat. A mismatch answers `SeatNotFound`, so nobody can enumerate seats through
  an event they can't see.
- **A repeated seat id is refused, not de-duplicated.** `[A, A, B]` is `400 duplicate_seat`:
  de-duplicating would make the cap judge a different number than the client sent, and return
  fewer lines than were requested.

---

## 004 — Postgres alone settles a race for a seat

Postgres is the source of truth for seat state. `RowVersion` maps to the `xmin` system column,
so the database maintains the concurrency token and no code has to. Being a system column,
`xmin` must never appear in a migration's `CreateTable`, and `MigrationConventionTests`
rejects it in every migration.

**There's no per-seat lock.** Every handler used to take a Redis lock per seat and then carry
on whatever it answered, because `xmin` would settle the race anyway. It excluded nobody and
cost two round trips per action. Removing it bought 18% more throughput and raised lost races
from 0.10% to 0.23% of attempts (015), because its round trips had been spreading writers out.
No invariant moved, and the lock shouldn't come back to buy those races back. The client lock
(005) is the one lock doing a job no row's token can.

**A lost race is retried exactly once.** One reload turns "you lost a race" into the accurate
"somebody has it", and retrying harder at peak load is how a thundering herd gets worse. The
retry detaches the tracked seat, or EF Core's identity map would hand back the stale token,
and it clears domain events so a rejected attempt can't publish a hold that never happened.

**Redis is never a correctness dependency.** The lock answers `Acquired`, `HeldByAnother` or
`Unavailable` (005), and the adapter translates only Redis's connection and timeout
exceptions, so a malformed script isn't filed as "unavailable". It's built with
`AbortOnConnectFail = false`, because otherwise a missing Redis threw inside the DI factory and
stopped the host: a component's construction is part of its failure surface. Releases ignore
the request's cancellation, so a client hanging up can't strand a lock until its TTL. 015
shows correctness surviving Redis being gone entirely, under load.

---

## 005 — The hold cap is a policy, not an invariant

A client may hold at most four seats per event. The rule spans four rows and `Seat` is a
one-row boundary, so it lives in the application layer: a Postgres count of the client's live
holds, serialised by a Redis lock on client and event. Counting in Postgres keeps the count
right about expiry, with no counter to drift. That lock is the cap's only guard.

**The first version enforced nothing, and a test found it.** One client, twelve simultaneous
requests, cap of four: twelve holds. The lock doesn't wait, so one request took it, eleven got
`null`, and the handler proceeded on `null`. That's right for a lock backed by a concurrency
token and catastrophic for one backed by nothing. The bug hid itself, because the lock is only
contended when one client has several requests in flight, exactly the case the cap exists
for. The lock now has three answers (004), and the policy is explicit:

| Client + event lock | Answer |
|---|---|
| `HeldByAnother` | refuse with `ConcurrentRequestInFlight`, retriable |
| `Unavailable` | proceed, and accept that the cap may be breached |

**The second row is the decision.** With Redis down, two simultaneous holds can both pass the
count and a client can take a fifth seat. A cap breach is a refund email; an oversell is a
customer outside a sold-out venue. Refusing every hold because Redis blinked would turn the
lesser harm into an outage.

**Cost:** a burst of twelve can yield a single hold, and a client that retries converges on
four. Tests pin both, because "no more than four" is also satisfied by granting one.

The limit is published as `SeatReservationLimits.MaxHoldsPerClientPerEvent` so Orders can
size a checkout up front. It's `static readonly`, not `const`, because a `const` is baked into
the caller at compile time and would go stale once the call crosses a process.

**Measured against a Postgres advisory lock.** `PostgresAdvisoryLock` implements the same port
(`Inventory:HoldCapLock = Postgres`), and the cap suite runs against both. It cost about 16%
of throughput on the monolith baseline, where no client contends with itself, because each
hold takes a second pooled connection to the busiest server. With Redis stopped it paid off: a
purchase cost what it did with Redis up (0.94–0.96× at the median, against 1.4–1.7× for the
Redis lock), and the cap stayed enforced. **Redis stays the default**, because sale-day
throughput is what this system is for and a Redis outage is the rare case, already priced.
Switching costs one key and a second pool of 100 connections per host.

---

## 006 — Expiry is lazy, and the sweep is cleanup

A `Held` seat whose `HoldExpiresAt` has passed is `Available` on every read and write path,
whatever the column says. A timer that an invariant depends on is an invariant that fails
whenever the timer runs late, so the background sweep only tidies the table. The test of the
design: **if a test can't pass with the sweep disabled, the sweep has become load-bearing and
the design is broken.**

`ExpiryWithoutTheSweepTests` is that test, run with no sweeper and no Redis. The next client
reclaims a lapsed hold, nobody can sell one, thirty clients reclaiming one lapsed seat produce
one winner, and the per-client cap reopens as holds lapse. That last one is subtle: the cap's
count carries a second copy of the expiry rule, and had it counted only `Status = Held`, a
client would stay capped until a job ran. `SWEEP_ENABLED=false` asks the same question under
load.

**The sweep goes through the aggregate**, not a bulk `UPDATE`, which would be cheaper but
publish nothing and leave hold history only for contended seats. `Seat.ExpireHold` names the
ending `Hold` already performed inline for a lapsed hold, and `Hold` delegates to it. **The
sweep decides nothing:** its SQL query only nominates ids, and the aggregate re-decides each
one, so a seat re-held since the query ran is refused. Two sweeps on one seat need no lease,
since `xmin` lets one through. Its partial index covers held rows only, and a test checks the
filter's enum literal against `SeatStatus.Held`.

**A swept seat keeps the record of its lapsed hold.** `ExpireHold` used to clear the holder
pair while `Sell` chose its refusal from the status, so the sweep changed the answer: before
it, the lapsed holder heard `HoldExpired`; after it, `NoActiveHold`. Orders records `Expired`
only when every refusal is `HoldExpired` (008), so the same lapsed order ended `Expired` or
`Failed` depending on the sweep's timing, which is exactly what this entry forbids. Now
`ExpireHold` sets only the status, and `Sell` reads the pair, so a swept seat answers exactly
as an unswept one. Only a release, a sale or a new hold changes the pair, and everything that
counts holds filters on `Held`. **Cost:** an `Available` row can still name a client.

---

## 007 — The HTTP surface: actions, one status rule, and no route around checkout

**Actions, not resources.** `POST …/hold`, `…/release`, `…/purchase`, and
`/orders/{id}/confirm` and `/cancel`. A hold isn't an entity (003), and the rules for ending an
order aren't the client's to apply by writing a status. Every action is idempotent, which is
what makes retrying a POST safe.

**One status rule.** Every refusal about the state of the world is a `409` with a `reason`,
`404` is only for the resource the URL names, and a mistake knowable from the request alone,
like a repeated seat id (003), is a `400`. So `hold_cap_reached` is 409, not 403 (nothing is
being authorised) or 429 (it isn't about rate). A `retriable` flag rides alongside. The
mappings are exhaustive switches with no default arm, so a new outcome breaks the build. A
timestamp without a timezone is refused rather than assumed to be UTC, which would open a sale
at the wrong instant. `AddProblemDetails` alone still left an unmatched route's 404, a 405, a
415 and an unbindable body's 400 empty; `UseStatusCodePages` gives them the same shape.

**`X-Client-Id` is a claimed identity, not authentication.** It stands in for an Identity
module that doesn't exist, so no customer route answers 401 or 403, and it arrives through a
route-group filter the next endpoint can't forget. Payments' routes are read-only for
customers, because **a client that can charge itself has walked around the order flow**.

**Nothing waits for Identity to close the rest.** With every route open, anyone could create
seats and buy them with no order and no money.
- *The seat actions are off by default.* `/hold`, `/release` and `/purchase` sell without a
  price, a payment or an on-sale check. They exist so the load harness can measure the hot
  path directly, and they're mapped only when `Inventory:ExposeSeatRoutes` is set.
- *Operator writes need a key.* Creating a venue, an event or a seat map requires
  `X-Operator-Key`, checked by the same `SharedSecretEndpointFilter` as the service token
  (014), and a host that maps these routes without a key refuses to start. Catalogue reads
  stay public.
- *Checkout and the hold route are rate-limited per IP.* `X-Client-Id` is claimed, so the
  per-client cap (005) only stops a client that keeps its id; by rotating ids, one client
  could hold a whole venue for five minutes at a time. A token bucket per address (10 a
  second, burst 20) answers `429 rate_limited` with `Retry-After`. Without `UseRateLimiter`
  every policy is silently skipped, so a test fails any host that maps a limited module
  without it. Confirm and cancel aren't limited, since checkout already admitted the order.

It's a floor, and the gaps are named: a botnet has many addresses, every client behind a proxy
shares one, and the load harness turns limiting off. The answer to bots is verified identity
and a waiting room; neither exists, and this doesn't pretend otherwise.

**The OpenAPI document is written by hand** and served with Swagger UI at `/docs/`, because it
promises two things endpoint metadata doesn't carry: the closed vocabulary of `reason` values
each route can answer, with its `retriable` flag, and which host serves which path (014).
Generating it would take a transformer per route, a hand-kept `servers` map, and still a test
against the C# enums: the hand-written document again, one step removed.
`OpenApiDocumentTests` checks it both ways: every mapped route is documented, every documented
path is mapped, and a path carries a `servers` entry exactly when the monolith doesn't map it.
Each status enum is pinned to the C# enum it's rendered from, the one place a shape had
already drifted. **Cost:** about 575 lines of JSON.

---

## 008 — Orders records the expiry; Inventory decides it

An order is `Pending`, then `Confirmed`, `Cancelled`, `Expired` or `Failed`, plus
`AwaitingCapture` and `PaymentDue` (009). `Expired` stays apart from `Cancelled` because
"your hold ran out" and "you changed your mind" are different things to tell a customer.

**`Order.HoldsExpireAt` is copied from Inventory, never computed**, and Orders doesn't know
the number five. Two copies of the expiry rule on two clocks can tell a customer their seat
has gone while it's still theirs, so:
- Orders never refuses a confirm because `HoldsExpireAt` has passed. It asks Inventory, and a
  `HoldExpired` answer moves the order to `Expired`.
- `GET /orders/{id}` returns the stored status and never derives expiry on read.

**An abandoned order is expired by a sweep that asks Inventory first.** A customer who walked
away used to leave the order `Pending` forever, blocking their next checkout for that event,
and if a confirm had authorised before dying, their money stayed held until the gateway gave
up. `OrderExpirySweeper` finds `Pending` orders whose `HoldsExpireAt` passed more than a minute
ago and ends each the way a cancel would (009): release the seats, void the authorisation,
write `Expired`. `HoldsExpireAt` only picks candidates, through a partial index; the grace
keeps the sweep behind any confirm that started in time; and every seat's answer comes from
Inventory, which matters because a confirm's sale commits in Inventory before the order row
records it. Two answers stop the sweep:
- `SoldToYou`: a confirm sold the seats and died before recording it. The sale stands, and
  the sweep finishes that confirm as the customer's next one would (009).
- `LostRace`: the sweep backs off until its next pass, since nobody is waiting.

A confirm of an order the sweep expired answers `holds_expired`, exactly as if it had found
the holds lapsed itself. Unlike the other sweeps it isn't only cleanup: with it off, an
abandoned order stays `Pending`. But no seat invariant depends on it. **Cost:** a seat
re-held through the direct hold route, unseen by Orders, would be released by the sweep, and
that route is off unless a deployment maps it (007). A void that times out still leaves
`Authorized` behind an ended order until the gateway's own expiry; chaos.sh reports that as a
statistic, not an invariant, since its payments-stopped fault produces exactly that case.

**The on-sale gate is the opposite shape, which is why Orders may judge it.** Catalog states
`OnSaleAt` without enforcing it, and a checkout before it is refused `409 not_on_sale` against
Orders' own clock. Skew opens a sale a few seconds early or late, nothing is lost, and no
second authority disagrees. It's a lower bound only, since walk-up sales are real.

**Checks run cheapest first**, and holds, which are writes against the hottest rows in the
system, come last. One `Pending` order per client per event is a partial unique index, and
`orders.orders` carries `xmin` because a confirm and a cancel of one order really do race.

**A partial checkout writes nothing, releases nothing, and names every seat that failed.** The
held seats stay held, and the customer can add a replacement with another checkout, since
re-holding is free. An amendable order would need a mutating route and give confirm and
cancel a third operation to race. Releasing the good seats would cost the customer seats they
won in a flash sale because another was taken.

---

## 009 — Authorise, sell, capture, and a cancel runs it backwards

A confirm authorises the payment, sells the seats, then captures, and any path that doesn't
end with every seat sold voids the authorisation. **The argument is about which resource
can't be recovered.** A sold seat is terminal; money can be given back. So the unrecoverable
step goes in the middle, between two that can be undone.

**Rejected:**
- *Sell, then charge.* Seats sold to someone who then fails to pay are gone for good.
- *Charge once, refund on failure.* Simpler, and common in real ticketing. It loses because
  **a lost authorisation heals itself and a lost charge doesn't**: an uncaptured
  authorisation expires on its own, while a stray charge sits there until someone reconciles
  it.
- *Payment after confirm.* Sold seats held by a customer who has paid nothing.
- *A free path.* Payments refuses to authorise nothing, so Catalog refuses a price that isn't
  above zero (`invalid_price`). A confirm that skipped Payments for a zero total would add a
  second path through the one sequence this repo treats as load-bearing, for a case nothing
  asks for.

**Cost:** two gateway round trips on the hot path instead of one, plus void and capture-retry
paths. **What would change my mind:** a gateway whose captures never fail would make the
second phase dead weight, and if confirm latency became the bottleneck, authorisation would
move to checkout. This is a judgement about what the project is for, where the seat is the
scarce thing, not a fact about payments.

**A decline or a timeout doesn't end the order.** It stays `Pending` with its holds live,
because losing four seats over a mistyped expiry date isn't reasonable, and both answers are
`409` and retriable.

**After the sale, the order is owed its money, and three rules keep it collectable:**
- *The sale is recorded before the capture is requested.* A confirm writes `AwaitingCapture`
  and `SoldAt` as soon as every seat sells: seats sold, funds held, capture not through. It
  isn't `Failed`, because the capture should be retried, not apologised for. Before, a confirm
  that died before capturing left seats sold and money held under an order still reading
  `Pending`, where nothing could find it.
- *`CaptureSweeper` confirms an owed order again*, as a customer would: once a minute, the
  same `ConfirmAsync`, under an advisory-lock lease, adding no rule of its own. It's cleanup
  in 006's sense, since with it off the next confirm still finishes the order. But relying on
  that confirm had a real cost: it had already answered 200, "you have every seat", so nobody
  asked again. One chaos run ended with 80 orders owed their capture and 80 authorisations
  untouched, and when those lapse, the seats have been given away.
- *A refused capture leaves the order `PaymentDue`, never `Failed`*: every seat sold, nothing
  held. `Payment.DeclineCapture` spends the authorisation and frees the order's live slot
  (011), so the customer's next confirm authorises again and captures without selling
  anything twice. The refused confirm answers `payment_due` (409, retriable), not
  `payment_declined`, which would suggest the seats are still only held. Rejected: unselling
  the seats, since `Sold` is terminal (003); a sweep that authorises again, charging a
  customer who didn't ask, which is a merchant's decision; and a new `Payment` status, since
  `Declined` already says what happened to the money. **Cost:** seats can now be sold with no
  money behind them, the state this entry and 010 exist to prevent, so the invariant allows it
  only when the order says `payment_due`. A customer who never confirms again keeps seats
  nobody paid for, and collecting is an operator's job.

**A cancel gives the seats back before the money.** The first version voided first, and that
could leave seats sold with nobody paying: confirm authorises and sells; cancel voids,
releases, finds every seat sold, and writes `Cancelled`; confirm's capture finds no
authorisation. A crashed confirm could reach the same state with no race at all. Now a cancel
releases the seats with `SeatReleaseReason.Cancelled` and touches the money only once they're
back. If any seat answers `SoldToYou`, a confirm of this order already sold it and only the
money is left, so the cancel answers `LostRace` and voids nothing. Otherwise no sale can
follow, and the void is safe. It's the same argument run backwards: each flow keeps its
reversible step on the far side of the sale. `SoldToYou` reads the `HeldByClientId` that
`Sold` keeps (003), and once the sale is recorded, a cancel answers `order_not_pending`,
leaving `lost_race` for the moment in between. Releasing on a cancel doesn't contradict 008,
which forbids releasing because a hold *lapsed*: a customer cancelling isn't a clock.

A cancel between the authorisation and the sale makes the sale refuse, and both flows void;
whichever saves first writes the label, so a cancelled order can read `Failed`. A crashed
confirm can't be cancelled while its seats are sold, and a retried confirm completes it: the
customer has the seats, so the customer pays. `AlreadyCaptured` on the void is kept as a
defensive answer no interleaving reaches. Under load (015), 92 cancels landed after their
confirm's sale and stepped back, and none left seats sold without money.

**Once money has moved, nothing stops the step halfway.** A confirm or cancel honours the
request's cancellation only while loading the order. Every later step is irreversible or
undoes one, so it runs to the end under `CancellationToken.None`, and so do Payments' gateway
call and the save of its answer, and the reconciler's. Before, a client that hung up after
the authorisation left it live, with no void coming. It's the rule 004 applies to releasing a
lock. **Cost:** a confirm keeps working for a client who has gone, for as long as the
gateway's timeouts allow.

---

## 010 — An order's seats sell together or not at all

The first confirm sold an order's seats one at a time, so it could sell some and not the
rest, leaving seats sold and unpaid under a `Failed` order. That wasn't hypothetical: a client
returning after a partial checkout (008) holds seats whose expiries are minutes apart.

**An order's seats sell in one transaction, all or none.** The handler loads every seat in
one query, asks each `Seat` to sell, and writes only if none refused, in one `SaveChanges`, so
every conditional `UPDATE` and outbox row lands together. The order ends `Expired` if every
refusal was an expiry and `Failed` otherwise. Holds and releases are batched the same way but
keep per-seat answers: a refused seat doesn't cost the client the others (008), and a seat
that can't be released doesn't keep the rest held (009).

**"One aggregate per transaction" was considered and not followed.** The rule exists because
aggregates may live in different stores, and because a long transaction over many of them
holds many locks. Neither applies to at most four rows of one table in one round trip, and
each `Seat` still decides its own transition and carries its own `xmin`.

**After a refusal or a double lost race, the seats are reloaded**, because the others already
read `Sold` in memory and a later `SaveChanges` on the same unit of work would write them. The
holds a refused sale leaves behind stay the client's (008). Under load, 1,943 orders, 1,298 of
them multi-seat, ended with none partly sold (015), and a composition test asserts it on every
run (014).

---

## 011 — One live payment attempt per order, and a timeout is settled by asking the gateway

`Payment` is built by `Payment.Create` and moves only through methods that check its state,
like `Seat`, but it stays in the flat module with no ports and no domain assembly. **Factory
construction and the hexagon are separate arguments, and only the second is
Inventory-specific.** `Event`, `Venue` and `Order` have public setters because no rule can be
decided from their own row. A payment's rules can: only an authorised payment may be
captured, captured is terminal, and the amount never moves once the gateway has been asked.
`Payment` shares an assembly with its `DbContext`, so the compiler can't enforce any of this,
and the real guards are in the database.

**One live attempt per order.** `ux_payments_order_live` is a partial unique index on
`order_id` over `Pending`, `Authorized`, `Captured` and `TimedOut`, and it, not the read
before it, is the real guard against a double charge. **`TimedOut` counts as live**, because
"the gateway never answered" really means "possibly holding funds". The filter is built from
`Payment.LiveStatuses`, so changing the list changes the model and needs a migration.

**The row is written before every gateway call.** A crash in between leaves a `Pending` row
holding the slot, and the retry asks again under the same key. Writing afterwards would leave
nothing, and a new key at a gateway that got the first call is a second authorisation. A
confirm that finds a `Pending` attempt resumes it with a write, so `xmin` orders two confirms
and the loser answers from the winner's row, not with a 500.

**A timeout is settled by asking the gateway.** When the gateway doesn't answer an
authorisation, the attempt becomes `TimedOut`, keeping its idempotency key and the order's
live slot. A retry moves the same row back to `Pending`, so the gateway is asked the same
question, not a second one. A *capture* that times out leaves the payment `Authorized`,
because that's still what's true. `PaymentReconciler` asks the gateway what it did, for
attempts `TimedOut` for more than five minutes:

| Gateway record | Meaning | Attempt becomes |
|---|---|---|
| `Authorized` | funds are held | voided at the gateway, then `Voided` |
| `Declined` | received and refused | `Declined` |
| `NotFound` | the gateway looked and has nothing | `Abandoned` |
| `Unknown` | the lookup got no answer | unchanged |

**Everything rests on `NotFound` and `Unknown` being different.** Reading a failed lookup as
"nothing happened" would free the order's slot while funds were still held, and the next
confirm would authorise twice. Found funds are voided, not recorded as `Authorized`, and
nothing is written when the void goes unanswered.

**The reconciler holds a row lock from the lookup to the save.** It used to void the funds
first and then try its `xmin`-checked save, so a confirm retrying the row in between could
win, ask again under the same key, and capture against a void. A `Reconciling` status was the
alternative, adding a state to every switch and to the live index. The lock costs a confirm on
that one row a gateway round trip, then a `lost_race` answer. And since nothing guarantees a
next attempt, a `Pending` attempt older than the reconciler's minimum age is treated as a
crash's leftovers: claimed as `TimedOut` under the row lock, settled like any other, and
counted by the readiness check.

It lives in Payments rather than behind the outbox, because the outbox carries decided facts
and a timeout's whole content is that nobody knows. It's on by default, polls once a minute,
and sleeps even after a full batch, since asking a gateway faster doesn't make it answer.
`payments-api` owns it, and a per-sweep advisory lock is the guard if a second one runs, not
the plan. The first version of that lock passed every test and threw on every sweep in a real
two-process run (`SqlQuery<bool>` wants a column named `Value`); a test now holds the lock
from a second connection.

**The simulated gateway had to become honest first.** A timeout is two events under one name:
`TimeoutRate` decides whether the caller hears back, and `LostRequestRate` whether an
unanswered authorisation arrived at all. With only the first, the "funds are held" branch was
unreachable while its tests passed. Then its memory was a dictionary on a singleton, and after
a restart of the Payments service the next sweep settled **120 of 121** timed-out attempts as
`Abandoned`. Answers now live in `payments.gateway_ledger`, keyed on the idempotency key. It
refuses a share of captures (`CaptureDeclineRate`, 009) but never a void; modelling that
honestly needs a real gateway's error vocabulary.

**No Payments domain events until something needs one.** The order's next confirm reads
whatever the reconciler settled, and a notification would need a message bus between
processes, which is deferred.

---

## 012 — The outbox commits with the seat and delivers late, never wrong

Domain events are written to `inventory.outbox_messages` in the same transaction as the seat
change that raised them, so a sale and its announcement can't disagree.

**The drain is `InventoryDbContext.SaveChanges`, not the repository.** The context commits
everything it tracks, and a four-seat checkout drives four holds through one context; a drain
in the repository would only be complete if every mutation had its own save. Events are
cleared after a successful save, or a four-seat checkout writes ten rows for four holds, and a
rejected save keeps its events for the retry.

**Rows are claimed by `ProcessedAt IS NULL`, never a high-water mark.** Ids are assigned at
insert and transactions commit in any order, so a consumer tracking "the last id I saw" skips
rows silently, the classic outbox bug. A GUID `MessageId` identifies the event.

**Delivery is at least once, in no order a consumer may rely on.** `OutboxDispatcher` claims a
batch with `FOR UPDATE SKIP LOCKED`, so a second instance can drain the same table without
taking the other's rows. A failing message backs off exponentially from two seconds (2, 4, 8,
16 s), and the fifth failure, about 30 s in, drops it out as a dead letter, kept and readable.
The five-minute `MaxBackoff` only binds if `MaxAttempts` is raised. Blocking the queue behind
it would protect an ordering nobody was promised, at the price of one bad row stopping every
good one, so the next row overtakes it, even a sibling from the same save, and `SKIP LOCKED`
can hand one save's rows to two dispatchers. The loop catches everything, because by default
an exception escaping `ExecuteAsync` stops the whole host.

**Late is never wrong.** No seat invariant depends on delivery, and the concurrency suites
never register a dispatcher. Under load with it off, 21,948 events piled up and the sale still
sold exactly 500 of 500 (015).

**Delivery is bounded by the wall clock**, because the claim transaction used to stay open as
long as the slowest consumer, holding fifty rows locked for twenty seconds in one chaos run.
Each handler gets `DeliveryTimeout` (2 s) and each tick `MaxBatchDuration` (5 s), and an
overrun fails that message like any other failure. It's the one place that uses the wall
clock rather than `TimeProvider`, because these deadlines bound real locks, not domain time.
The textbook alternative, delivering outside the claim under a lease, adds the lease state
`SKIP LOCKED` was chosen to avoid.

**What crosses the wire is a versioned contract**, such as `SeatHeldV1` in
`Inventory.Contracts`, so renaming a field inside `Seat` breaks no consumer and no stored row.
The reason enum crosses as a string, so inserting a member can't re-label old rows. **Payloads
are read strictly**: lenient deserialising let a row missing a member reach its handler with
`Guid.Empty` and be marked delivered; now it becomes a visible dead letter. **Cost:** any
member added to a V1 contract needs a default.

**All three events are published, not only the one with a consumer.** In an append-only log,
YAGNI cuts the other way, because history can't be backfilled. It's narrower than it sounds:
an event type with no handler is marked delivered when it's claimed, so a later consumer only
sees new rows. The measured cost was near zero (015), since a refused hold never reaches the
save. **Retention deletes delivered rows only**, after 30 days: an undelivered message is work
however old it is, and a dead letter is evidence.

No MediatR: handlers are registered through `OutboxEventCatalog.Register<T>`, so the compiler
checks that payload and handler agree, and an unregistered name becomes a visible dead letter.
**Notifications exists so there's somewhere to deliver**: one handler, one table, in process,
since moving it out would need a message bus. Idempotency is a unique index on `MessageId`.
The gap between the event's `OccurredAt` and the row's `CreatedAt` gives delivery latency
straight from the database, and a delivery's trace links to the request that raised the event
instead of joining it, since that request ended long before and a redelivery would give it a
second child.

---

## 013 — Modules meet through contracts, and shared code may not name a module

Each module is reached through one seam, an `Add{Module}Module()` / `Map{Module}Module()` pair
the host calls, and the host knows nothing else about any module.

**Modules call each other only through `.Contracts` assemblies**, which depend on nothing.
Orders needs an event's price, but referencing Catalog would put its `DbContext` on Orders'
compile surface, and the first person who needed one more field would write a cross-schema
join. So Catalog publishes `IEventPricing` and an in-process adapter. That isn't the ceremony
001 argues against: the substitution really happens (a fake in tests, a remote call on
extraction), it protects the boundary itself, and it's four files. The interface is named for
the caller's need, so it can't grow into a general one.

**Migrations are explicit by default.** `dotnet ef database update` is the real path, and
`{Module}:MigrateOnStartup`, set only by the run profiles, migrates before Kestrel accepts a
connection. Each module keeps its migration history in its own schema, so extracting one never
means unpicking a shared table.

**Shared code must be inert and name no module.** Five copies of the migrator became one
`ModuleMigrator<TContext>` in `Encore.Modules.Shared.Persistence`. `Encore.Modules.Shared.Http`
holds `ClientIdEndpointFilter`, per-IP rate limiting and `SharedSecretEndpointFilter`, which
hashes both sides before `FixedTimeEquals` so a wrong guess can't learn the secret's length.
The tests hold both projects to the same rules: neither names a module or a contracts
assembly, nothing zero-dependency references them, and no host names them. Each module still
registers its own migrator, filters and rate-limit policy, in its own vocabulary. Sharing a
migration *step* in the host was refused, since it would teach the host every module's
context.

**Readiness is voted by modules, through the framework's health checks.** A module registers
an `IHealthCheck`, and both hosts map `/health` and `/health/ready` through one
`MapEncoreHealthChecks` in `Encore.Telemetry`, restricted to GET and HEAD because
`MapHealthChecks` answers every method. The host counts the votes without knowing which
modules have a database. A hand-rolled readiness endpoint came first and was replaced on 001's
own argument: architecture the framework already ships isn't optionality anyone spends.
`/health` runs no check at all, so a dependency's outage never gets a working process
restarted. An outbox backlog is reported but never fails readiness, because taking a host out
of rotation over one undelivered message would turn a late email into an outage.

---

## 014 — Payments becomes a service, and a seam is not proven by testing each side of it

`Encore.Payments.Api` hosts the Payments module on its own, and Orders reaches it over HTTP
through the same `IOrderPayments` it already called in process. Neither the interface nor
anything inside Payments changed, which is what the modular monolith had claimed from the
start. It was cheap because the seam was keyed by order, idempotency came from the database,
and `TimedOut` was already in the contract. The database didn't move (same Postgres, same
schema), so the risk was a wire format rather than a migration.

**A service surface isn't a customer surface.** The three `/internal/payments/*` routes are
kept apart by four things, not by intent: their own seam (`MapPaymentsServiceApi`), their own
path prefix, their own credential (`X-Service-Token`, compared in fixed time), and no default.
The host refuses to start without a token, because a fallback token is one everybody has. A
shared secret is the floor until Identity or mTLS exists.

**An unreadable answer is a timeout.** A 502, a truncated body, an unknown outcome or a
refused connection all become `TimedOut`, the one status already handled correctly for "the
money may or may not be held". A rejected token throws instead, since configuration won't fix
itself. **No Polly**: a transparent retry turns one ambiguous answer into several without
telling anyone.

**At first the extraction was inert.** Orders registered `HttpOrderPayments` with
`services.Replace(...)`, under a comment claiming that made registration order irrelevant.
`Replace` removes the first *existing* registration, and Orders was registered before
Payments, so there was nothing to remove. Payments then appended its in-process adapter, and
last-wins gave it every payment: the "extracted" configuration was a monolith in two
containers. Both halves' tests were green and would have stayed green forever. The adapter
was tested against a stub, the service's routes on their own, and no test project referenced
both. The fix is `TryAddScoped` in Payments, and `StranglerSwitchTests` resolves
`IOrderPayments` from a composed container in both registration orders, with the switch set
and unset.

**The first chaos run caught it** the same day, before it had measured anything, by stopping
`payments-api` and watching confirms keep succeeding. **A seam isn't proven by testing each
side of it**, so every extraction now ships with a composition test. The strongest
cross-module claim became one too: "no order partly sold, sold without money, or paid without
its seats" had rested on chaos.sh rows that were printed and never compared.
`CheckoutCompositionTests` now composes the real Inventory and Payments modules, races every
confirm against its cancel, and asserts those invariants on every test run, and chaos.sh
exits non-zero when any "must be 0" row isn't.

---

## 015 — Measure first, then break it on purpose

**The load harness came before the outbox**, out of order, because the outbox writes into
every seat transaction and a baseline taken afterwards could never say what it cost. **k6 over
NBomber**, for a build reason rather than taste: NBomber would be a new project and a package
every purity rule would need a carve-out for, while a k6 script sits outside the solution, runs
in its own container, and can only reach the system over HTTP, the one surface a real flash
sale touches.

**The thresholds are invariants, not latencies.** `seats_sold <= SALE_SEATS` checks for
oversell under sustained load, counting distinct seats, since every iteration uses a fresh
client id and never retries. `unexpected_responses == 0` treats a 500 as a fault and a 409 as
the system working. There's no latency threshold: an SLO set before choosing which of the
system's three configurations runs would measure a system nobody had chosen.

**What the outbox cost**, p99 in ms (three runs before it, one each after):

| | hold, contention | purchase | iterations |
|---|---|---|---|
| Before the outbox | 41.1 – 50.2 | 44.2 – 60.5 | 425,299 |
| Drain only | 48.4 | 57.4 | 398,048 |
| Drain and dispatcher | 78.2 | 141.4 | 304,071 |

The drain stays inside the baseline's spread, because only ~15,000 of 304,000 iterations write
at all. **The dispatcher is the whole cost**, as a second workload on the same database, so
don't "optimise" the outbox by weakening the drain's atomicity (012). Most of that cost later
proved fixable: the claim read the whole due backlog every tick, and EF Core logged every
statement. With both fixed, three runs came in inside the pre-outbox spread or below it, apart
from one 86 ms purchase p99.

**The chaos rig** (`load/chaos.sh`) injects one fault per run; k6 asserts, and the script
reads the aftermath from Postgres. A fault measured as a number runs as a healthy window and
a broken window of the same shape, so its cost is read against a same-day control rather than
a mixture of both states.

| Fault | Invariants | What it exposed |
|---|---|---|
| Payments stopped | held; 0 seats sold without an owner | first, that the extraction was inert (014); then, that a stopped container swallows connections, so every confirm waited out the 10 s client timeout |
| Two reconcilers | held; no attempt settled twice | the gateway's memory was per process; a restart settled 120 of 121 attempts `Abandoned` (011) |
| Redis stopped | held; no oversell | a hold cost 85× without Redis, and the healthy control window was full of `too many clients` |
| Dispatcher stalled 20 s | held; request path p99 ~20 ms | delivery max 20.6 s, late but not wrong, and the claim transaction was open the whole time (012) |
| Orders | 1,943 orders; none partly sold, sold unpaid or paid unsold | 92 cancels landed after their confirm's sale and stepped back (009) |

**The Redis cost took four sessions to remove, and the order is the lesson.** Each host had a
connection pool of 100 against a Postgres allowing 97 in total, so the budget is now written
down (`max_connections=300`, pools of 100 and 50). Cutting Redis timeouts from 5 s to 250 ms
took a hold from 85× to 22× and no lower, because each attempt still cost about a second.
Tripling `ConnectTimeout` moved nothing, which ruled it out. `BacklogPolicy.FailFast` removed
it, and a hold's median cost of losing Redis fell to 6%; logging an outage's edges instead of
every refusal took that to zero. Then `CooldownDistributedLock` stopped asking Redis for a
second after it fails to answer: against its own control (`REDIS_LOCK_COOLDOWN=00:00:00`), a
purchase's median cost of losing Redis fell from 1.53–1.73× to 1.42–1.47×, real, since the
ranges don't overlap, but small. **A change is never measured by the session that motivated
it.**

**Measuring again found a regression nothing had announced.** A later run held every
invariant, but with Redis stopped a purchase cost 2.6–4.3× its healthy median, against 1.75×
and 1.87× for older code in the same session, and a contended hold's median had risen from
46 ms to 62–88 ms even with Redis up. Measuring commit by commit pinned it on an earlier
audit's "one read per hold": `GetForHoldAsync` loads the requested seats and the client's live
holds in one `UNION ALL`, and it was compiling the live-hold predicate into a delegate on every
request. The audit's baseline ran 50 VUs, where that cost hid; the Redis fault runs 250. The
read now counts rows instead: `UNION ALL` keeps duplicates, so a requested seat the client
already holds comes back from both halves, and an unrequested live hold only from the second.
A purchase's cost of losing Redis fell back to 1.82× and 1.91×, a contended hold's median to
33–34 ms, below the pre-audit 46 ms, and the baseline went from 329,000–366,000 hold attempts
a run to 730,000–770,000: the compile had slowed the whole hot path. **Rejected:** the
pre-audit pair of queries, a round trip per hold. **Cost:** a `Union` or `Distinct` added
later would break the count silently;
`Hold_WhenAskedForSeatsItAlreadyHolds_ShouldCountOnlyTheLiveOnesOnce` fails if one is.

**The session the README quotes is committed with it**, in `load/evidence/<date>/`: the chaos
report and the three baseline digests, replaced in the same change whenever the README's
numbers change. `load/results/` stays ignored. One machine's runs are worth keeping locally to
compare with the next, but a reader can't tell them from a claim, and committing all 139
files would mostly keep runs nothing cites. An entry's other figures come from the session
that measured it.

**What this isn't:** one laptop, with k6, the system and the telemetry collector sharing a
CPU, mostly one run per configuration. The counts and the mechanisms behind them are strong;
latency comparisons across sessions are not, since the same code's baseline drifted about 17%
between sessions, which is why the README quotes ranges. **There is no deployment**, because a
standing cloud one wouldn't remove that caveat: it would add cost without adding evidence, the
chaos rig stops containers with `docker compose` and couldn't do that to managed services, and
a public one would need the Identity work 007 names. A throwaway run with k6 and the collector
on a second machine was planned and dropped on 2026-09-26: it would have removed a caveat, not
changed a claim, because the invariants hold wherever k6 runs and only the latencies depend on
it.
