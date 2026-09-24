# Decisions

The choices in Encore that are worth defending, each with the alternative it beat and what
it costs. A decision that changes is superseded by a new entry, never rewritten. Two edits
happen in place: an entry's own open or deferred item is closed where it was raised, and a
statement of fact the code has since overtaken, such as a test's name, is corrected.

This log was consolidated on 2026-09-23 from an 80-entry working log into 001–020; later entries follow them.
Corrections, audits and re-measurements were folded into the decision they concerned, and
the wrong turns worth learning from were kept (018 is the best of them). The full working
log is in git: `git show 2e5ad70:DECISIONS.md`.

## Index

- [001](#001--inventory-is-hexagonal-and-everything-else-is-flat) — Inventory is hexagonal, and everything else is flat
- [002](#002--the-domains-purity-is-enforced-by-the-compiler-the-build-and-a-test) — The domain's purity is enforced by the compiler, the build and a test
- [003](#003--the-seat-state-machine) — The seat state machine
- [004](#004--postgres-settles-a-race-for-a-seat-redis-only-thins-the-crowd) — Postgres settles a race for a seat; Redis only thins the crowd
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
- [020](#020--a-claim-nothing-checks-reads-like-a-claim-that-holds) — A claim nothing checks reads like a claim that holds
- [021](#021--telemetry-follows-the-asymmetry) — Telemetry follows the asymmetry
- [022](#022--once-money-has-moved-nothing-stops-the-step-halfway) — Once money has moved, nothing stops the step halfway
- [023](#023--a-swept-seat-keeps-the-record-of-its-lapsed-hold) — A swept seat keeps the record of its lapsed hold
- [024](#024--the-outbox-promises-delivery-not-order) — The outbox promises delivery, not order
- [025](#025--an-order-owed-its-capture-is-finished-by-a-sweep) — An order owed its capture is finished by a sweep
- [026](#026--what-remains-of-the-roadmap-restated) — What remains of the roadmap, restated
- [027](#027--a-comment-carries-a-reason-never-the-name) — A comment carries a reason, never the name

---

## 001 — Inventory is hexagonal, and everything else is flat

Inventory holds the one genuinely hard problem here: two people clicking the same seat in
the same millisecond must not both get it. The mechanism for preventing that will change —
and did, several times — so ports and adapters buy the ability to change it, and to test the
seat rules against a fake clock in microseconds, without touching the rules. Catalog,
Orders, Payments and Notifications are CRUD over tables nobody contends for. The same
structure there would be three folders and an interface to say "save this row", with no
invariant protected and no substitution ever performed.

**Architecture is a cost you pay for optionality, and you should only pay it where you will
spend the optionality.** That asymmetry is the argument this repository makes, and it is
what I would want to be asked about in a review.

It is applied honestly rather than selectively. `CheckoutService` takes `OrdersDbContext`
concretely and there is no `IOrderRepository`. The interfaces Orders does use —
`IEventPricing`, `ISeatReservations`, `IOrderPayments` — exist because they cross a module
boundary that will one day be a process boundary, which is a different argument that
Orders does not get to borrow for its own storage. The bill arrives in the tests:
`CheckoutService` can only be tested against real Postgres. `EntityFrameworkCore.InMemory`
was rejected because it does not enforce a partial unique index, and would fake away the
most load-bearing line in the module's schema.

**Money is a `decimal` and a currency code, not a value object.** A `Money` type earns its
keep by preventing something — mixing currencies, rounding at the wrong step — and nothing
here does arithmetic beyond summing one order, whose lines share a currency by construction.
The column is `numeric(19,4)`: never a float, which drifts silently, and never Postgres's
`money`, whose scale is a server setting. `Money` arrives when tax, fees or a second
currency make the arithmetic hard, and not before.

Orders and Payments were built as real modules during the first phase, out of order,
because Strangler Fig needs something to strangle (018) and an aggregate with no caller
proves nothing. They were built flat. `Payment` is the one exception to "flat means POCO",
and 013 says why that is not a crack in this rule.

---

## 002 — The domain's purity is enforced by the compiler, the build and a test

`Encore.Modules.Inventory.Domain` is its own assembly rather than a folder. A folder makes
"no infrastructure in the domain" a convention that lasts as long as everyone remembers it.
A separate project makes it a fact: domain code cannot name a type from an assembly it does
not reference. When I say the domain is infrastructure-free, the build is the evidence.

**Three build rules, one file.** `Directory.Build.targets` fails the build with `ENCORE001`
for any `PackageReference`, `ENCORE002` for a `FrameworkReference` other than the implicit
BCL one, and `ENCORE003` for anything outside the BCL in the *resolved* reference closure.
The third is the one that matters: the first two read what a csproj declares, and
infrastructure arriving transitively through a `ProjectReference` passes them cleanly.
`ENCORE003` reads what the compiler is about to be handed. BCL assemblies carry
`NuGetPackageId = Microsoft.NETCore.App.Ref`, every package carries its own id, and a
project's output carries none — so "has a package id that is not the base targeting pack" is
exactly "came from outside the BCL and outside this repo". Verified against the SDK, and
proven by making it fail: `StackExchange.Redis` added to `Encore.Shared` produces `ENCORE003`
on the Domain, naming Redis and its transitive dependencies.

A project opts in with `<EncoreZeroDependency>true</EncoreZeroDependency>`, sitting directly
under the comment that claims the property, rather than being selected by name — a rename
should not silently change what the build checks. `Encore.Shared` and the three
`.Contracts` assemblies opt in too. `Encore.Shared` matters most: it is the Domain's only
project reference, so anything reaching it reaches the Domain. `Encore.Api` takes
`ENCORE001` alone; a host composes modules, so EF Core arriving transitively is its job.

**The architecture tests use no architecture-test library.** Nearly every assertion is an
assembly-reference question `GetReferencedAssemblies()` answers in a line, and a fluent DSL
for that would be the pattern the problem does not justify. Two mechanisms, because they
answer different questions: `AssemblyReferenceTests` reads compiled metadata, which cannot
see a declared-but-unused `ProjectReference`; `ProjectGraphTests` parses the csprojs, which
can. Module names are compared exactly, never by prefix — `Inventory.Contracts` starts with
`Inventory`, and a prefix match would ban the seam the rule exists to permit.

**A port never names an adapter's type**, including in an XML doc comment. `ISeatRepository`
once documented that it throws EF Core's `DbUpdateConcurrencyException`, which coupled every
caller that handles a lost race to EF Core through the one interface whose job is to hide
it. It now throws `ConcurrentSeatModificationException`, a Domain type, and `EfSeatRepository`
translates, keeping the original as the inner exception. No project graph can catch a leak
that travels through a type *name*.

**The cost.** The build now depends on SDK item metadata, so an SDK that stopped populating
`NuGetPackageId` would break it — loudly, on the first build, which is the right direction.

---

## 003 — The seat state machine

`Seat` is the aggregate root and the only consistency boundary. A hold is not an entity: it
is the `HeldByClientId` / `HoldExpiresAt` pair on the seat row. Every rule about when a seat
changes hands lives in the transitions, and nothing above the aggregate may re-decide one.
The tests for these were written before the transitions.

**Construction is by factory only.** `Seat.Create(id, eventId)`, a private constructor, and
a private parameterless one for EF Core. A public constructor with settable properties is a
hole straight through the rules — `new Seat { Status = Sold }` reaches a state nothing
approved. `Create` also refuses `Guid.Empty` for either id, with an `ArgumentException`
rather than a transition refusal, because no HTTP request can produce it.

**The aggregate owns the duration.** `Hold(clientId, utcNow)` takes the current instant and
never an expiry; a caller-supplied expiry would let anyone hold a seat until the year 3000.
Holds last five minutes. `utcNow` must be UTC — `Local` and `Unspecified` are both refused,
because a wall-clock time with no zone is a different instant in London and Los Angeles.
The check sits in the aggregate because it is a precondition of these methods, and every
`OccurredAt` is UTC by construction as a result.

**The rules, and the reason for each one a reviewer would push back on:**

- *Re-holding a seat you hold is an idempotent no-op, and the expiry does not move.* A retry
  must not tell a client their own seat is taken, and a hold must not be extendable forever
  by repeating the request.
- *Reclaiming a lapsed hold raises `SeatReleased(Expired)` before `SeatHeld`.* One event
  would leave the old claim looking live forever. Event history cannot be backfilled, which
  is also why `SeatReleased` carried a `Reason` from the start. A client reclaiming their
  *own* lapsed hold reclaims it too: by then the seat was available to anyone.
- *There is no route from `Available` to `Sold`.* `Sell` needs a live hold by the same
  client. A sale that skipped the hold is a double-sell `xmin` cannot catch, because the two
  writers never contend on the same row version.
- *`Sold` keeps `HeldByClientId`.* Nulling it would throw away who owns the seat, which is
  what lets a repeated purchase succeed (007) and a cancel recognise its own confirm (012).
  `Sold` is terminal.
- *Only the holder may release.* Releasing an `Available` seat, or a lapsed hold, is a no-op
  success with no event, and the lapsed row is deliberately left as it is — tidying it would
  give the release path an opinion about expiry (006).
- *Expiry is exclusive:* `HoldExpiresAt <= utcNow` has expired. Four tests sit one tick
  apart; flipping the comparison would otherwise break nothing.

**Refusals throw one `SeatTransitionException` carrying a closed reason enum**, not a type
per refusal. Callers branch on *why*, and a closed enum makes that a `switch` the compiler
checks, where an exception hierarchy makes it `catch` blocks whose order matters.

`ExpireHold(utcNow)` is a fourth method but not a fourth rule; 006 explains it.

---

## 004 — Postgres settles a race for a seat; Redis only thins the crowd

Postgres is the source of truth for seat state, and optimistic concurrency is what makes a
seat write atomic. `RowVersion` maps to the `xmin` system column, so the database maintains
the token and no code has to remember to. Because `xmin` is a system column it must never
appear in a migration's `CreateTable` — the provider happens to strip it today, but a
hand-written script or a different provider turns it into an error, so
`MigrationConventionTests` refuses the line in every migration.

**A lost race is retried exactly once.** Losing means someone else wrote the row first, so
reloading and re-asking turns a bare "you lost a race" into the accurate "somebody has it".
Retrying harder when the system is busiest is how a thundering herd gets worse. The retry
only means something with a genuinely fresh read: EF Core's identity map hands back the
tracked instance with its stale token, so `EfSeatRepository` detaches a tracked seat and
reads it again — otherwise the retry re-attempts exactly the state that just lost. Domain
events are cleared before each attempt, so a rejected attempt cannot publish a hold that never
happened.

**The lock can say "I don't know".** `IDistributedLock.TryAcquireAsync` returns a
`LockAcquisition`: `Acquired`, `HeldByAnother` or `Unavailable`. The first version returned
a token or `null`, which could not tell "someone has it" from "Redis did not answer", and
that cost both correctness (005) and availability — an unhandled Redis exception took every
hold down while three documents claimed correctness survived Redis. The adapter translates
`RedisConnectionException` and `RedisTimeoutException` and nothing wider, so a malformed Lua
script is not filed as "unavailable". It also had to be built with `AbortOnConnectFail =
false`: with Redis absent, `Connect` threw inside the DI factory, before the adapter existed
to catch anything. **A component's own construction is part of its failure surface.**

**Releases ignore the request's cancellation.** A client hanging up used to cancel the
release in the `finally` and strand the lock until its TTL. The release helper now takes no
`CancellationToken` at all, so the next caller cannot pass the wrong one.

**There is no per-seat lock.** Every handler used to take one and then proceed whatever it
answered — because `xmin` would settle the race anyway — so it excluded nobody and cost two
Redis round trips per hold, sale and release. It was removed. Measured (019): 18% more
throughput, and lost races up from 0.10% to 0.23% of attempts. The lock never excluded
anyone, but its round trips spread writers out; removing it gives that delay back as races
`xmin` settles with a retriable 409. No invariant moved. That trade is taken, and the lock
should not be restored to buy it back. The client lock (005) is the one lock with a job no
row's token can do.

Correctness must survive Redis being gone entirely, and 019 shows it doing so under load.

---

## 005 — The hold cap is a policy, not an invariant

A client may hold at most four seats per event. That rule spans four rows and `Seat` is a
one-row boundary, so it lives in the Application layer: a Postgres read of the client's live
holds (`Held` and not yet expired), serialised by a Redis lock on client + event. Counting in
Postgres means the count is automatically right about expiry, with no counter to drift.

**The first version enforced nothing, and a test found it.** One client, twelve seats, twelve
simultaneous requests, cap of four: **twelve holds**. The lock does not wait, so one request
took it and eleven got `null`, and the handler proceeded on `null` — right for a lock with a
concurrency token behind it, catastrophic for one with nothing. The failure hid itself: the
lock is only contended when one client has several requests in flight, which is exactly what
the cap exists to catch. With the three-valued lock (004) the policy is now explicit:

| Client + event lock | Answer |
|---|---|
| `HeldByAnother` | refuse with `ConcurrentRequestInFlight`, retriable |
| `Unavailable` | proceed, and accept that the cap may be breached |

**That second row is the decision.** If Redis is down, two simultaneous holds can both pass
the count and a client can take a fifth seat. A cap breach is a refund email; an oversell is
a customer outside a sold-out venue. Refusing every hold in the system because Redis blinked
would turn the first into an outage to prevent the lesser harm.

**The cost.** A burst of twelve now yields as few as one hold, and a client that retries
converges on four. Both integration tests pin it, because "no more than four" is also
satisfied by granting one.

The cap counts concurrent *holds*, not purchases: sold seats are no longer held. Re-holding a
seat you already hold is free, so the port returns the ids of live holds rather than a count —
a count could not tell a re-hold from a fifth seat.

**The limit is published** as `SeatReservationLimits.MaxHoldsPerClientPerEvent`, so Orders can
size a checkout instead of sending five holds and compensating four. The test for a value on a
contract: a caller needs it *before* acting, and it is already observable afterwards — every
`hold_cap_reached` response ships it. It is `static readonly`, not `const`, because a `const`
is copied into the consumer at compile time and would go stale silently once the call crosses
a process.

**Measured against a Postgres advisory lock.** `PostgresAdvisoryLock` is the same port on
`pg_try_advisory_lock`, selected with `Inventory:HoldCapLock = Postgres`. It ran twice per
configuration, runs alternated, and the cap suite runs against both locks.

- **It costs where the lock does nothing.** On the monolith baseline no client ever contends
  with itself. There throughput fell about 16% (353k and 319k iterations, against 426k and
  376k), and a purchase's median rose from 21–26 ms to 28–33 ms. Each hold takes a second
  pooled connection and a round trip to the busiest server.
- **It pays where Redis fails.** With Redis stopped, a purchase costs what it did with Redis up
  (0.94× and 0.96× at the median, against 1.4×–1.7× for the Redis lock). Holds keep being won
  at the same rate, and the cap stays enforced rather than best-effort.
- **Under 200 contending VUs it also cut lost races**, from 4,200–5,500 a run to about 1,300.
  The reason is the one that made the per-seat lock's removal raise them (004): a round trip
  spreads writers out.

**Redis stays the default.** Sale-day throughput is what this system is for, and a Redis outage
is the rare case already priced above. The advisory lock stays behind the switch for the day
the cap has to hold through an outage. Switching costs one key and a second pool of 100
connections per host.

---

## 006 — Expiry is lazy, and the sweep is cleanup

A seat reading `Held` whose `HoldExpiresAt` has passed is `Available` on every read and write
path, whatever the column says. A timer that is load-bearing for an invariant is an invariant
that fails whenever the timer is late, so the background sweep exists only to tidy the table,
and the test of the design is concrete: **if a test cannot pass with the sweep disabled, the
sweep has become load-bearing and the design is broken.**

`ExpiryWithoutTheSweepTests` is that test: no sweeper registered and no Redis, so whatever
passes rests on the aggregate and `xmin`. A lapsed hold is reclaimed by the next client; the
lapsed holder cannot sell; a passer-by cannot sell it either; thirty clients reclaiming one
lapsed seat produce one winner; and the per-client cap reopens as holds lapse. The last is the
subtle one, because the cap is a Postgres count, which carries a second copy of the expiry
rule. Had it said only `Status = Held`, a client would stay capped until a job ran.
`SWEEP_ENABLED=false` in compose asks the same question under load.

**The sweep goes through the aggregate, not a bulk `UPDATE`.** One statement would be cheaper
and would publish nothing — so hold history would be reconstructable only for seats that were
contended, and the unpopular seats and abandoned baskets would vanish from it. `Seat` got
`ExpireHold(utcNow)`: the lapsed-hold ending `Hold` already performed inline, given a name so
something other than a new holder can trigger it. `Hold` delegates to it, so exactly one place
knows what a lapsed hold's ending looks like. It returns `bool` instead of throwing, alone
among the transitions, because "nothing to tidy" is an ordinary answer.

**The sweep decides nothing.** Its candidate query is SQL, a second expression of the expiry
rule, and it is defused by having no authority: it returns ids, and the aggregate re-decides
each one. A seat re-held since the query ran is refused, and nothing is written.

**No lease.** Two sweeps picking the same seat both load it and both save, and `xmin` lets one
through; the loser's outbox row was in the transaction that rolled back. A scope per seat, so
one lost race does not roll back its neighbours. It loops on a full batch, because every
visit settles its row, and otherwise runs once a minute: an unswept row is not news to anyone.

**Its index is partial.** `ix_seats_expiring_holds` covers held rows only, so it stays a few
thousand rows whatever the table's size. The filter names the stored enum value as a literal,
and `MigrationConventionTests` reads `SeatStatus.Held` from source and checks the migration
agrees — a renumbered enum would otherwise leave a sweep that quietly scans. It was briefly
suspected of a baseline regression and cleared by three runs with it and three without (019).

---

## 007 — Exceptions inside the aggregate, closed outcomes above it

`Seat` throws to refuse. The handlers above it catch, translate and return a result carrying
a closed outcome enum. The two layers disagree about what is exceptional: inside the
aggregate an illegal transition means a rule was about to break, but at the use-case boundary
during a flash sale **losing a seat to somebody else is the most common outcome there is.**
Exceptions on the common path cost throughput when it is scarcest and push callers into
control flow made of `catch` blocks. An enum lets an endpoint switch over every case with the
compiler checking none was forgotten.

**Unhandled reasons propagate.** Each handler catches exactly the reasons its transition can
produce. A reason it does not know means the aggregate's contract moved without the handler
being told, and a catch-all response would hide that until it mattered.

**Buying a seat you already bought returns `Sold`, not a refusal.** A lost response or a
double-submitted form is asking for a state that already holds, and telling a customer their
own completed purchase failed would be untrue and alarming. No second `SeatSold` is raised.

**Every seat command carries its event id, checked and never trusted.** Once the routes are
`/events/{eventId}/seats/{seatId}/…`, a command without it would leave the URL decorative —
any event's URL would buy any seat. A mismatch answers `SeatNotFound`, deliberately
indistinguishable from "no such seat", so nobody can enumerate seat ids through an event they
cannot see.

**A repeated seat id is refused, not de-duplicated.** `[A, A, B]` is `400 duplicate_seat`.
De-duplicating would make the cap check judge a different number than the client sent, and
return an order with fewer lines than the request had ids, with nothing saying why. It is
judged before the cap so the response names the real mistake. It is 400 rather than 409
because it is knowable from the request alone — the same line that separates `too_many_seats`
from `hold_cap_reached`.

---

## 008 — The HTTP surface: actions, one status rule, and a hand-written contract

**Actions, not resources.** `POST …/hold`, `…/release`, `…/purchase`, and
`/orders/{id}/confirm` and `/cancel`. A hold is not an entity (003), and minting a `/holds`
collection would contradict that at the front door. Confirm and cancel are actions rather
than a writable status, because the rules for reaching each ending are not the client's to
apply. All of these are idempotent, which is what makes retrying a POST safe.

**One status rule: the code carries the class of failure, and a `reason` says which one.**
Every refusal about the state of the world is `409`; `404` is only for the resource the URL
addressed. So a body naming a venue that does not exist is `409 venue_not_found`, and
`hold_cap_reached` is 409 rather than 403 (nothing is being authorised) or 429 (it is not
about rate). A `retriable` flag rides alongside, so a client does not keep its own list of
which refusals are worth another attempt. The mappings live in `SeatResults`, `OrderResults`
and friends, every switch exhaustive with no default arm, so adding an outcome breaks the
build.

**An instant with no timezone is refused, not guessed**: `400 ambiguous_timestamp`. Assuming
UTC would put a show on sale at the wrong instant, silently, until the day.

**`X-Client-Id` is a claimed identity, not authentication.** It stands in for an Identity
module that does not exist, which is why no route answers 401 or 403. It arrives through a
route-group filter rather than a bound parameter, because a failed bind yields an empty 400
nobody can diagnose, and a group filter cannot be forgotten by the next endpoint. Payments'
routes are read-only for customers — **a client that can charge itself has walked around the
order flow entirely** — and someone else's payment is `404`, not `403`. Catalog lives under
`/catalog` rather than sharing `/events` with Inventory, so the module seam is visible in the
URL and neither module has to redesign routes to leave.

**Framework responses get the same shape.** `AddProblemDetails` alone changes nothing: a 404
for an unmatched route, a 405, a 415 or a 400 for an unbindable body all came back as empty
bodies, measured on the running host. `UseStatusCodePages` is the line that asks for a body.
Those responses carry no `reason`, because there is no closed vocabulary for "malformed" and
inventing one would be a promise no handler makes.

**The OpenAPI document is written by hand**, served with Swagger UI at `/docs/` from the
monolith. Swashbuckle and `Microsoft.AspNetCore.OpenApi` are packages, and the host holds none
of its own (002). The price is that nothing generates the document, so `OpenApiDocumentTests`
checks it: every mapped route must be documented and every documented path mapped, in both
directions, and a fourth test counts `Map*` calls against the ones it could read, so a
registration in an unreadable shape is a red test rather than a hole. One document covers two
hosts (018), so **a path carries a `servers` entry exactly when the monolith does not map
it**, and the test walks the call graph from each host's `Program.cs` to check that too.
Response shapes remain unchecked, except each status enum, which a test pins to the C# enum it
is rendered from: the one place a shape had already drifted.

---

## 009 — Orders records the expiry; Inventory decides it

An order is `Pending`, then `Confirmed`, `Cancelled`, `Expired` or `Failed`, plus the
non-terminal `AwaitingCapture` that 010 added. `Expired` stays apart from `Cancelled` for the
reason `SeatReleased` carries a reason: "your hold ran out" and "you changed your mind" are
different things to tell a customer, and history cannot be backfilled.

**`Order.HoldsExpireAt` is copied from Inventory, never computed** — the earliest expiry across
the order's holds, because the first to lapse is when the order stops being completable.
Orders does not know the number five. Three prohibitions follow, and they are the rules most
likely to be "fixed" later:

- Orders never refuses a confirm because `HoldsExpireAt` has passed. It asks Inventory, and
  `HoldExpired` is what moves the order to `Expired`. Two copies of the expiry rule on two
  clocks can tell a customer their seat has gone while it is still theirs.
- `GET /orders/{id}` returns the stored status and never derives expiry on read.
- Orders never releases seats because a hold lapsed. Inventory reclaims lapsed holds itself.

`Confirm_WhenHoldsLapsed_ShouldAskInventoryRatherThanItsOwnClock` pins it: a clock far past
`HoldsExpireAt`, an Inventory that says `Sold`, and an order that confirms.

**The on-sale gate is the opposite shape, and that is why Orders may judge it.** A checkout
before `OnSaleAt` is refused `409 not_on_sale`, against Orders' clock. Skew there opens a sale
a few seconds early or late, nothing is lost, and no second authority disagrees — Catalog
states the instant and does not enforce it. It is a lower bound only: walk-up sales are real.
It binds `/orders` only. Inventory's own hold route does not know the on-sale time, and a hold
taken early carries into a later checkout, since re-holding your own live hold is a no-op.

**Orders sends the holds, and the checks are ordered by cost.** A checkout validates the
request, asks Catalog, checks for an open checkout, and takes holds last — holds are writes
against the hottest rows in the system, and taking four before discovering a typo would pay
the highest price for it. One `Pending` order per client per event is a partial unique index.
`orders.orders` carries `xmin`, because a confirm and a cancel of one order really do race.

**A partial checkout writes nothing, releases nothing, and names every seat that failed.** The
seats that were held stay held, and the customer can buy those or add a replacement with
another `POST /orders`; re-holding is free and does not move the expiry. The alternative — a
partial order that can be amended — needs a mutating route, makes the order's snapshotted
total false, and gives confirm and cancel a third operation to race. Releasing the good seats
would cost the customer two seats they got during a flash sale because a third was taken.
Abandoned holds lapse on their own. The cost is that the client remembers its basket, which
is what clients do.

---

## 010 — Authorise, sell, capture

A confirm secures the money, sells the seats, then takes the money, and any path that does not
end in every seat sold voids the authorisation. **The argument is about which resource cannot
be recovered.** A sold seat is terminal; money can be given back. So the unrecoverable step
goes in the middle, between two that can be undone.

**What was rejected.** *Sell, then charge*: seats sold to someone who then fails to pay are
gone for good. *One charge before selling, refunded on failure*: simpler, and common in real
ticketing — it loses because **a lost authorisation heals itself and a lost charge does not**.
When the gateway times out, an uncaptured authorisation expires on its own, while a stray
charge sits there until someone reconciles it. *Payment as a step after confirm*: sold seats
held by a customer who has paid nothing.

**The cost.** Two gateway round trips on the hot path instead of one, a void path and a
capture-retry path. **What would change my mind:** a real gateway whose captures never fail
would make the second phase dead weight, and if confirm latency became the bottleneck, the
authorisation would move to checkout. This is a judgement about what the project is for — the
seat is the scarce thing — not a fact about payments.

**A decline does not end the order**, and neither does a gateway timeout. The order stays
`Pending` with its holds live, because losing four seats over a mistyped expiry date is not
reasonable. Both come back `409` with `retriable: true`, which here means "try again with
something different".

**`AwaitingCapture`: seats sold, funds held, capture not through.** Not `Failed`, because an
operator looking at it should retry a capture, not apologise. It is resolved by the next
confirm, which retries only the capture, rather than by a background job — a capture-retry
sweep would be load-bearing in the way 006 forbids. The cost is that an untouched order can sit
there, holding money we are owed and have not taken. `Pending` is pinned to zero because the
one-open-checkout index filters on the literal `"Status" = 0`; `AwaitingCapture` was appended
as 5.

Since 011 no confirm ends with *some* seats sold, and since 012 no cancel can void the money
behind a sale that happened.

---

## 011 — An order's seats sell together or not at all

The first confirm sold an order's seats one at a time, so it could sell some and not the rest:
those seats were terminal, the order `Failed`, the authorisation voided, and nobody could buy
seats nobody had paid for. That is not hypothetical — a client that comes back after a partial
checkout (009) holds seats whose expiries are minutes apart, and a confirm between the first
lapse and the last sold the late ones.

**An order's seats are sold in one transaction, all or none.** The sell handler loads every
seat in one query, asks each `Seat` to sell, and writes only if none refused — one
`SaveChanges`, so every conditional `UPDATE` and every outbox row land together or not at all.
A refusal names each seat and why. The order ends `Expired` if every refusal was an expiry and
`Failed` otherwise, and `Failed` no longer means "partly sold".

**Holds and releases are batched too, and keep their per-seat answers.** A refused seat still
does not cost the client the seats that could be held (009), and a seat that cannot be released
does not keep the others held (012). Only the write is shared. `ISeatReservations` takes an
order's seats in one call for all three, and the single-seat HTTP routes are a batch of one.

**"One aggregate per transaction" was considered and deliberately not followed.** The rule
exists because aggregates may live in different stores, and because a transaction over many
of them holds many locks for long. Neither applies: at most four rows of one table, written in
one round trip. And the transaction enforces nothing — each `Seat` still decides its own
transition and carries its own `xmin`. It adds atomicity, not an invariant. Creating a seat
map draws the same line: the aggregate is per seat, the use case is bulk, and half a seat map
is a broken venue rather than a smaller one.

**A refused sale reloads its seats before returning.** By the time the lapsed seat refuses,
the others already read `Sold` in memory, and any later `SaveChanges` on the same unit of work
would sell them. `SeatBatchTests` pins it by saving after a refusal and checking nothing sold.

The holds a refused sale leaves behind stay the client's, for 009's reason. Under load, 1,943
orders — 1,298 of them multi-seat — ended with none partly sold (019).

A sale that loses its race twice reloads too: the retry's load discards the first loss, and
nothing discarded the second, so a later save on the same unit of work tried to write stale
`Sold` seats. `xmin` refused that write, but the refusal landed on an unrelated caller.

---

## 012 — A cancel gives the seats back before the money

Cancelling releases the order's seats with `SeatReleaseReason.Cancelled`, a member that had
existed since the first domain events and had no producer. Leaving up to four seats to lapse on
their own after every cancellation strands the scarcest resource for five minutes at a time.
This does not contradict 009: that forbids releasing because *a hold lapsed*, which would be a
second clock judging expiry. A customer cancelling is not a clock judgement.

**The first version voided the money first, and that left seats sold with nobody paying.**
Confirm authorises and sells; cancel voids (nothing captured yet, so the guard passes); cancel
releases, every seat answers "sold", and it writes `Cancelled`; confirm's capture finds no
authorisation. End state: seats sold, money released, and the sale announced. A crashed
confirm reached the same state with no race at all.

**Now a cancel releases the seats first and touches the money only once they are back.** If any
seat answers `SoldToYou`, a confirm of this order has already sold it; money is the only step
left, so the cancel answers `LostRace` and voids nothing. If none does, the holds a sale needs
are gone, no sale can follow, and the void is safe. This is 010's argument run backwards: the
sale is the step that cannot be undone, so each flow keeps its reversible step on the far side
of it. `SoldToYou` is a new answer rather than a new rule — `Release` still refuses every sold
seat, and the reason is simply more specific, read from the `HeldByClientId` that `Sold` keeps.

**The other interleavings.** A cancel between the authorisation and the sale gives the seats
back, the sale refuses, and both flows void — though whichever saves the order first writes
the label, so a cancelled order can read `Failed`. A cancel after the sale loses, and the
confirm captures. A crashed confirm cannot be cancelled while its seats are sold, and a retried
confirm completes it: the customer has the seats, so the customer pays. `AlreadyCaptured` on
the void is kept as a defensive answer no interleaving of this module reaches.

**The cost.** A client that bought the seats through Inventory's own purchase route, around
the order, cannot cancel a `Pending` order for them — and a confirm then charges for seats they
do own, which is what 008's read-only Payments would say anyway.

Under load (019), 92 cancels landed after their confirm's sale and stepped back, and none left
seats sold without money. A cancel fired at the same instant as the confirm never reaches that
window, because it finishes before the sale; the rig delays it on purpose.

---

## 013 — `Payment` has a state machine, and one live attempt per order

`Payment` is built by `Payment.Create` and moves only through methods that check its state —
`Seat`'s shape — while staying in the flat module with no ports, no adapters and no domain
assembly. **Factory construction and the hexagon are separate arguments, and only the second
is Inventory-specific.** Taking the first without the second is the consistent reading of both.

`Event`, `Venue` and `Order` have public setters because they have no rule decidable from their
own row. A payment does: only an authorised payment may be captured, captured is terminal, and
the amount never moves after the gateway was asked. The transitions refuse non-UTC instants
and `Create` refuses empty ids, with the guard ahead of every idempotent early return, so a bad
clock is reported every time rather than depending on state the caller cannot see.

**What it costs.** No compiler enforcement: `Payment` shares an assembly with its `DbContext`.
Acceptable, because the real guards are in the database — the index below, and `xmin`, which
stops a confirm and a cancel writing different answers to the same row.

**One live attempt per order.** `ux_payments_order_live` is a partial unique index on
`order_id` over `Pending`, `Authorized`, `Captured` and `TimedOut`, and it — not the read that
precedes it — is the real guard against a double charge. `Declined`, `Voided` and `Abandoned`
moved no money, so they do not stop a new attempt. **`TimedOut` counts as live on purpose**:
"the gateway never answered" honestly reads as "possibly holding funds". `Payment.LiveStatuses`
is the one definition, and the filter is built from it, so changing the list changes the model
and `MigrateAsync` refuses to run until a migration moves the index too.
`TheLiveAttemptIndex_ShouldFilterOnExactlyTheLiveStatuses` reads the filter back from the model.

**The row is written before every gateway call.** A crash between them leaves a `Pending` row
holding the slot, and the next attempt asks again under the same key. Writing afterwards would
leave nothing, the retry would mint a new key, and a new key at a gateway that received the
first call is a second authorisation.

**No domain events, and none are built until something needs one.** Nothing announces a
reconciled outcome (014) to the order, and the order does not need it. After an authorisation
times out, the order stays `Pending`. The next confirm asks under the same key and reads
whatever the reconciler settled (010, 014), so no order is ever wrong for want of the event.
The only consumer would be a notification. In the strangled arrangement that means delivery from
`payments-api` into another process, which is a message bus's job, and the bus is deferred.
Building an outbox with nobody to read it would be machinery proving nothing, the reason
Notifications exists (016). The trigger for building it is a consumer that needs the fact:
a customer message, or an order that must close itself.

---

## 014 — A timed-out authorisation is reconciled by asking the gateway

When the gateway does not answer an authorisation, the attempt becomes `TimedOut`, keeps its
idempotency key and keeps the order's live slot. A retry calls `Payment.Retry`, which moves the
same row back to `Pending` — same key, same amount — so the gateway is asked the same question
rather than a second one. A new row per attempt was the original plan and was worse: a second
row needs a second key, and the index would have to stop counting `TimedOut` to allow it. A
*capture* that times out leaves the payment `Authorized`, because that is still what is true.

**`PaymentReconciler` asks the gateway what it did.** Attempts `TimedOut` for longer than five
minutes are looked up under their key, and settled on the answer:

| Gateway record | Meaning | Attempt becomes |
|---|---|---|
| `Authorized` | funds are held | voided at the gateway, then `Voided` |
| `Declined` | received and refused | `Declined` |
| `NotFound` | the gateway looked and has nothing | `Abandoned` |
| `Unknown` | the lookup got no answer | unchanged |

**`NotFound` and `Unknown` are not interchangeable, and everything rests on that.** Reading a
failed lookup as "nothing happened" would release the order's slot while the customer's funds
were still held, and the next confirm would authorise twice. A hold it finds is *released*,
not recorded — writing `Authorized` would leave the funds held for days — and nothing is
written when the void gets no answer. `Abandoned` is new because the existing members would
each have lied: nothing was declined, and nothing existed to void.

It lives in Payments, not behind the outbox: the outbox carries decided facts, and a timeout's
whole content is that nobody knows. It polls once a minute, always sleeps even after a full
batch — an unresolved row is still first in line next time, and asking a gateway faster does
not make it answer — and takes a transaction-scoped advisory lock per sweep, so two
reconcilers do not duplicate work. The first version of that lock compiled, passed every
test, and threw on every sweep under a real two-process run (`SqlQuery<bool>` wants a column
named `Value`); the outer loop logged it as an ordinary failure, so neither process ever
settled anything. The test that now holds the lock from a second connection is the one that
would have caught it.

**The simulated gateway had to become honest for this to be testable.** A timeout is two
events under one name: `TimeoutRate` decides whether the caller hears anything, and
`LostRequestRate` decides, for those that do not, whether the request arrived at all. Never
recording a timed-out call — the first design — made the "funds are held" branch unreachable
while its tests passed. Its answered keys were first a dictionary on a singleton, which made
"every reconciler sees the same gateway" a precondition nothing stated: a restart of the
Payments service emptied it, and the next sweep settled **120 of 121** timed-out attempts as
`Abandoned`. They now live in `payments.gateway_ledger`, keyed on the idempotency key. Its
tests moved to the integration suite as a result, deliberately — the alternative was an
`IGatewayLedger` interface, which is the repository 001 forbids in a flat module.

Known gap: the simulator never declines a capture or a void. A real gateway can, and modelling
that honestly needs a real gateway's error vocabulary.

---

## 015 — The outbox is drained by the unit of work, in the seat's transaction

Domain events are written to `inventory.outbox_messages` in the same transaction as the seat
change that raised them, so the sale and the announcement of the sale cannot disagree.

**The drain is `InventoryDbContext.SaveChanges`, not the repository.** The repository is handed
one aggregate; the context commits everything it tracks, and a four-seat checkout drives four
holds through one scoped context. A drain in the repository would be complete only as long as
every mutation was followed by its own save — a property of a call pattern, not of the design.

**It clears the events after a successful save.** This was first called optional, and it is
not: without it, each save re-drains every aggregate still tracked, so a four-seat checkout
publishes ten rows for four holds.
`Hold_WhenSeveralSeatsAreHeldOnOneContext_ShouldWriteEachEventExactlyOnce` is the test. The
clear comes after the base call, never before, so a rejected save leaves the events for the
retry — and stale `Added` outbox rows from a rejected save are detached so the retry does not
write them twice. Both are "state from one save leaking into another".

**Two identities.** `Id` is a sequence and orders rows; `MessageId` is a GUID and identifies
the event. Ids are assigned at insert and transactions commit in any order, so a consumer
tracking "the last id I saw" skips rows silently — the classic outbox bug. The guarantee is
therefore per transaction only: one save's events arrive in order, which is what a reclaim's
`SeatReleased(Expired)` then `SeatHeld` needs. Across transactions there is no promise, and the
dispatcher claims on `ProcessedAt IS NULL`, never on a high-water mark.

**All three events are published, not only the one with a consumer.** That costs an insert on
the hottest path, and it loses to a simpler argument: **an append-only log is the one place
YAGNI has an asymmetric cost.** A consumer can be added later; history cannot. The measured
cost was near zero anyway (019), because a refused hold never reaches the save.

**What crosses the wire is a contract, not a domain record.** `SeatHeldV1` and its siblings
live in `Inventory.Contracts`, mapped from the domain events in one place, so renaming a field
inside `Seat` is not a breaking change to consumers or to rows already written. Names are
chosen — `inventory.seat.sold.v1` — not CLR type names. The reason enum crosses as a string,
because an integer would silently re-label every row the day a member is inserted. A domain
event with no mapping throws at the first save that raises it.

The payload is `jsonb`, so a stuck message can be diagnosed by querying into it. There is no
`xmin` on the table, because `SKIP LOCKED` already hands each row to one reader.
`ix_outbox_messages_unprocessed` is partial, so it indexes only the backlog.

**Retention deletes delivered rows only**, older than 30 days, hourly, by `ProcessedAt`. An
undelivered message is work however old it is, and a dead letter is the evidence that
something never arrived. Keeping everything forever was also a policy, and nobody had chosen it.

---

## 016 — The dispatcher delivers late, never wrong

`OutboxDispatcher` is a `BackgroundService` that claims a batch with `FOR UPDATE SKIP LOCKED`,
delivers it, and marks what happened. `SKIP LOCKED` means a second instance drains the same
table in parallel without delivering the other's messages, which is the cheap half of running
more than one host. Raw SQL, because EF Core cannot express a locking clause.

**A failing message backs off and lets the queue move past it**: exponential from two seconds,
capped at five minutes, and after five attempts it drops out of the claim as a dead letter —
kept, readable, and no longer retried. So a failing message is overtaken. Blocking the queue
behind it would protect a global ordering this design never promised (015) at the price of one
bad row stopping every good one. The loop catches everything, because an exception escaping
`ExecuteAsync` stops a `BackgroundService` silently for the life of the process.

**No seat invariant depends on it**, and that is checked, not claimed: the concurrency suites
never register a dispatcher. Under load, with the dispatcher off, 21,948 events piled up
undelivered and the sale still sold exactly 500 of 500 (019).

**Delivery is bounded, because the claim transaction used to stay open as long as the slowest
consumer.** Holding the consumer's table locked for twenty seconds held one Inventory
transaction open for twenty seconds, with fifty rows locked. Now each handler gets
`DeliveryTimeout` (2 s) and the whole tick `MaxBatchDuration` (5 s); an overrun fails that
message like any other failure, and unattempted messages are left for the next tick. Both run
on the wall clock rather than `TimeProvider`, alone in this codebase, because they bound real
locks rather than domain time. Delivering outside the claim transaction with a lease is the
textbook shape, and it would add lease state that `SKIP LOCKED` was chosen to avoid.

No MediatR and no reflection: `OutboxEventCatalog.Register<T>` closes over the generic when it
is registered, so the compiler checks that payload and handler agree. An unregistered name
throws, becoming a dead letter someone can see.

**Notifications exists so the dispatcher has somewhere to dispatch.** A dispatcher delivering
to nobody would be machinery proving nothing. It is flat — one handler, one table — and has no
`Map` half and no web framework reference, because its whole inbound surface is a handler
another module's dispatcher resolves. Delivery is at least once, so idempotency is a unique
index on `MessageId`; a read-then-write check is one that two concurrent deliveries both pass.
The violation is caught by constraint name, narrowly, so a dropped connection is not filed as
"already handled". It keeps the event's `OccurredAt` and its own `CreatedAt`, and the gap is
the one measurement of delivery latency the system has.

**Readiness is reported by modules, not asked by the host.** `/health/ready` asks every
registered `IReadinessCheck` and answers 503 if any cannot work. The host may not know that a
module has a database (017), so it counts votes. Inventory reports the outbox backlog and dead
letters; Payments reports attempts the reconciler is still carrying. Neither number fails the
check — taking the host out of rotation over one undelivered message would turn a late email
into an outage.

---

## 017 — Modules meet through contracts, and the one shared project may not name a module

Each module is reached through one seam, an `Add{Module}Module()` / `Map{Module}Module()`
pair called from the host, which knows nothing else about any module. A module that serves no
routes, like Notifications, has only the `Add` half.

**Modules call each other only through `.Contracts` assemblies.** Orders needs an event's
price; referencing Catalog would put its `DbContext` on Orders' compile surface, and the first
person who needed one more field would write a cross-schema join. So Catalog publishes
`IEventPricing` — an interface, a request, a response, a closed status enum, zero packages —
and an in-process adapter. That looks like the ceremony 001 argues against, and it fails all
three of 001's clauses: the substitution really happens (a fake in Orders' tests, a remote call
at extraction), it protects the module boundary itself, and it is four files. The interface is
named for the need rather than the owner, so it can only grow one way.

**Migrations are explicit by default and automatic only when asked.** `dotnet ef database
update` is the real path. Each module also runs a migrator at startup behind its own
`{Module}:MigrateOnStartup` flag, set only by the run profiles. It is an
`IHostedLifecycleService` migrating in `StartingAsync`, which the host calls before any
`StartAsync` — so before Kestrel accepts a connection. Each module keeps its migration history
in its own schema, so extracting one never means unpicking rows from a shared table.

**Five copies of that migrator became one `ModuleMigrator<TContext>`**, in
`Encore.Modules.Shared.Persistence`. Sharing a *step* in the host was refused: it would name
every module's context, put EF Core into the host, and teach it that modules have databases.
Sharing an *implementation* is different, provided three rules hold, and each is a test: the
project names no module and no `.Contracts` assembly; it declares no `ProjectReference`, and
nothing zero-dependency may reference it, so EF Core cannot reach the Domain through it; and
the host never names it. A module still registers its own migrator and keeps its own
vocabulary — Catalog still declares the string `"catalog"`.

**The test for sharing anything: the code is inert, and sharing it does not teach the shared
project a module's name.** `ClientIdEndpointFilter` fails both halves and stays copied into
three modules. `Encore.Shared` is referenced by the Domain and holds zero packages, so an
endpoint filter there would drag ASP.NET Core into the domain's reach; and a new web-only
project to hold one class is the ceremony 001 refuses. The copies store their client id under
different keys on purpose — a shared key would work until one route carried two filters, and
then work by accident. `Encore.Shared` holds contracts every module agrees on and nothing
else: `IDomainEvent`, `IIntegrationEventHandler`, `IReadinessCheck`.

---

## 018 — Payments becomes a service, and a seam is not proven by testing each side of it

`Encore.Payments.Api` composes the Payments module on its own, through the same
`AddPaymentsModule`, and Orders reaches it over HTTP through the same `IOrderPayments` it
already called in process. The interface did not change and nothing inside Payments changed,
which is the claim the modular monolith had been making from the start. It was cheap because
the seam was keyed by order, idempotency came from the database rather than from anything the
caller remembers, and `TimedOut` was already in the contract.

**A service surface is not a customer surface.** 008 refused customers a write route, and that
stands. The three `/internal/payments/*` routes are kept apart by four things rather than by
intent: a separate seam (`MapPaymentsServiceApi`, which `MapPaymentsModule` does not call), a
separate path prefix, a different credential (`X-Service-Token`, compared in fixed time — a
caller with only a client id gets 401), and no default (the host refuses to start without a
token, because a fallback token is one everybody has). A shared secret is the floor until
Identity or mTLS exists.

**The caller branches on `reason`, never on the status code, and an unreadable answer is a
timeout.** A 502, a truncated body, an unknown outcome, a refused connection — all become
`TimedOut`, the one status already handled correctly for "the money may or may not be held".
A rejected token throws instead: it is configuration and will not fix itself. **No Polly**: a
transparent retry would turn one ambiguous answer into several without telling anyone. The
database did not move — same schema, same Postgres — so this change's interesting failure is a
wire format, and a later split's would be a migration.

**For four days the extraction was inert.** Orders registered `HttpOrderPayments` with
`services.Replace(...)`, whose comment said it made the outcome independent of registration
order. `Replace` removes the first *existing* registration — and Orders was registered before
Payments, so there was nothing to remove. Payments then appended the in-process adapter after
it, and last-wins gave it every payment. The strangled configuration was a monolith wearing two
containers. Both halves' tests were green and would have stayed green forever: the adapter was
tested against a stub, the service's routes on their own, and no test project referenced both
modules. The fix is `TryAddScoped` in Payments, and `StranglerSwitchTests` resolves
`IOrderPayments` from a composed container in all four registration orders.

**It was found by the chaos rig, before it had measured anything**: stopping `payments-api`
and watching confirms keep succeeding. **A seam is not proven by testing each side of it.**
Every future extraction gets a composition test as part of the extraction.

Both arrangements still run — `docker compose --profile load up` is the monolith, `--profile
strangled up` the pair — and hosts may not reference each other, which a test enforces.

---

## 019 — Measure first, then break it on purpose

**The load harness was built before the outbox, out of phase order.** The outbox writes into
every seat transaction, so a baseline taken afterwards could never say what it cost. **k6 over
NBomber**, and the reason is the build rules rather than taste: NBomber would be a new project
and a package every purity rule would need a carve-out for, while a k6 script is outside the
solution entirely and can only reach the system over HTTP — which is the only surface a real
flash sale touches.

**Two thresholds are assertions.** `seats_sold <= SALE_SEATS` is the oversell invariant under
sustained load against a real Kestrel, Postgres and Redis, and it only counts distinct seats
because every iteration uses a fresh client id and never retries. `unexpected_responses == 0`
says a 500 is a fault while a 409 is the system working. **There is deliberately no latency
threshold:** an SLO set before choosing which of the system's three configurations runs would
measure a system nobody has decided to run. Summaries stay out of git — one laptop is not a
claim about performance.

**What the outbox cost**, in p99 ms (three runs before it, one run each after):

| | hold, contention | purchase | iterations |
|---|---|---|---|
| Before the outbox | 41.1 – 50.2 | 44.2 – 60.5 | 425,299 |
| Drain only | 48.4 | 57.4 | 398,048 |
| Drain and dispatcher | 78.2 | 141.4 | 304,071 |

The drain sits inside the baseline's spread: only ~15,000 of 304,000 iterations write at all,
because a refused hold throws before the save. **The dispatcher is the whole cost**, as a
second workload competing for the same database. Do not "optimise" the outbox by weakening the
drain's atomicity, which is the entire product.

**The chaos rig** (`load/chaos.sh`): k6 measures and asserts, the script injects faults and
reads the aftermath from Postgres. A marker row pins the two clocks together. A fault measured
as a number lands between scenarios, as a paired control window and broken window. The stall
lands inside a paced window, since a backlog needs a sustained rate, and the reconciler faults
are startup settings. Each fault gets its own run so one aftermath does not feed the next.

| Fault | Invariants | What it exposed |
|---|---|---|
| Payments stopped | held; 0 seats sold without an owner | a stopped container swallows connections, so every confirm waited the full 10 s client timeout — and first, that the extraction was inert (018) |
| Two reconcilers | held; no attempt settled twice | the gateway's memory was per process; a restart settled 120 of 121 attempts `Abandoned` (014) |
| Redis stopped | held; no oversell | a hold cost 85× without Redis, and the healthy control window was full of `too many clients` |
| Dispatcher stalled 20 s | held; request path p99 ~20 ms | delivery max 20.6 s: late is not wrong — and the claim transaction was open the whole time (016) |
| Orders (6th run) | 1,943 orders; zero partly sold, sold-unpaid or paid-unsold | 92 cancels landed after their confirm's sale and stepped back (012) |

**The Redis cost took four sessions to remove, and the order is the lesson.** Npgsql pools per
connection string, so each host had one pool of 100 against a Postgres allowing 97 in total;
the budget is now written down (`max_connections=300`, pools of 100 and 50). Redis timeouts
were cut from 5 s to 250 ms and a hold went from 85× to 22× — not the promised half-second,
because each attempt still cost ~1,000 ms. Tripling `ConnectTimeout` moved nothing, which ruled
it out. `BacklogPolicy.FailFast` removed it: a hold's median cost of losing Redis fell to 6%,
and every refusal was "no connection is active". Logging each refused attempt — ~3,000 a second
— was then the suspect for what purchases still paid; logging an outage's edges instead
(`LockOutageLog`) took the hold's median cost to zero, but purchases only from 2.0× to 1.78×.
**A change is never measured by the session that motivated it.**

The next step was a cooldown. After a refusal, `CooldownDistributedLock` stops asking Redis for
`Inventory:RedisLock:Cooldown` (1 s) and answers "unavailable" itself. It was measured against
its own control, `REDIS_LOCK_COOLDOWN=00:00:00`, two runs each, alternated. A purchase's median
cost of losing Redis fell from 1.53× and 1.73× to 1.47× and 1.42×. The effect is real, since the
ranges do not overlap, but small. Holds won while Redis was gone stayed about 25% below the
healthy window in every Redis-lock run, with or without the cooldown, and did not move at all
with the Postgres lock (005). What remains belongs to losing Redis, not to asking it, and is
unattributed.

The seat lock's removal was measured the same way: three runs against six, 18% more attempts,
lost races 0.10% → 0.23% (004). And the partial index suspected of a 62% p99 regression was
cleared by three runs with it and three without — one noisy window was the likelier answer.

**The dispatcher's cost was mostly two fixable things.** After the audit, the outbox claim was
ordered the way its index is (it had read the whole due backlog every tick), and EF Core's
per-statement logging went to `Warning`. A fresh baseline, three runs on 2026-09-24 with the
dispatcher on, put the p99 of a contended hold at 34–44 ms and a purchase at 27–30 ms, with one
86 ms outlier whose median matched the others. That is inside the pre-outbox spread or below
it, against 78.2 and 141.4 before. Every run held all three invariants.

**What this is not.** One laptop, mostly one run per configuration. The counts and the
mechanisms behind them are strong; latency comparisons across sessions are not.

---

## 020 — A claim nothing checks reads like a claim that holds

The rules that matter here are enforced by the build or by a test rather than by prose (002,
008). This entry is about the checks around the checks.

**The tests run in a container.** On the development machine, Smart App Control blocks every
freshly built unsigned assembly: in enforcement mode, a file with no reputation verdict is
refused, a rebuild produces a new hash and a new unanswered question, and there is no
exclusion mechanism. Signing, or permanently disabling a security feature, were the other
options. The container is reversible, free, and makes the suite run identically on any machine
with Docker. Testcontainers run as siblings on the host's daemon rather than
Docker-in-Docker. Source is copied into the image rather than bind-mounted, because Windows
`bin`/`obj` in front of a Linux build fail in ways that look like code problems — which also
means **the command needs `--build`**, or it tests the last image and reports green.

**CI runs exactly that command** on every push to `main` and every pull request, and **reads
the verdict from the summary lines, not the exit code**: `docker compose run` exits 0 when it
cannot reach the daemon at all. The workflow counts `Passed!` lines against the test projects
under `tests/`, which fails when an assembly fails, when nothing ran, and when a test project
exists but never reached the runner. Each of those was checked by fabricating the log that
produces it. CI does not run the load harness — a latency number from a shared runner would be
a measurement wearing a threshold's clothing — and it does not re-check the purity rules the
build already enforces.

**The documents drifted; the code did not.** Two audits found the suite green and the prose
stale: the project guide and the README described one host four days after there were two,
and cited decisions for things other decisions had done. Where a document is machine-readable,
it now has a test — the OpenAPI document (008), and this log's index, which must list every
entry in order with a working anchor and numbering without gaps. **No test reads English**, so
the rest is checked by deriving each claim from the code and comparing.

**One environment call, recorded because the failure looked like a code bug.** Postgres is
published on host port `55432`, not `5432`, because a native PostgreSQL 18 service on the
development machine owns `5432` — and `docker ps` still prints the mapping as if Docker had
won it. Every containerised path stayed green, since container traffic never touches a
published port; only `dotnet run` and `dotnet ef` failed, with a password error against a
correct connection string. Moving the host side is reversible and leaves the developer's
machine alone.

---

## 021 — Telemetry follows the asymmetry

OpenTelemetry was brought forward from On Tour by the owner. Its packages live in
**`Encore.Telemetry`**, a host-side project that both hosts reference and nothing else may.
The alternative was to drop `EncoreNoDirectPackages` from the hosts. That would reopen the rule
behind 008's hand-written contract for the sake of four packages. 017's "one shared project"
still holds for modules. This second one is shared by hosts. The architecture tests forbid it
to name a module, a contracts assembly or `Encore.Shared`, forbid anything but a host to
reference it, and forbid any module to emit a reference to OpenTelemetry.

**Modules emit through the BCL.** Inventory's `ActivitySource` and `Meter` are
`System.Diagnostics` types, recorded at the adapters' edges: the lock cooldown, the outbox
dispatcher, and the two driving adapters. The Domain and the use cases are unchanged. The host
subscribes to `Encore.*` by wildcard, so it never names a module. **Only Inventory has
instruments of its own.** The flat modules get the framework's spans for HTTP, Npgsql and
HttpClient and nothing more, for 001's reason.

**An outbox delivery links to the trace that raised it; it does not join it.** The drain
stores `Activity.Current`'s `traceparent` in a nullable column, and the dispatcher starts a
consumer span with a link to it. A parent would have been wrong on both counts: the request
ended long before, and a redelivery would give it a second child. The drain's atomicity is
untouched. It writes one more string per row.

**Two things the first live run showed.** Every background poll (the outbox claim, the sweep,
the reconciler) arrived as its own trace of one Npgsql span, about one a second per job,
burying the requests. The root sampler now drops a client span that nothing started, and
Npgsql's metrics still price those queries. The confirm's trace crosses both processes, with
authorise and capture as server spans in `encore-payments`, without any code for it:
HttpClient propagates W3C context natively.

**Off unless `OTEL_EXPORTER_OTLP_ENDPOINT` is set**, so a load run measures the system without
it, and tests and `dotnet run` are unchanged. Locally, `grafana/otel-lgtm` runs under
`--profile telemetry`, with an "Encore — flash sale" dashboard provisioned from `ops/grafana/`.

**What exporting costs, measured.** The collector ran warm through both arms, and each "on" run
was checked for arrival afterwards. The comparison was two runs each, alternated.

- Throughput fell about 25%: 310k and 311k iterations, against 439k and 394k.
- A contended hold's median rose from 18 ms to 22 ms, and a sale hold's p95 roughly doubled,
  from 31–38 ms to 70–72 ms.

On one laptop, the SDK's cost and the collector's CPU cannot be told apart, so this prices the
whole arrangement. An earlier, unverified pair disagreed with itself (231k and 418k iterations),
which is why arrival is now checked. Every trace is sampled. A ratio sampler is the obvious
lever, and it is not measured.

**The dashboard found two things the tests did not.**

- The SDK pushes metrics every 60 s, which gives a one-minute sale one point, so exports now go
  every 5 s unless `OTEL_METRIC_EXPORT_INTERVAL` says otherwise.
- The delivery-lag histogram used the SDK's default boundaries, which are sized for
  milliseconds. Every delivery landed in the 0–5 bucket, and the dashboard read p99 = 5 s. With
  boundaries in seconds, deliveries take 0.1–2.5 s, which is the dispatcher's 1 s poll showing.

---

## 022 — Once money has moved, nothing stops the step halfway

An audit found three ways a payment step could be abandoned between the gateway and the row
that records it. Each ends with funds held that nothing releases, which 010 and 014 exist to
prevent. This amends 013 and 014.

**A confirm or cancel honours the request's token only while it loads the order.** Every
later step is irreversible or undoes one, so it runs to the end. Before, a client that hung up
after the authorisation threw out of the sale, and left the order `Pending` and the
authorisation live with no void coming. Inside Payments, once the attempt's row is committed,
the gateway call and the save of its answer ignore the caller's token for the same reason. This
is the rule 004 already applies to releasing a lock. The cost is that a confirm keeps working
for a client that has gone, for as long as the gateway's own timeouts allow.

**The reconciler holds a row lock from the lookup to the save.** Before, it voided the funds at
the gateway and only then tried its `xmin`-checked save. A confirm that retried the row in
between won the save and asked again under the same key, and the gateway answered with the
authorisation just released. The seats sold, and the capture went against a void. The
alternative was a `Reconciling` status, which would add a state to every switch and to the
live index. The lock costs a confirm on that one row a wait of one gateway round trip, after
which it answers `lost_race` and the next confirm pays under a fresh key.

**A `Pending` attempt is fenced, and it is found.** A confirm that finds one resumes it with
a write, so `xmin` orders two confirms instead of letting both save over each other. A save
that loses answers from the winner's row, not with a 500. 013 relied on "the next attempt
asks again", but nothing guarantees a next attempt: a cancel voids nothing on a `Pending` row.
So a `Pending` attempt older than the reconciler's minimum age is taken to be one a crash left
behind. It is claimed as `TimedOut` under the row lock and settled like any other, and the
readiness check counts it.

The simulator still never refuses a capture, so it could not have shown the capture against a
void. That gap is 014's, and it is unchanged.

---

## 023 — A swept seat keeps the record of its lapsed hold

`ExpireHold` used to clear `HeldByClientId` and `HoldExpiresAt`, and `Sell` picked its
refusal from the status. So the sweep changed the answer. Before it reached a seat, the lapsed
holder heard `HoldExpired` and anyone else heard `NotTheHolder`. After it, both heard
`NoActiveHold`. Orders records an order `Expired` only when every refusal is `HoldExpired`, so
the same lapsed order ended `Expired` or `Failed` depending on the sweep's timing. That breaks
006: the sweep is cleanup, and turning it off must change nothing.

Now `ExpireHold` sets only the status, and the lapsed pair stays on the row as a record. `Sell`
reads the pair rather than the status, so a swept seat answers exactly as an unswept one does.
Only a release, a sale or a new hold changes the pair. Nothing that counts holds reads it
without the status: the cap, the sweep and the partial index all filter on `Held`.

The alternative was to make the lazy path forget as well, answering `NoActiveHold` to
everyone. That is simpler, but it throws away the one distinction Orders uses to tell a customer
their time ran out. The cost is an `Available` row that still names a client, which the field's
documentation now says.

---

## 024 — The outbox promises delivery, not order

015 said one save's events arrive in order, and gave a reclaim's `SeatReleased(Expired)` then
`SeatHeld` as the reason. The dispatcher never did that. When a handler fails, its message is
backed off and the next row claimed in the same tick overtakes it, and that row can be its
sibling from the same save. `SKIP LOCKED` can also hand two rows of one save to two
dispatchers. Keeping the promise would need a per-save id and a claim that stops behind a
failed sibling. That is head-of-line blocking, which 016 rejected so that one bad row does not
stop the good ones. So the promise is withdrawn. **Delivery is at least once, in no order a
consumer may rely on.** A consumer deduplicates by `MessageId` and reads each event on its own.
Nothing depended on the ordering: the only consumer handles `SeatSold`, and a sale raises one
event.

**A payload is read strictly.** Deserialising was lenient, so a row missing a member reached
its handler with `Guid.Empty` and was marked delivered. Now it fails, is retried, and ends as
a dead letter someone can see, which is 016's rule. The cost is that any member added to a V1
contract must have a default. The wire names now ship on the contracts as attributes, so a
consumer holding only `Inventory.Contracts` reads what Inventory writes. Before, the names
lived only in Inventory's internal serializer options. A test pins each contract's payload.

**"A consumer can be added later; history cannot" is narrower than it read.** An event type
registered with no handler is marked delivered as soon as it is claimed. A consumer added
later is handed only new rows. The retained ones are still in the table for the retention
window, so it can replay them, but nothing delivers them to it.

---

## 025 — An order owed its capture is finished by a sweep

This supersedes 010's "`AwaitingCapture` is resolved by the next confirm, not a job". 010
rejected a job as load-bearing in the way 006 forbids. But 006's test is that everything stays
correct with the timer off, and a capture sweep passes it exactly as the payment reconciler
does (014). With the sweep off, the next confirm still finishes the order. The reasoning did
not separate the two. The cost of relying on the next confirm was real. The confirm that left
an order awaiting capture had answered 200, "you have every seat", so nobody asked again. One
chaos run ended with 80 orders awaiting capture and 80 authorisations untouched. When those
lapse at the gateway, the seats have been given away.

**`CaptureSweeper` confirms them again, as a customer would.** It adds no rule of its own:
it calls the same `ConfirmAsync`, once a minute, for orders owed their capture for more than a
minute. It is behind `Orders:CaptureSweep:Enabled` and takes an advisory-lock lease per sweep,
like the reconciler.

**The sale is recorded before the capture is asked for.** A confirm now writes
`AwaitingCapture` and `SoldAt` as soon as every seat sells. Before, the order row was first
written after the capture round trip, so a confirm that died there left seats sold and money
held under an order that still read `Pending`, and nothing could find it. `SoldAt` is also how
the sweep leaves a confirm still waiting on its own capture alone. The cost is one more write
to the order row per confirm. It also changes what 012's cancel sees: once the sale is recorded,
a cancel finds `order_not_pending` rather than `lost_race`. `lost_race` remains only for the
moment between the sale and its record. Orders already awaiting capture when this shipped were
given their placement time, so the sweep picks them up.

---

## 026 — What remains of the roadmap, restated

**On Tour's "cloud deploy" becomes a measurement run on more than one machine.** Every claim
here is a load or chaos result, and each carries the same caveat: one laptop, with k6, the
system and the telemetry collector sharing a CPU (019, 021). A standing cloud deployment would
not remove that caveat, and it would add cost without evidence. The chaos rig stops containers
with `docker compose`, so it could reproduce none of its faults against managed services. A
public deployment would also expose `/purchase` and seat-map creation to anyone, with a
development service token, which is Identity and secrets work the roadmap does not include.
So the remaining item is a throwaway run: the unchanged stack on one host, k6 and the
collector on another, the baseline and the chaos runs repeated, and everything torn down
afterwards. It needs `load/chaos.sh` pointed at a remote Docker context, with k6's
`ENCORE_BASE_URL` pointed at that host. A standing deployment stays on the deferred list, and
now the list and the roadmap say the same thing.

**Soundcheck reads pairwise: Payments via the strangler, Notifications via the outbox.** That
is what was built. Moving Notifications into its own process would need a transport, and
013 already made cross-process delivery a message bus's job, with the bus deferred. So it is
deferred until a bus exists, and the roadmap row no longer suggests it was forgotten.

**The strongest cross-module claim is now a check.** "No order partly sold, sold without
money, or paid without its seats" rested on chaos.sh rows that were printed and never
compared. `CheckoutCompositionTests` composes the real Inventory and Payments modules behind
checkout, races every confirm against its cancel, and asserts the same three invariants on
every test run. chaos.sh now exits non-zero when any row it reports as "must be 0" is not.
This does not reopen 020: the check is about rows, not latency.

---

## 027 — A comment carries a reason, never the name

An audit counted 1,286 `///` blocks outside `Migrations/`. Most said only what the member's
name says, 94 were `<inheritdoc />` tags that no documentation file read, and 44 had gone
stale. The comments that carry a rule were hard to find among them. Fewer than 300 remain.

**A comment exists only where a maintainer would otherwise get something wrong**: a deliberate
conflation, an idempotency promise across a boundary, a pinned value, what null means, a trap,
an invariant with its decision number, or why the obvious thing is not done. **A fact is
written once**, where it acts or on the contract that promises it, and a repo-wide rule is not
restated per member. **`///` only where visible outside its file**, and **`<inheritdoc />` only
on this repo's own contracts and ports**. **Endpoint summaries live only in `openapi.json`
(008)**, not in `.WithSummary`, which nothing served and which had drifted.

The alternative was to document every member. It reads as thorough, and it hides the rules
among restatements, where a stale claim goes unnoticed. The cost is fewer IDE tooltips, and a
reviewer holds each new comment to this rule.
