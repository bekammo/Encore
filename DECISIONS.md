# Decisions

The choices in Encore worth defending, each with the alternative it beat and what it costs.
Code comments cite entries by number, so a comment ending in `(014)` points here.

If you only read five, read [001](#001--inventory-is-hexagonal-and-everything-else-is-flat)
for the thesis, [005](#005--the-hold-cap-is-a-policy-not-an-invariant) for a rule that is
allowed to fail open, [010](#010--authorise-sell-capture) for why the sale sits between
authorise and capture,
[018](#018--payments-becomes-a-service-and-a-seam-is-not-proven-by-testing-each-side-of-it)
for an extraction that did nothing for four days, and
[032](#032--a-holds-one-read-counts-its-rows-instead-of-compiling-a-predicate) for a
regression found by measuring again.

A decision that changes gets a new entry, and the entry it supersedes says so. The log was
consolidated from an 80-entry working log on 2026-09-23 (`git show 2e5ad70:DECISIONS.md`) and
condensed on 2026-09-26; the unabridged entries are at `git show 8f3159f:DECISIONS.md`.

## Index

- [001](#001--inventory-is-hexagonal-and-everything-else-is-flat) — Inventory is hexagonal, and everything else is flat
- [002](#002--the-domains-purity-is-enforced-by-the-compiler-the-build-and-a-test) — The domain's purity is enforced by the compiler, the build and a test
- [003](#003--the-seat-state-machine) — The seat state machine
- [004](#004--postgres-alone-settles-a-race-for-a-seat) — Postgres alone settles a race for a seat
- [005](#005--the-hold-cap-is-a-policy-not-an-invariant) — The hold cap is a policy, not an invariant
- [006](#006--expiry-is-lazy-and-the-sweep-is-cleanup) — Expiry is lazy, and the sweep is cleanup
- [007](#007--exceptions-inside-the-aggregate-closed-outcomes-above-it) — Exceptions inside the aggregate, closed outcomes above it
- [008](#008--the-http-surface-actions-one-status-rule-and-a-hand-written-contract) — The HTTP surface: actions, one status rule, and a hand-written contract
- [009](#009--orders-records-the-expiry-inventory-decides-it) — Orders records the expiry; Inventory decides it
- [010](#010--authorise-sell-capture) — Authorise, sell, capture
- [011](#011--an-orders-seats-sell-together-or-not-at-all) — An order's seats sell together or not at all
- [012](#012--a-cancel-gives-the-seats-back-before-the-money) — A cancel gives the seats back before the money
- [013](#013--payment-has-a-state-machine-and-one-live-attempt-per-order) — `Payment` has a state machine, and one live attempt per order
- [014](#014--a-timed-out-authorisation-is-reconciled-by-asking-the-gateway) — A timed-out authorisation is reconciled by asking the gateway
- [015](#015--the-outbox-is-drained-by-the-unit-of-work-in-the-seats-transaction) — The outbox is drained by the unit of work, in the seat's transaction
- [016](#016--the-dispatcher-delivers-late-never-wrong) — The dispatcher delivers late, never wrong
- [017](#017--modules-meet-through-contracts-and-the-one-shared-project-may-not-name-a-module) — Modules meet through contracts, and the one shared project may not name a module
- [018](#018--payments-becomes-a-service-and-a-seam-is-not-proven-by-testing-each-side-of-it) — Payments becomes a service, and a seam is not proven by testing each side of it
- [019](#019--measure-first-then-break-it-on-purpose) — Measure first, then break it on purpose
- [020](#020--how-the-suite-runs-and-how-ci-knows-it-ran) — How the suite runs, and how CI knows it ran
- [021](#021--telemetry-follows-the-asymmetry) — Telemetry follows the asymmetry
- [022](#022--once-money-has-moved-nothing-stops-the-step-halfway) — Once money has moved, nothing stops the step halfway
- [023](#023--a-swept-seat-keeps-the-record-of-its-lapsed-hold) — A swept seat keeps the record of its lapsed hold
- [024](#024--the-outbox-promises-delivery-not-order) — The outbox promises delivery, not order
- [025](#025--an-order-owed-its-capture-is-finished-by-a-sweep) — An order owed its capture is finished by a sweep
- [026](#026--why-there-is-no-deployment) — Why there is no deployment
- [027](#027--comments-only-where-they-prevent-a-mistake) — Comments only where they prevent a mistake
- [028](#028--an-event-is-never-free) — An event is never free
- [029](#029--where-the-framework-already-does-the-job-it-does-it) — Where the framework already does the job, it does it
- [030](#030--what-checkout-does-not-guard-is-closed-keyed-or-rate-limited) — What checkout does not guard is closed, keyed or rate-limited
- [031](#031--an-abandoned-order-is-expired-by-a-sweep-that-asks-inventory-first) — An abandoned order is expired by a sweep that asks Inventory first
- [032](#032--a-holds-one-read-counts-its-rows-instead-of-compiling-a-predicate) — A hold's one read counts its rows instead of compiling a predicate
- [033](#033--the-session-the-readme-quotes-is-committed-with-it) — The session the README quotes is committed with it
- [034](#034--a-refused-capture-leaves-the-order-payment_due-never-failed) — A refused capture leaves the order `payment_due`, never `failed`
- [035](#035--the-multi-host-run-is-dropped) — The multi-host run is dropped

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
boundaries. The bill arrives in the tests: `CheckoutService` can only be tested against real
Postgres, because EF Core's in-memory provider doesn't enforce the partial unique index that
stops a client opening two checkouts.

**Money is a `decimal` and a currency code, not a value object**, stored as `numeric(19,4)`.
A `Money` type earns its keep by preventing something, and nothing here does more than sum one
order in one currency; it arrives with tax, fees or a second currency. `Payment` is the one
flat type with guarded transitions, and 013 explains why that doesn't break this rule.

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
- *`Sold` keeps `HeldByClientId`*, so a repeated purchase succeeds (007) and a cancel
  recognises its own confirm (012). `Sold` is terminal.
- *Only the holder may release.* Releasing an `Available` seat or a lapsed hold is a silent
  no-op, and the release path has no opinion about expiry (006).
- *Expiry is exclusive:* `HoldExpiresAt <= utcNow` has expired, pinned by four tests one tick
  apart.

**Refusals throw one `SeatTransitionException` with a closed reason enum**, not a type per
refusal, so callers branch with a `switch` the compiler checks instead of `catch` blocks whose
order matters.

---

## 004 — Postgres alone settles a race for a seat

Postgres is the source of truth for seat state. `RowVersion` maps to the `xmin` system column,
so the database maintains the concurrency token and no code has to. Being a system column,
`xmin` must never appear in a migration's `CreateTable`, and `MigrationConventionTests`
rejects it in every migration.

**There's no per-seat lock.** Every handler used to take a Redis lock per seat and then carry
on whatever it answered, because `xmin` would settle the race anyway. It excluded nobody and
cost two round trips per action. Removing it bought 18% more throughput and raised lost races
from 0.10% to 0.23% of attempts (019), because its round trips had been spreading writers out.
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
the request's cancellation, so a client hanging up can't strand a lock until its TTL. 019
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
(`Inventory:HoldCapLock = Postgres`), and the cap suite runs against both. Over two runs each,
alternated, it cost about 16% of throughput on the monolith baseline, where no client contends
with itself, because each hold takes a second pooled connection to the busiest server. With
Redis stopped it paid off: a purchase cost what it did with Redis up (0.94–0.96× at the
median, against 1.4–1.7× for the Redis lock), and the cap stayed enforced. Under 200
contending VUs it also cut lost races from 4,200–5,500 a run to about 1,300, for the reason
the per-seat lock's removal raised them (004).

**Redis stays the default.** Sale-day throughput is what this system is for, and a Redis
outage is the rare case, already priced. Switching costs one key and a second pool of 100
connections per host.

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

---

## 007 — Exceptions inside the aggregate, closed outcomes above it

`Seat` throws to refuse; the handlers above it catch, translate and return a closed outcome
enum. The layers disagree about what's exceptional. Inside the aggregate an illegal transition
means a rule was about to break, but during a flash sale **losing a seat to somebody else is
the most common outcome there is**, and exceptions on the common path cost throughput when
it's scarcest. Each handler catches exactly the reasons its transition can produce and lets
anything else propagate, because an unknown reason means the aggregate's contract moved.

- **Buying a seat you already bought returns `Sold`**, since a lost response or a
  double-submitted form is asking for a state that already holds.
- **Every seat command carries its event id, checked and never trusted**, or any event's URL
  would buy any seat. A mismatch answers `SeatNotFound`, so nobody can enumerate seats through
  an event they can't see.
- **A repeated seat id is refused, not de-duplicated.** `[A, A, B]` is `400 duplicate_seat`:
  de-duplicating would make the cap judge a different number than the client sent, and return
  fewer lines than were requested.

---

## 008 — The HTTP surface: actions, one status rule, and a hand-written contract

**Actions, not resources.** `POST …/hold`, `…/release`, `…/purchase`, and
`/orders/{id}/confirm` and `/cancel`. A hold isn't an entity (003), and the rules for ending an
order aren't the client's to apply by writing a status. Every action is idempotent, which is
what makes retrying a POST safe.

**One status rule.** Every refusal about the state of the world is a `409` with a `reason`,
`404` is only for the resource the URL names, and a mistake knowable from the request alone,
like a repeated seat id (007), is a `400`. So `hold_cap_reached` is 409, not 403 (nothing is
being authorised) or 429 (it isn't about rate). A `retriable` flag rides alongside. The
mappings are exhaustive switches with no default arm, so a new outcome breaks the build. A
timestamp without a timezone is refused rather than assumed to be UTC, which would open a sale
at the wrong instant.

**`X-Client-Id` is a claimed identity, not authentication.** It stands in for an Identity
module that doesn't exist, so no customer route answers 401 or 403, and it arrives through a
route-group filter the next endpoint can't forget. Payments' routes are read-only for
customers, because **a client that can charge itself has walked around the order flow**.
*030 later closed the direct seat routes and put operator writes behind a key, without waiting
for Identity.*

**Framework responses get the same shape.** `AddProblemDetails` alone still left an unmatched
route's 404, a 405, a 415 and an unbindable body's 400 empty; `UseStatusCodePages` is what
asks for a body.

**The OpenAPI document is written by hand** and served with Swagger UI at `/docs/`, because
the hosts hold no packages of their own (002). *029 gives a better reason.*
`OpenApiDocumentTests` checks it both ways: every mapped route is
documented, and every documented path is mapped. One document covers two hosts (018), so a
path carries a `servers` entry exactly when the monolith doesn't map it. Response shapes
aren't checked, apart from each status enum, which is pinned to the C# enum it's rendered
from: the one place a shape had already drifted.

---

## 009 — Orders records the expiry; Inventory decides it

An order is `Pending`, then `Confirmed`, `Cancelled`, `Expired` or `Failed`, plus
`AwaitingCapture` (010) and `PaymentDue` (034). `Expired` stays apart from `Cancelled` because
"your hold ran out" and "you changed your mind" are different things to tell a customer.

**`Order.HoldsExpireAt` is copied from Inventory, never computed**, and Orders doesn't know
the number five. Three prohibitions follow, the rules most likely to be "fixed" later:
- Orders never refuses a confirm because `HoldsExpireAt` has passed. It asks Inventory, and a
  `HoldExpired` answer moves the order to `Expired`. Two copies of the expiry rule on two
  clocks can tell a customer their seat has gone while it's still theirs.
- `GET /orders/{id}` returns the stored status and never derives expiry on read.
- Orders never releases seats because a hold lapsed. *031 amends this: an expiry sweep now
  asks Inventory to release them.*

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

## 010 — Authorise, sell, capture

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

**Cost:** two gateway round trips on the hot path instead of one, plus void and capture-retry
paths. **What would change my mind:** a gateway whose captures never fail would make the
second phase dead weight, and if confirm latency became the bottleneck, authorisation would
move to checkout. This is a judgement about what the project is for, where the seat is the
scarce thing, not a fact about payments.

**A decline or a timeout doesn't end the order.** It stays `Pending` with its holds live,
because losing four seats over a mistyped expiry date isn't reasonable, and both answers are
`409` and retriable.

**`AwaitingCapture` means seats sold, funds held, capture not through.** It isn't `Failed`,
because an operator should retry the capture, not apologise. *The next confirm was meant to
finish it; 025 added a sweep.*

---

## 011 — An order's seats sell together or not at all

The first confirm sold an order's seats one at a time, so it could sell some and not the
rest, leaving seats sold and unpaid under a `Failed` order. That wasn't hypothetical: a client
returning after a partial checkout (009) holds seats whose expiries are minutes apart.

**An order's seats sell in one transaction, all or none.** The handler loads every seat in
one query, asks each `Seat` to sell, and writes only if none refused, in one `SaveChanges`, so
every conditional `UPDATE` and outbox row lands together. The order ends `Expired` if every
refusal was an expiry and `Failed` otherwise. Holds and releases are batched the same way but
keep per-seat answers: a refused seat doesn't cost the client the others (009), and a seat
that can't be released doesn't keep the rest held (012).

**"One aggregate per transaction" was considered and not followed.** The rule exists because
aggregates may live in different stores, and because a long transaction over many of them
holds many locks. Neither applies to at most four rows of one table in one round trip, and
each `Seat` still decides its own transition and carries its own `xmin`.

**After a refusal or a double lost race, the seats are reloaded**, because the others already
read `Sold` in memory and a later `SaveChanges` on the same unit of work would write them. The
holds a refused sale leaves behind stay the client's (009). Under load, 1,943 orders, 1,298 of
them multi-seat, ended with none partly sold (019), and a composition test asserts it on every
run (026).

---

## 012 — A cancel gives the seats back before the money

A cancel releases the order's seats with `SeatReleaseReason.Cancelled` rather than leave up
to four seats stranded for five minutes. That doesn't contradict 009, which forbids releasing
because *a hold lapsed*: a customer cancelling isn't a clock.

**The first version voided the money first, and that could leave seats sold with nobody
paying.** Confirm authorises and sells; cancel voids, releases, finds every seat sold, and
writes `Cancelled`; confirm's capture finds no authorisation. A crashed confirm could reach
the same state with no race at all.

**Now a cancel gives the seats back first, and touches the money only once they're back.** If
any seat answers `SoldToYou`, a confirm of this order already sold it and only the money is
left, so the cancel answers `LostRace` and voids nothing. Otherwise no sale can follow, and
the void is safe. It's 010's argument run backwards: each flow keeps its reversible step on
the far side of the sale. `SoldToYou` reads the `HeldByClientId` that `Sold` keeps (003), and
`AlreadyCaptured` on the void is kept as a defensive answer no interleaving reaches.

A cancel between the authorisation and the sale makes the sale refuse, and both flows void;
whichever saves first writes the label, so a cancelled order can read `Failed`. A crashed
confirm can't be cancelled while its seats are sold, and a retried confirm completes it: the
customer has the seats, so the customer pays. *Since 025 the sale is recorded on the order,
and a cancel after it answers `order_not_pending`.*

**Cost:** a client that bought seats through Inventory's direct purchase route, around the
order, can't cancel a `Pending` order for them. *030 has since turned that route off by
default.*

Under load (019), 92 cancels landed after their confirm's sale and stepped back, and none left
seats sold without money. The rig fires each racing cancel at a random point inside its
confirm, since one sent at the same instant finishes before the sale.

---

## 013 — `Payment` has a state machine, and one live attempt per order

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
nothing, and a new key at a gateway that got the first call is a second authorisation (022
fences that row).

**No domain events until something needs one.** The order's next confirm reads whatever the
reconciler settled (014), and a notification would need a message bus between processes,
which is deferred.

---

## 014 — A timed-out authorisation is reconciled by asking the gateway

When the gateway doesn't answer an authorisation, the attempt becomes `TimedOut`, keeping its
idempotency key and the order's live slot. A retry moves the same row back to `Pending`, so
the gateway is asked the same question, not a second one. A *capture* that times out leaves
the payment `Authorized`, because that's still what's true.

**`PaymentReconciler` asks the gateway what it did**, for attempts `TimedOut` for more than
five minutes:

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

It lives in Payments rather than behind the outbox, because the outbox carries decided facts
and a timeout's whole content is that nobody knows. It's on by default, polls once a minute,
and sleeps even after a full batch, since asking a gateway faster doesn't make it answer.
`payments-api` owns it, and a per-sweep advisory lock is the guard if a second one runs, not
the plan. The first version of that lock passed every test and threw on every sweep in a real
two-process run (`SqlQuery<bool>` wants a column named `Value`), so neither process settled
anything. A test now holds the lock from a second connection. *022 amends this entry: the
reconciler now holds a row lock from the lookup to the save, and also claims `Pending`
attempts a crash left behind.*

**The simulated gateway had to become honest first.** A timeout is two events under one name:
`TimeoutRate` decides whether the caller hears back, and `LostRequestRate` whether an
unanswered authorisation arrived at all. The first design never recorded a timed-out call, so
the "funds are held" branch was unreachable while its tests passed. Then its memory was a
dictionary on a singleton, and after a restart of the Payments service the next sweep
settled **120 of 121** timed-out attempts as `Abandoned`. Answers now live in
`payments.gateway_ledger`, keyed on the idempotency key. The simulator still never declines a
void; modelling that honestly needs a real gateway's error vocabulary. A refused capture has
been modelled since 034.

---

## 015 — The outbox is drained by the unit of work, in the seat's transaction

Domain events are written to `inventory.outbox_messages` in the same transaction as the seat
change that raised them, so a sale and its announcement can't disagree.

**The drain is `InventoryDbContext.SaveChanges`, not the repository.** The context commits
everything it tracks, and a four-seat checkout drives four holds through one context; a drain
in the repository would only be complete if every mutation had its own save. Events are
cleared after a successful save, or a four-seat checkout writes ten rows for four holds, and a
rejected save keeps its events for the retry.

**Rows are claimed by `ProcessedAt IS NULL`, never a high-water mark.** Ids are assigned at
insert and transactions commit in any order, so a consumer tracking "the last id I saw" skips
rows silently, the classic outbox bug. A GUID `MessageId` identifies the event. *This entry
also promised in-order delivery within one save; 024 withdrew it.*

**All three events are published, not only the one with a consumer.** In an append-only log,
YAGNI cuts the other way: a consumer can be added later, but history can't (024 narrows this).
The measured cost was near zero (019), since a refused hold never reaches the save.

**What crosses the wire is a versioned contract**, such as `SeatHeldV1` in
`Inventory.Contracts`, so renaming a field inside `Seat` breaks no consumer and no stored row.
The reason enum crosses as a string, so inserting a member can't re-label old rows.
**Retention deletes delivered rows only**, after 30 days: an undelivered message is work
however old it is, and a dead letter is evidence.

---

## 016 — The dispatcher delivers late, never wrong

`OutboxDispatcher` claims a batch with `FOR UPDATE SKIP LOCKED`, delivers it, and records the
outcome. `SKIP LOCKED` lets a second instance drain the same table without taking the other's
rows.

**A failing message backs off and lets the queue move past it**, exponentially from two
seconds to five minutes, and after five attempts it drops out as a dead letter, kept and
readable. Blocking the queue behind it would protect an ordering nobody was promised, at the
price of one bad row stopping every good one. The loop catches everything, because an
exception escaping `ExecuteAsync` silently stops a `BackgroundService` for good.

**Late is never wrong.** No seat invariant depends on delivery, and the concurrency suites
never register a dispatcher. Under load with it off, 21,948 events piled up and the sale still
sold exactly 500 of 500 (019).

**Delivery is bounded by the wall clock**, because the claim transaction used to stay open as
long as the slowest consumer, holding fifty rows locked for twenty seconds in one chaos run.
Each handler gets `DeliveryTimeout` (2 s) and each tick `MaxBatchDuration` (5 s), and an
overrun fails that message like any other failure. It's the one place that uses the wall
clock rather than `TimeProvider`, because these deadlines bound real locks, not domain time.
The textbook alternative, delivering outside the claim under a lease, adds the lease state
`SKIP LOCKED` was chosen to avoid.

No MediatR: handlers are registered through `OutboxEventCatalog.Register<T>`, so the compiler
checks that payload and handler agree, and an unregistered name becomes a visible dead letter.
**Notifications exists so there's somewhere to deliver**: one handler, one table. Delivery is
at least once, so idempotency is a unique index on `MessageId`. The gap between the event's
`OccurredAt` and the row's `CreatedAt` gives delivery latency straight from the database,
which is how the chaos rig reads it.

**Readiness is voted by modules**, and the host counts the votes without knowing which
modules have a database (017). A backlog is reported but never fails the check: taking a host
out of rotation over one undelivered message would turn a late email into an outage. *029
moved this onto the framework's health checks.*

---

## 017 — Modules meet through contracts, and the one shared project may not name a module

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
`ModuleMigrator<TContext>` in `Encore.Modules.Shared.Persistence`, under rules the tests
enforce: it names no module or contracts assembly, nothing zero-dependency may reference it,
and no host names it. Each module still registers its own migrator. Sharing a migration *step*
in the host was refused, since it would teach the host every module's context. *029 superseded
two details here: `IReadinessCheck` gave way to the framework's health checks, and
`ClientIdEndpointFilter`, first kept as three copies, is now shared.*

---

## 018 — Payments becomes a service, and a seam is not proven by testing each side of it

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

**For four days the extraction was inert.** Orders registered `HttpOrderPayments` with
`services.Replace(...)`, under a comment claiming that made registration order irrelevant.
`Replace` removes the first *existing* registration, and Orders was registered before
Payments, so there was nothing to remove. Payments then appended its in-process adapter, and
last-wins gave it every payment: the "extracted" configuration was a monolith in two
containers. Both halves' tests were green and would have stayed green forever. The adapter
was tested against a stub, the service's routes on their own, and no test project referenced
both. The fix is `TryAddScoped` in Payments, and `StranglerSwitchTests` resolves
`IOrderPayments` from a composed container in all four registration orders.

**The chaos rig caught it** before it had measured anything, by stopping `payments-api` and
watching confirms keep succeeding. **A seam isn't proven by testing each side of it**, so
every extraction now ships with a composition test.

---

## 019 — Measure first, then break it on purpose

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
don't "optimise" the outbox by weakening the drain's atomicity. Most of that cost later proved
fixable: the claim read the whole due backlog every tick, and EF Core logged every statement.
With both fixed, three runs on 2026-09-24 came in inside the pre-outbox spread or below it,
apart from one 86 ms purchase p99. And with the dispatcher switched off, 21,948 events piled
up undelivered while the sale still sold exactly 500 of 500.

**The chaos rig** (`load/chaos.sh`) injects one fault per run; k6 asserts, and the script
reads the aftermath from Postgres. A fault measured as a number runs as a healthy window and
a broken window of the same shape, so its cost is read against a same-day control rather than
a mixture of both states.

| Fault | Invariants | What it exposed |
|---|---|---|
| Payments stopped | held; 0 seats sold without an owner | first, that the extraction was inert (018); then, that a stopped container swallows connections, so every confirm waited out the 10 s client timeout |
| Two reconcilers | held; no attempt settled twice | the gateway's memory was per process; a restart settled 120 of 121 attempts `Abandoned` (014) |
| Redis stopped | held; no oversell | a hold cost 85× without Redis, and the healthy control window was full of `too many clients` |
| Dispatcher stalled 20 s | held; request path p99 ~20 ms | delivery max 20.6 s, late but not wrong, and the claim transaction was open the whole time (016) |
| Orders | 1,943 orders; none partly sold, sold unpaid or paid unsold | 92 cancels landed after their confirm's sale and stepped back (012) |

**The Redis cost took four sessions to remove, and the order is the lesson.** Each host had a
connection pool of 100 against a Postgres allowing 97 in total, so the budget is now written
down (`max_connections=300`, pools of 100 and 50). Cutting Redis timeouts from 5 s to 250 ms
took a hold from 85× to 22× and no lower, because each attempt still cost about a second. Tripling
`ConnectTimeout` moved nothing, which ruled it out. `BacklogPolicy.FailFast` removed it, and a
hold's median cost of losing Redis fell to 6%. Logging an outage's edges instead of every
refusal took that to zero, but a purchase's only from 2.0× to 1.78×. **A change is never
measured by the session that motivated it.**

**Then a cooldown.** After Redis fails to answer, `CooldownDistributedLock` stops asking it
for a second and answers "unavailable" itself. Against its own control
(`REDIS_LOCK_COOLDOWN=00:00:00`), a purchase's median cost of losing Redis fell from
1.53–1.73× to 1.42–1.47×: real, since the ranges don't overlap, but small. What remains
belongs to losing Redis, not asking it, and is unattributed.

The seat lock's removal was measured the same way: three runs against six, 18% more attempts,
and lost races up from 0.10% to 0.23% (004).

**What this isn't:** one laptop, mostly one run per configuration. The counts and the
mechanisms behind them are strong; latency comparisons across sessions are not.

---

## 020 — How the suite runs, and how CI knows it ran

**The tests run in a container.** On the development machine, Smart App Control blocks every
freshly built unsigned assembly, and the alternatives were signing everything or permanently
disabling a security feature. The container is reversible, free, and identical anywhere with
Docker; Testcontainers run as siblings on the host's daemon. Source is copied into the image,
not bind-mounted, because Windows `bin`/`obj` folders in front of a Linux build fail in ways
that look like code problems. That's also why **the command needs `--build`**: without it, it
tests the last image and reports green.

**CI reads the verdict from summary lines, not the exit code**, because `docker compose run`
exits 0 even when it can't reach the daemon. It counts `Passed!` lines against the test
projects under `tests/`, so it fails when an assembly fails, when nothing ran, and when a
project never reached the runner. Each case was checked by fabricating the log that produces
it.

**Documents drift; the code didn't.** Two audits found the suite green and the prose stale.
Where a document is machine-readable, it now has a test: the OpenAPI document (008) and this
log's index. No test reads English, so the rest is checked by deriving each claim from the
code.

**Postgres is published on host port `55432`**, because a native Postgres on the development
machine owned `5432` while `docker ps` still showed the mapping as Docker's. Container traffic
never touches a published port, so only `dotnet run` and `dotnet ef` failed, with a password
error against a correct connection string.

---

## 021 — Telemetry follows the asymmetry

OpenTelemetry's packages live in **`Encore.Telemetry`**, a host-side project that both hosts
reference and nothing else may. The alternative was lifting the hosts' no-packages rule for
four packages. The architecture tests forbid it to name a module or a contracts assembly, and
forbid any module to reference OpenTelemetry.

**Modules emit through the BCL.** Inventory's `ActivitySource` and `Meter` are
`System.Diagnostics` types, recorded at the adapters' edges, so the Domain and the use cases
never learn they exist. The host subscribes to `Encore.*` by wildcard and never names a
module. **Only Inventory has instruments of its own**; the flat modules get the framework's
spans, for 001's reason.

**An outbox delivery links to the trace that raised it instead of joining it.** That request
ended long before, and a redelivery would give it a second child. A confirm's trace crosses
both processes with no code at all, since HttpClient propagates W3C context. The first live
run buried the requests under one-span traces from every background poll, so the root sampler
now drops client spans that nothing started.

**Off unless `OTEL_EXPORTER_OTLP_ENDPOINT` is set**, so load runs measure the system without
it. Locally, `grafana/otel-lgtm` runs under `--profile telemetry` with a provisioned
dashboard.

**What exporting costs:** with the collector warm and every export checked for arrival, over
two runs each, throughput fell about 25%. On one laptop the SDK and the collector share a CPU,
so this prices the whole arrangement. An earlier, unverified pair disagreed with itself, which
is why arrival is now checked.

**The dashboard caught two things the tests didn't.** Metrics exported every 60 s gave a
one-minute sale a single point, so they now go every 5 s. And the delivery-lag histogram's
default buckets were sized for milliseconds, so every delivery landed in the first and p99
read 5 s. In seconds, deliveries take 0.1–2.5 s: the dispatcher's one-second poll.

---

## 022 — Once money has moved, nothing stops the step halfway

An audit found three ways a payment step could be abandoned between the gateway and the row
that records it, each leaving funds held with nothing to release them. This amends 013 and
014.

**Once money has moved, the request's cancellation is ignored.** A confirm or cancel honours
it only while loading the order. Every later step is irreversible or undoes one, so it runs
to the end. Before, a client that hung up after the authorisation left it live, with no void
coming. It's the rule 004 applies to releasing a lock. **Cost:** a confirm keeps working for a
client who has gone, for as long as the gateway's timeouts allow.

**The reconciler holds a row lock from the lookup to the save.** It used to void the funds
first and then try its `xmin`-checked save, so a confirm retrying the row in between could
win, ask again under the same key, and capture against a void. A `Reconciling` status was the
alternative, adding a state to every switch and to the live index. The lock costs a confirm on
that one row a gateway round trip, then a `lost_race` answer.

**A `Pending` attempt is fenced, and found.** A confirm that finds one resumes it with a
write, so `xmin` orders two confirms and the loser answers from the winner's row, not with a
500. And since nothing guarantees a next attempt, a `Pending` attempt older than the
reconciler's minimum age is treated as a crash's leftovers: claimed as `TimedOut` under the
row lock, settled like any other, and counted by the readiness check.

---

## 023 — A swept seat keeps the record of its lapsed hold

`ExpireHold` used to clear `HeldByClientId` and `HoldExpiresAt`, while `Sell` chose its
refusal from the status, so the sweep changed the answer: before it, the lapsed holder heard
`HoldExpired`; after it, `NoActiveHold`. Orders records `Expired` only when every refusal is
`HoldExpired`, so the same lapsed order ended `Expired` or `Failed` depending on the sweep's
timing. That breaks 006.

Now `ExpireHold` sets only the status, and the lapsed pair stays on the row as a record.
`Sell` reads the pair, so a swept seat answers exactly as an unswept one does. Only a release,
a sale or a new hold changes the pair, and everything that counts holds filters on `Held`.

**Rejected:** making the lazy path forget too, which throws away the one distinction Orders
uses to tell a customer their time ran out. **Cost:** an `Available` row can still name a
client.

---

## 024 — The outbox promises delivery, not order

015 said one save's events arrive in order, and the dispatcher never did that. A failing
message backs off and the next row overtakes it, even a sibling from the same save, and
`SKIP LOCKED` can hand one save's rows to two dispatchers. Keeping the promise would take the
head-of-line blocking 016 rejected, so it's withdrawn: **delivery is at least once, in no
order a consumer may rely on.** Nothing depended on it, since the only consumer handles
`SeatSold` and a sale raises one event.

**Payloads are read strictly.** Lenient deserialising let a row missing a member reach its
handler with `Guid.Empty` and be marked delivered; now it becomes a visible dead letter.
**Cost:** any member added to a V1 contract needs a default. The wire names ship on the
contracts, so a consumer holding only `Inventory.Contracts` reads what Inventory writes.

**"A consumer can be added later" is narrower than 015 made it sound.** An event type with no
handler is marked delivered when it's claimed, so a later consumer only sees new rows. The old
ones sit in the table for the retention window, undelivered.

---

## 025 — An order owed its capture is finished by a sweep

This supersedes 010's "the next confirm resolves `AwaitingCapture`, not a job". 006's test is
that everything stays correct with the timer off, and a capture sweep passes it just as the
reconciler does: with the sweep off, the next confirm still finishes the order. But relying on
that confirm had a real cost. It had already answered 200, "you have every seat", so nobody
asked again. One chaos run ended with 80 orders awaiting capture and 80 authorisations
untouched, and when those lapse at the gateway, the seats have been given away.

**`CaptureSweeper` confirms them again, as a customer would.** Once a minute it calls the same
`ConfirmAsync` for orders owed their capture for over a minute, under an advisory-lock lease
like the reconciler's. It adds no rule of its own.

**The sale is recorded before the capture is requested.** A confirm now writes
`AwaitingCapture` and `SoldAt` as soon as every seat sells. Before, a confirm that died before
capturing left seats sold and money held under an order still reading `Pending`, where nothing
could find it. **Cost:** one more write per confirm, and once the sale is recorded, 012's
cancel gets `order_not_pending`, leaving `lost_race` for the moment in between.

---

## 026 — Why there is no deployment

Every claim here is a load or chaos result with the same caveat: one laptop, with k6, the
system and the telemetry collector sharing a CPU (019, 021). A standing cloud deployment
wouldn't remove that caveat, and it would add cost without adding evidence. The chaos rig
stops containers with `docker compose`, so it couldn't reproduce its faults against managed
services. A public deployment would also expose `/purchase` and seat-map creation to anyone,
and closing them properly is Identity and secrets work that's out of scope (030 has since
closed or keyed both). The plan became a throwaway run instead, with k6 and the collector on a second machine. *035
dropped that too.*

**Notifications stays in process.** Moving it into its own process would need a transport,
and 013 already made cross-process delivery a message bus's job, so it waits for a bus.

**The strongest cross-module claim became a check instead.** "No order partly sold, sold
without money, or paid without its seats" rested on chaos.sh rows that were printed and never
compared. `CheckoutCompositionTests` now composes the real Inventory and Payments modules,
races every confirm against its cancel, and asserts those invariants on every test run, and
chaos.sh exits non-zero when any "must be 0" row isn't.

---

## 027 — Comments only where they prevent a mistake

An audit counted 1,286 `///` blocks outside `Migrations/`. Most repeated the member's name, 94
were `<inheritdoc />` tags no documentation file read, and 44 had gone stale, so the comments
that carry a rule were hard to find. Fewer than 300 remain.

**A comment exists only where a maintainer would otherwise get something wrong**: a
deliberate conflation, an idempotency promise across a boundary, a pinned value, what null
means, a trap, an invariant with its decision number, or why the obvious thing isn't done. A
fact is written once, where it acts or on the contract that promises it. Endpoint summaries
live only in `openapi.json` (008).

**Rejected:** documenting every member. It looks thorough, and it hides the rules among
restatements, where a stale claim goes unnoticed. **Cost:** fewer IDE tooltips, and every new
comment has to meet this bar in review.

---

## 028 — An event is never free

Catalog accepted a price of zero, and Payments refuses to authorise one, since a charge of
nothing isn't a charge (013). So an order for a free event failed at confirm, and in the
two-process setup the failure read as `TimedOut` (018), so the order could never confirm. No
test had priced an event at zero.

**Catalog now refuses a price that isn't above zero** (`invalid_price`, 400). **Rejected:** a
confirm that skips Payments when the total is zero. It would support free events, but it adds
a second path through authorise, sell, capture (010, 022, 025), the one sequence this repo
treats as load-bearing, for a case nothing asks for. **Cost:** a free event would have to be
modelled some other way, starting from here.

---

## 029 — Where the framework already does the job, it does it

A review turned the repository's own argument on it: architecture is paid for only where the
optionality is spent (001), yet the hosts hand-rolled a readiness endpoint ASP.NET Core
already ships, and three modules carried copies of one filter.

**Readiness uses the framework's health checks.** Modules register an `IHealthCheck`, and both
hosts map them through one `MapEncoreHealthChecks` in `Encore.Telemetry`, restricted to GET
and HEAD because `MapHealthChecks` answers every method. This supersedes 017's
`IReadinessCheck` but keeps 016's promise: the host counts votes without knowing which modules
have a database, and a backlog never fails a check. `/health` runs no check at all, so a
dependency's outage never gets a working process restarted.

**`ClientIdEndpointFilter` is shared, in `Encore.Modules.Shared.Http`**, superseding 017's
three copies. It passed 017's own test for sharing, and had been refused only as a web-only
project for one class. The three copies had used three keys so that two on one route couldn't
collide; one filter writes one value from one header, so nothing is left to collide. The
project also holds `SharedSecretEndpointFilter`, which hashes both sides before
`FixedTimeEquals` so a wrong guess can't learn the secret's length, and it follows
Shared.Persistence's rules.

**The OpenAPI document stays hand-written, for a better reason than 008 gave.** 008 borrowed
the Domain's no-packages rule (002). The real reason is that the document promises two things
endpoint metadata doesn't carry: the closed vocabulary of `reason` values each route can
answer, with its `retriable` flag, and which host serves which path (018). Generating it would
take a transformer per route, a hand-kept `servers` map, and still a test against the C#
enums: the hand-written document again, one step removed. **Cost:** about 575 lines of JSON,
kept honest by `OpenApiDocumentTests`.

---

## 030 — What checkout does not guard is closed, keyed or rate-limited

008 and 012 left every route open until an Identity module could restrict it. 026 noticed
what that meant for a deployment: anyone could create seats and buy them with no order and no
money. This supersedes "open until Identity" in three places.

**The seat actions are off by default.** `/hold`, `/release` and `/purchase` sell without a
price, a payment or an on-sale check. They exist so the load harness can measure the hot path
directly, and they're mapped only when `Inventory:ExposeSeatRoutes` is set.

**Operator writes need a key.** Creating a venue, an event or a seat map requires
`X-Operator-Key`, checked by the same `SharedSecretEndpointFilter` as the service token (029),
and a host that maps these routes without a key refuses to start. Catalogue reads stay public.

**Checkout and the hold route are rate-limited per IP.** `X-Client-Id` is claimed, so the
per-client cap (005) only stops a client that keeps its id; by rotating ids, one client could
hold a whole venue for five minutes at a time. A token bucket per address (10 a second, burst
20) stops one machine doing that, answering `429 rate_limited` with `Retry-After`. Without
`UseRateLimiter` every policy is silently skipped, so a test fails any host that maps a
limited module without it. Confirm and cancel aren't limited, since checkout already admitted
the order.

**It's a floor, and the gaps are named.** A botnet has many addresses, every client behind a
proxy shares one (`ForwardedHeaders` isn't configured, and there's no proxy to configure it
for), and the load harness turns limiting off. The answer to bots is verified identity and a
waiting room; neither exists, and this doesn't pretend otherwise.

---

## 031 — An abandoned order is expired by a sweep that asks Inventory first

An order used to leave `Pending` only when its customer confirmed or cancelled. A customer who
walked away left it `Pending` forever, blocking their next checkout for that event, and if a
confirm had authorised before dying, their money stayed held until the gateway gave up.

**An expiry sweep ends it the way a cancel would.** `OrderExpirySweeper` finds `Pending`
orders whose `HoldsExpireAt` passed more than a minute ago and calls `ExpireAsync`, which
follows 012: release the seats, void the authorisation, write `Expired`. Asking Inventory
first matters even more here, because a confirm's sale commits in Inventory before the order
row records it, so only Inventory can fence it. Two answers stop the sweep:
- `SoldToYou`: a confirm sold the seats and died before recording it. The sale stands, and
  the sweep finishes that confirm as the customer's next one would (025).
- `LostRace`: the sweep backs off until its next pass, since nobody is waiting.

A confirm of an order the sweep expired answers `holds_expired`, exactly as if it had found
the holds lapsed itself.

**This supersedes two clauses of 009**: Orders now asks Inventory to release lapsed seats, and
`Expired` gains a second path. 009's reason still holds, because two clocks still can't tell a
customer different things. `HoldsExpireAt` only picks candidates, through a partial index; the
grace keeps the sweep behind any confirm that started in time; and every seat's answer comes
from Inventory.

**Cost:**
- A seat re-held through the direct hold route, unseen by Orders, would be released by the
  sweep. That route is off unless a deployment maps it (030).
- A void that times out still leaves `Authorized` behind an ended order until the gateway's
  own expiry. chaos.sh reports that as a statistic, not an invariant, since its
  payments-stopped fault produces exactly that case.

---

## 032 — A hold's one read counts its rows instead of compiling a predicate

019's rig was run again after 029–031. Every invariant held, and it found a regression none of
those entries caused. With Redis stopped, a purchase cost 2.6–4.3× its healthy median, against
1.75× and 1.87× for older code in the same session, and a contended hold's median had risen
from 46 ms to 62–88 ms even with Redis up. Measuring commit by commit pinned it on an earlier
audit's "one read per hold". `GetForHoldAsync` loads the requested seats and the client's live
holds in one `UNION ALL`, and it was compiling the live-hold predicate into a delegate on every
request to sort the rows in memory. The audit's baseline ran 50 VUs, where that cost hid; the
Redis fault runs 250.

**The read counts rows instead.** `UNION ALL` keeps duplicates, so a requested seat the client
already holds comes back from both halves and an unrequested live hold only from the second;
the count says which rows are live holds. Two runs each, in the same session:
- a purchase's cost of losing Redis fell to 1.82× and 1.91×, and holds won while Redis was
  gone returned to ~72% of the healthy window, as before the audit;
- a contended hold's median fell to 33–34 ms, below the pre-audit 46 ms, so the single read
  now pays for itself;
- the monolith baseline made 730,000–770,000 hold attempts a run, against 329,000–366,000 for
  the unfixed code earlier that day. The compile had slowed the whole hot path, not just the
  fault.

**Rejected:** the pre-audit pair of queries, which costs a round trip per hold. **Cost:** the
answer relies on `UNION ALL`'s duplicates, so a `Union` or `Distinct` added later would break
it silently; `Hold_WhenAskedForSeatsItAlreadyHolds_ShouldCountOnlyTheLiveOnesOnce` fails if
one is.

The same session showed how far numbers drift on one machine: the baseline ran about 17% below
2026-09-24's, for this code and the older code alike. That's 019's caveat, and why the README
quotes ranges.

---

## 033 — The session the README quotes is committed with it

`load/results/` stays ignored. One machine's numbers on one day are worth keeping locally to
compare with the next run, but a reader can't tell them from a claim. The README quotes
numbers, though, and a number with no run behind it asks to be taken on trust.

**The session the README quotes is copied to `load/evidence/<date>/`**: the chaos report and
the three baseline digests, nothing more. When the README's numbers change, the folder is
replaced in the same change. **Rejected:** committing `load/results/` whole, 139 files, most
of them runs nothing cites. **Cost:** one more thing to keep in step with the README.

---

## 034 — A refused capture leaves the order `payment_due`, never `failed`

014 left a gap. The simulator never refused a capture, and a capture that found nothing held
marked the order `Failed`: an order with sold seats, labelled as one that "could not be
completed", with nothing to collect the money. 010 puts the sale in the middle on the promise
that money can be settled afterwards, and a refused capture is where that promise gets
tested.

**The order becomes `PaymentDue`: every seat sold, nothing held.** `Payment.DeclineCapture`
spends the authorisation, which frees the order's live slot, so the customer's next confirm
authorises again under a fresh attempt and captures without selling anything twice. The
confirm whose capture is refused answers `payment_due` (409, retriable), not
`payment_declined`, which would suggest the seats are still only held. The simulator refuses a share of captures
(`CaptureDeclineRate`), and chaos.sh's orders run refuses one in twenty.

**Rejected:**
- Unselling the seats. `Sold` is terminal (003), and the sale is the step 010 says can't be
  undone.
- A sweep that authorises again, charging a customer who didn't ask. That's a merchant's
  decision, not this system's.
- A new `Payment` status. `Declined` already says what happened to the money, and
  `GatewayReference` tells a refused capture from a refused authorisation.

**Cost:** seats can now be sold with no money behind them, the state 011 and 012 exist to
prevent, so the invariant allows it only when the order says so. chaos.sh's "sold, no money"
row excludes `payment_due`, and another row counts those orders. A customer who never
confirms again keeps seats nobody paid for, and collecting is an operator's job. A confirm
that dies between the new authorisation and its capture leaves money held until the
customer's next confirm, because no sweep pays on anyone's behalf.

---

## 035 — The multi-host run is dropped

026 left one item: a throwaway run with k6 and the collector on a second machine. I ruled it
out on 2026-09-26, so it's dropped rather than deferred, and every number stays a one-laptop
number, as 019 and the README say. The run would have removed a caveat, not changed a claim:
the invariants hold wherever k6 runs, and only the latencies depend on it. The alternative was
a cloud machine for an afternoon, which is the deployment 026 kept off the list. Nothing is
left on the roadmap.
