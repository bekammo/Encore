# Decisions

An append-only log of the choices in Encore that are worth defending, and the
reasoning at the time. Entries are not rewritten when circumstances change —
a superseding entry gets added instead.

---

## 001 — Why Inventory is hexagonal and the other modules aren't

Inventory is the only module with a genuinely hard problem in it: two people
clicking the same seat at the same millisecond must not both get it, and the
mechanism for preventing that (optimistic concurrency on the seat row in
Postgres, a short-lived Redis lock in front of it to keep the losers off the
database, an outbox so the sale and the announcement of the sale cannot
disagree) is exactly the kind of thing I expect to get wrong twice before I get
it right. Ports and adapters buy me the ability to change that mechanism — or
test the seat state machine against a fake clock in microseconds — without
touching the rules themselves. Catalog, Orders and Payments are CRUD over
tables nobody contends for, so the same structure there would be pure ceremony:
three folders and an interface to express "save this row", with no invariant
being protected and no substitution ever actually performed.

The asymmetry is the point, and it is what I would want to be asked about in a
review: architecture is a cost you pay for optionality, and you should only pay
it where you will actually spend the optionality.

<!-- Expand later: what specifically went wrong in the naive version; why the
     Redis lock is an optimisation rather than the correctness mechanism, and
     what happens to throughput if you remove it; what the load test showed. -->

---

## 002 — Inventory's domain is its own project, not a folder

`Encore.Modules.Inventory.Domain` is a separate assembly rather than a
`Domain/` folder inside the module. A folder makes the "no infrastructure in
the domain" rule a convention that survives exactly as long as everyone
remembers it; a project with zero `PackageReference` items makes it a fact the
compiler enforces, because domain code cannot name a type from an assembly it
does not reference. The csproj additionally fails the build (`ENCORE001`) if a
package reference is ever added, so the rule breaks loudly at the moment of the
mistake rather than in review.

The cost is one extra project and a slightly deeper folder tree. That is cheap
next to the thing it buys: when I claim in an interview that the domain is
infrastructure-free, the build is the evidence, not my word.

<!-- Expand later: whether Ports should also move into the Domain project once
     IClock has a real consumer, and how this compares to the NetArchTest
     approach used in the other direction. -->

---

## 003 — Ports speak the module's language, never the adapter's

`ISeatRepository.SaveAsync` originally documented that it throws EF Core's
`DbUpdateConcurrencyException`. That was wrong, and wrong in a way the hexagonal
structure exists specifically to catch: a port is the contract the inside of the
module depends on, so naming a persistence library's exception type in it means
every caller that handles losing a race is coupled to EF Core through the one
interface whose entire job is to hide it. The structure was clean and the
contract was not, which is the failure mode worth remembering — the project
reference graph cannot catch a leak that travels through a type name in a
signature or an XML doc.

Losing an optimistic-concurrency race now has a name the module owns:
`ConcurrentSeatModificationException`, in `Inventory.Domain`. The EF adapter
catches `DbUpdateConcurrencyException` and translates, keeping the original as
the inner exception so diagnostics lose nothing. Fixed before any transition
logic existed, precisely because it was free then and would have meant touching
every caller later.

<!-- Expand later: whether the Redis lock adapter needs the same treatment once
     it has a failure mode worth distinguishing. -->

---

## 004 — Orders and Payments are real modules, not permanent stubs

They are currently the worst of both options: real EF Core and Npgsql package
references with no entities, no mappings and no DI registration, so they read as
started-then-abandoned rather than as a deliberate stub. That ambiguity is the
actual problem — an empty module is fine, and a working module is fine, but one
wearing the other's clothes costs a reader time every time they look.

Both become real in Load-In. Payments has to exist as working in-process code
before Soundcheck can extract it, because Strangler Fig needs something to
strangle; Orders has to exist because something must eventually call into
Inventory's hold flow, and an aggregate with no caller proves nothing. Neither
needs the hexagonal treatment when it gets built — they stay flat, per 001.

Ordering: after Seat's transitions, not before. Inventory is the spine of the
project and the reason it exists, and building CRUD around an aggregate whose
rules are still undecided would mean guessing at the shape of the thing that
calls it.

<!-- Expand later: whether Orders owns the hold lifetime or Inventory does, and
     which of the two decides that a checkout window has run out. -->

---

## 005 — Aggregates are constructed by factory, never by public constructor

`Seat` has a private parameterised constructor, a public static `Create`, and a
private parameterless constructor used only by EF Core's materialisation
pipeline. This is the convention for every aggregate built from here, not a
one-off.

The reasoning is the same reasoning that justifies Inventory getting hexagonal
treatment at all. The point of the tactical patterns is that invariants are
unbypassable; a public constructor with settable properties is a hole straight
through that, because `new Seat { Status = Sold }` reaches a state no rule ever
approved. `Create` takes an id and an event id and nothing else, so "every seat
that exists was born Available" is enforced by the compiler rather than
remembered by whoever is writing a seed script at 11pm. The only routes out of
Available are the transition methods, which is where the rules live.

The parameterless constructor is a mechanical accommodation to the ORM and
nothing more. It is private rather than protected because `Seat` is sealed.

<!-- Expand later: whether Create should reject Guid.Empty, and whether seats are
     ever created individually or only in bulk as part of an event's seat map. -->

---

## 006 — The hold cap is a policy, not an invariant

A client may hold at most 4 seats per event. That rule spans four rows, and `Seat` is a
one-row consistency boundary, so the aggregate cannot enforce it — no amount of
optimistic concurrency on a single seat says anything about three other seats. It lives
in the Application layer instead.

Enforcement is a Postgres count of the client's live holds for the event
(`status = Held AND hold_expires_at > now`), serialised by a Redis lock keyed on
client+event, taken alongside the existing per-seat lock. Counting in Postgres rather
than keeping a Redis counter means the count is automatically correct about expiry and
has no bookkeeping to drift; the extra lock closes the only race that matters, which is
one client racing themselves across several seats at once. That contention is rare, so
the lock is cheap.

The consequence is worth stating plainly rather than discovering later: **this makes the
cap a policy enforced best-effort-plus, not a hard invariant.** If Redis is unavailable,
two simultaneous holds by the same client could both pass the count and take a fifth
seat. That is a deliberate asymmetry. A cap breach is a refund email; an oversell is a
customer holding a receipt outside a sold-out venue. The no-double-sell invariant stays
Postgres-authoritative and survives Redis being gone entirely — which is the property
the architecture is actually built to defend.

Note also what the cap counts: concurrent *holds*, not lifetime purchases. A client who
holds 4 and completes checkout is immediately free to hold 4 more, because sold seats
are no longer held. A per-customer purchase limit is a different rule and would belong
in Orders, not here.

<!-- Expand later: what the load test shows about how often the lock is actually
     contended, and whether the count query needs an index on (event_id,
     held_by_client_id, status). -->

---

## 007 — The seat state machine

`Seat` has exactly three transitions — `Hold`, `Release`, `Sell` — and every rule
about when a seat may change hands lives in them. Nothing above the aggregate is
allowed to re-decide any of it. This entry records the choices inside that machine
that a reviewer would reasonably push back on, because until now they existed only
as instructions and code comments, and the log that is supposed to hold them jumped
from construction (005) straight to the hold cap (006).

**Hold duration belongs to the aggregate, not the caller.** `Hold(clientId, utcNow)`
takes the current instant and never an absolute expiry. A caller-supplied expiry is
a hole straight through the rules in the same way a public constructor is (005) — it
lets anyone grant themselves a hold lasting until the year 3000. The caller says
when *now* is; the aggregate says how long five minutes is.

**Re-holding a seat you already hold is an idempotent no-op, and the expiry does not
move.** Two separate decisions that pull in opposite directions, and both matter. It
is a no-op rather than an error because a retried request — a dropped response, a
double-clicked button — must not tell a client that a seat is taken when *they* are
the ones holding it. The expiry stays put because the alternative is a client
extending a hold indefinitely by repeating the request, which is squatting with
extra steps. So the retry is safe and the clock still runs.

**Reclaiming a lapsed hold raises `SeatReleased(Expired)` before `SeatHeld`, not a
single event.** One event would record the new claim while leaving the old one
looking indefinitely live, so the log would show two consecutive claims with no
point at which the first stopped being true. Two events in that order keep hold
history reconstructable from the log alone, which is the property that makes the
event stream worth having. The cost is that one call emits two events; that is the
right trade when the alternative is an unreconstructable history, and event history
cannot be backfilled later.

**Expiry is enforced lazily on every path, and the background sweep is cleanup
only.** A row reading `Held` whose `HoldExpiresAt` has passed is treated as
available by every read and write in the aggregate, whatever the column says.
Refusing a lapsed hold until a sweep had run would make correctness depend on a
timer, and a timer that is load-bearing for an invariant is an invariant that fails
whenever the timer is late. The test for whether this has been violated is concrete:
if a test cannot pass with the sweep disabled, the sweep has become load-bearing and
the design is broken. `EffectiveStatusAt` is private for the same reason — a second
copy of the lapsed-hold rule anywhere else is how the sweep quietly acquires
authority it is not supposed to have.

**There is no direct Available-to-Sold route.** `Sell` requires a live, unexpired
hold by the same client. Allowing a sale without one would make the hold step
skippable, and a seat sold out from under an actively-paying holder is a double-sell
that optimistic concurrency *cannot* catch, because the two writers never contend on
the same row version. The invariant depends on every sale passing through a hold.

**`Sold` keeps `HeldByClientId`.** It would be tidier to null it out alongside
`HoldExpiresAt`, and it would also throw away the answer to "who owns this seat".
Keeping it means the row can answer that without replaying the event stream, and it
is what lets the application layer distinguish "you already bought this" from
"somebody else did" (008). `Sold` is terminal: nothing moves a seat out of it.

**Refusals throw one exception type carrying a closed reason enum**, rather than a
type per refusal. The caller nearly always wants to branch on *why*, and a closed
enum makes that a `switch` the compiler can check, where an exception hierarchy
makes it a chain of `catch` blocks whose order matters. It also keeps the domain's
public surface small.

**The expiry boundary is exclusive: `HoldExpiresAt <= utcNow` is expired.** A hold
at exactly its expiry instant is over. This almost never matters in production and
matters immediately in any test that constructs the boundary deliberately, which is
the only reason it is worth writing down.

<!-- Expand later: whether a client re-holding after their OWN hold lapsed should
     reclaim (current behaviour, and it lets them extend indefinitely by letting the
     hold lapse first) or be refused as squatting — the idempotency rule above and
     the reclaim rule overlap at this edge and neither settles it. Also: what Sell
     should report to a caller who never held the seat once the real holder's hold
     has lapsed; it currently says HoldExpired, which is untrue for them. -->

---

## 008 — Refusals are return values, not exceptions, above the aggregate

`Seat` throws to refuse. The handlers above it catch, translate, and return a
`HoldSeatResult` / `SellSeatResult` carrying a closed outcome enum. The boundary
between those two styles is deliberate and is the main shape decision in the
Application layer.

The reason is that the two layers disagree about what is exceptional. Inside the
aggregate, an illegal transition genuinely is — it means a rule was about to be
broken. At the use-case boundary during a flash sale, **losing a seat to somebody
else is the single most common outcome there is.** Exceptions for the common path
cost real throughput at exactly the moment throughput is scarce, and they push
callers into control flow made of `catch` blocks. An enum lets an endpoint switch
over every case and map each to its own response, with the compiler checking that
none was forgotten.

**The Redis lock is an optimisation, and the code treats it as one.** Failing to
acquire it is not an error and does not abort the attempt: a null token means
somebody else has it, and the handler carries on to Postgres, where the concurrency
token settles the race properly. This is the operational expression of the claim 001
makes — correctness survives Redis being gone entirely, and all that is lost is the
throughput saved by keeping the losers off the database. A handler that aborted on a
missed lock would quietly promote Redis to a correctness dependency and make the
claim false.

The lock TTL is five seconds, sized to one write attempt and deliberately unrelated
to the five-minute hold window. It is a safety net for a process that died
mid-write, not a booking. Tying it to the business window would mean a crashed
process blocked a seat for five minutes.

**A lost race is retried exactly once.** Losing means somebody else wrote the row
first, so the seat almost certainly now reads `Held` by them — reloading and
re-asking converts a bare "you lost a race", which tells a customer nothing, into
the accurate "somebody already has it". The bound is one because a second loss means
genuine sustained contention, and retrying harder precisely when the system is
busiest is how a thundering herd becomes worse instead of better. The retry also
requires a genuinely fresh read to mean anything, which is what 009 is about.

Domain events are cleared before each attempt. A rejected attempt leaves events on
the instance describing a transition that never happened, and letting those survive
into the attempt that succeeds would publish a hold that was never taken — a bug
that would only surface once the outbox exists in Soundcheck, i.e. long after it was
introduced.

**Selling a seat this client already bought returns `Sold`, not a refusal.** Made
possible by `Seat` keeping `HeldByClientId` through the sale (007): the handler can
ask *who* owns the sold seat and answer accordingly. A customer whose response was
lost, or who double-submitted the checkout form, is asking for a state that already
holds — telling them their own completed purchase failed would be both untrue and
alarming. This mirrors the idempotent re-hold, and raises no second `SeatSold`,
because nothing happened.

**Unhandled refusal reasons are deliberately left to propagate.** Each handler
catches exactly the reasons its transition can produce. A reason arriving that the
handler does not know about means the aggregate's contract moved without the handler
being told, and mapping it to some catch-all response would hide that until it
mattered. Better a loud failure in development than a plausible wrong answer in
production.

<!-- Expand later: whether the one-shot retry should become a small bounded backoff
     once the load test shows what contention actually looks like, and whether the
     outcome enums survive contact with the HTTP layer unchanged or want a separate
     response mapping. -->

---

## 009 — The repository defeats EF's identity map on reload

`EfSeatRepository.GetByIdAsync` checks whether the context is already tracking the
seat and, if so, calls `ReloadAsync` rather than handing back the tracked instance.

This exists because of the retry in 008, and without it that retry is theatre. EF
Core's identity map returns the instance a context is already tracking, complete
with the stale concurrency token and whatever mutations the previous attempt made
before its write was rejected. So a handler that loses a race, reloads, and tries
again would be re-attempting against *exactly the state that just lost* — same row
version, same rejection, with a second round trip spent proving it. The retry would
look right in the code and accomplish nothing, which is the worst kind of wrong.

The subtlety worth keeping: `ReloadAsync` detaches the entry when the row has been
deleted, leaving the instance pointing at a seat that no longer exists. The method
returns `null` in that case rather than a ghost.

This is an adapter concern and stays entirely inside the adapter — the port says
"load a seat", and nothing above it knows that EF has an identity map or that it had
to be worked around. That is the hexagon earning its keep in a small way: a real
persistence-library quirk absorbed at the boundary instead of leaking into the use
case as a "remember to reload" comment.

<!-- Expand later: whether a short-lived context per attempt would be a cleaner
     answer than reloading within one, once the handler's lifetime is settled by the
     HTTP layer. -->

---

## 010 — A lock that can say "I don't know", and two locks with different authority

`IDistributedLock.TryAcquireAsync` returned `Task<string?>`: a token, or nothing. That
shape could not express the difference between *somebody else holds this* and *the lock
service did not answer*, and collapsing those two turned out to cost both correctness
and availability at once.

**What it cost.** The per-client hold cap (006) was measured under the contention it
exists for — one client, twelve seats, twelve simultaneous requests, cap of four — and
produced **twelve holds**. Not a weak cap; no cap. `TryAcquireAsync` does not wait, so
one request took the client lock and eleven got `null`, and the handler's policy on
`null` was to proceed. That policy is right for the seat lock, which has the row's
concurrency token behind it, and catastrophic for the client lock, which has nothing.
The failure mode is self-concealing: the lock is only contended when several of one
client's requests are in flight, which is precisely the case the cap exists to catch.

Separately, a Redis outage took every hold down with it. Nothing caught the connection
exception, so it escaped `HandleAsync` — while 001, 008 and the port's own doc all
claimed correctness survived Redis being gone. It did not.

**The change.** `TryAcquireAsync` now returns a `LockAcquisition`: an outcome of
`Acquired` / `HeldByAnother` / `Unavailable`, plus a token when there is one. The Redis
adapter translates `RedisConnectionException` and `RedisTimeoutException` into
`Unavailable` rather than letting them out — the same duty the EF adapter performs for
`DbUpdateConcurrencyException` under 003, now that the second adapter has a failure mode
worth distinguishing. That was the open question 003's own note left behind, and this is
its answer.

The translation is deliberately narrow. Catching everything would file a malformed Lua
script or a wrong type at a key under "unavailable", and the caller would carry on
believing it had merely lost a lock.

**Translating in the adapter was not enough, and the reason is worth remembering.** With
Redis absent, `ConnectionMultiplexer.Connect` throws from inside the DI factory — while
the container is building the lock adapter, before a single line of
`RedisDistributedLock` runs. No `catch` in the adapter can see it, because the adapter
does not exist yet. The multiplexer is now built with `AbortOnConnectFail = false`, so
`Connect` returns an object that retries in the background and individual commands fail
with something the adapter *can* translate. The general lesson: an adapter can only
absorb failures that happen after it is constructed, so a component's own construction
is part of its failure surface.

**A second bug fell out of the same review.** Both handlers released their locks from a
`finally` using the request's `CancellationToken`, and the adapter's release begins with
`ThrowIfCancellationRequested()`. A client hanging up mid-request therefore cancelled
the release and stranded the lock, holding everyone else off that seat until the TTL
expired — on the one path where releasing promptly matters most. Releases now pass
`CancellationToken.None`: the write has already happened by then, and cancelling the
cleanup cannot un-happen it.

`LockOutcome.HeldByAnother` is zero so that a default-constructed `LockAcquisition`
grants nothing. With `Acquired` at zero, `default` would have read as ownership while
carrying no token.

**The policy, which is the actual decision:**

| Lock | `HeldByAnother` | `Unavailable` |
|---|---|---|
| Seat | proceed — `xmin` settles it | proceed |
| Client + event | **refuse**, retryably | proceed, accept the breach |

The two rows differ because what sits behind the two locks differs, and nothing else.
Refusing on a contended client lock is not pessimism; without it the cap is decorative.
Proceeding on an unavailable one is not laxity; it is 006's asymmetry stated in code —
a breached cap is a refund email, and refusing every hold in the system because Redis
blinked is an outage. The no-double-sell invariant is unaffected in every cell of that
table, because it never depended on the lock.

`HoldSeatOutcome.ConcurrentRequestInFlight` carries the refusal. It is the only refusal
in either enum caused by the system rather than by the seat, which is why it is
retryable and why the name says what actually happened rather than "busy".

**The cost, stated plainly.** A client firing several holds at once now gets most of
them refused rather than served, because the lock does not wait. A burst of twelve
yields as few as one hold; a client that retries converges on four. That is a worse
burst experience than before and a correct one, and both integration tests pin it —
one proves the ceiling, the other proves a retrying client actually reaches it, since
"no more than four" is also satisfied by granting one.

<!-- Expand later: whether a bounded wait on the client lock would recover the burst
     experience without weakening the cap, once the load test shows how often it is
     contended in practice. Also whether ReleaseAsync returning false should
     distinguish "not mine" from "could not reach Redis" — nothing needs it yet. -->

---

## 011 — Every seat command carries its event id, checked and never trusted

`HoldSeatCommand` needed an event id for the cap (006). `SellSeatCommand` and the new
`ReleaseSeatCommand` now carry one too, and all three refuse with `SeatNotFound` when it
disagrees with the seat.

The seat id alone identifies the seat, so this is redundant data by one reading. It is
not symmetry for its own sake. Once the routes are
`/events/{eventId}/seats/{seatId}/...`, a command without an event id leaves two bad
options: drop the segment from the route, so buying a seat through *any* event's URL
works and the URL is decorative — an API that lies — or check it in the endpoint, which
puts rule enforcement above the aggregate and is the thing `CLAUDE.md` forbids more
firmly than it forbids a redundant field.

The refusal is deliberately indistinguishable from "no such seat". Telling a caller
"that seat exists, but not here" would let anyone enumerate seat ids by asking about an
event they cannot see. `HoldSeatOutcome.SeatNotFound` already documented that property;
this gives it to the other two paths, where it was simply absent.

<!-- Expand later: whether the event id belongs on the command at all once a read model
     can resolve seat → event cheaply, or whether the check is worth the column either
     way as a guard against a mis-wired client. -->

---

## 012 — Seats are created in bulk, by a use case, with generated ids

`CreateSeatMapCommandHandler` is the first production caller of `Seat.Create`. Until it
existed, seats could only be brought into being by test fixtures, which meant the
running system had nothing to sell.

This answers 005's open question — "whether seats are ever created individually or only
in bulk as part of an event's seat map" — with **the aggregate is per seat, the use case
is bulk**. `Seat.Create` is unchanged and still makes exactly one seat; the handler
loops and the repository writes the batch in one `SaveChangesAsync`. A venue gets a
layout in a single act, and half a seat map is not a smaller venue, it is a broken one
somebody has to reconcile by hand.

Two absences are deliberate. **No lock**: the other three use cases take one because
they contend for a row that already exists, and this one writes rows that do not, so
there is nothing to contend for and no concurrency token to lose. Taking one anyway
would be cargo-cult symmetry. **No outcome enum**: nothing in the domain can refuse a
new seat — every seat is born `Available` — so the only failure is a nonsense count,
which is a malformed request rather than a refusal and throws instead of returning
something every caller would have to branch on.

Ids are generated and returned rather than supplied. Seats have no natural key, and a
caller that has just created a seat map needs something to hold.

`MaxSeatsPerRequest` is 10,000: comfortably above a real arena, low enough that a typo
cannot ask Postgres for a million rows in one transaction.

<!-- Expand later: whether a seat map should carry structure — rows, sections, price
     bands — or stay a flat count until Catalog owns venue layout. -->

---

## 013 — Migrations are explicit by default and automatic only in development

Nothing applied migrations, so `dotnet run` against a fresh `docker compose up` gave an
API pointed at a database with no `inventory.seats`.

The fix is **both** options, not one. `dotnet ef database update` stays the real path:
schema change is a deliberate act, and an application that rewrites the database as a
side effect of booting is a bad thing to own in production even on the days it works.
On top of that, `InventoryMigrator` runs at startup when `Inventory:MigrateOnStartup`
is set — which the run profiles set and nothing else does.

It implements `IHostedLifecycleService` rather than `IHostedService`, and that is the
detail that makes it correct rather than merely convenient. Hosted services start in
registration order, and the web host's own is registered before any module's, so
`StartAsync` here would run *after* Kestrel began accepting connections — a window in
which a request can reach a table that does not exist yet. The host calls
`StartingAsync` on every lifecycle service before `StartAsync` on any, so migrating
there is strictly before the socket opens.

It is registered inside `AddInventoryModule`, not in `Program.cs`. The host keeps zero
package references and keeps not knowing that Inventory has a database at all — the
same argument that put `InventoryDbContextFactory` in the module. And the module reads
a configuration flag rather than `IHostEnvironment`, because which environment this is
happens to be the host's business, not Inventory's.

The flag lives in `launchSettings.json` rather than `appsettings.Development.json`,
which `.gitignore` excludes — the convenience would otherwise not survive a clone.

Same change, because it is now-or-never: `__EFMigrationsHistory` moves from `public`
into the `inventory` schema. EF's default left Inventory's tables self-contained while
its record of *which migrations had run* sat in a schema shared with every module, so
extracting Inventory — the thing the module seam exists for — would have meant unpicking
one table out of a shared one. Moving it later would need a data migration; moving it
now needs `docker compose down -v`.

`InventoryPersistence.UseInventoryNpgsql` exists so the three callers that configure a
context — DI, the design-time factory, and the integration tests — cannot disagree about
that history table. A disagreement there is the nastiest kind available: each would read
a different table to decide what had been applied, so the tooling and the running app
would hold different beliefs about the schema and neither would report an error.

<!-- Expand later: whether the migrator should also gate on a health check once there is
     more than one instance, and whether Catalog/Orders/Payments get their own migrators
     or one shared host-level step. -->

---

## 014 — Inventory's HTTP surface: actions, one status rule, and a claimed identity

**Actions, not resources.** The routes are `POST .../hold`, `.../release` and
`.../purchase`, not a `/holds` collection. A hold is not an entity — it is the
`HeldByClientId` / `HoldExpiresAt` pair on the seat row (005, 007) — and minting a URI
for it to look more RESTful would contradict the aggregate's central design choice at
the front door. All three are idempotent, which is what makes retrying a POST safe here,
and that is a property of the aggregate rather than an accident of the endpoints.

**One status rule: the code carries the class of failure, a `reason` member carries
which one.** Every refusal about the state of the world is `409`; only "no such seat" is
`404`. Scattering refusals across distinct codes would make clients branch on status and
freeze those statuses forever; a machine-readable `reason` string plus a `retriable`
flag is the thing worth promising, and `retriable` exists because under flash-sale load
most refusals are ordinary rather than faults and a client should not keep its own list
of which ones are worth another attempt.

`HoldCapReached` is 409 rather than 403 or 429. 403 would imply an authorisation
decision, and there is nothing authorising; 429 is about request *rate* and would want a
`Retry-After` that cannot be computed, since it depends on when one of the client's own
holds lapses. It is a conflict with the state of that client's basket.

**`X-Client-Id` is a claimed identity, not authentication**, and the code says so.
Anyone can change the header and reset their cap. It is a deliberate stand-in for the
Identity module that does not exist, so the seat use cases can be exercised end to end
without inventing an auth story first — which is also why no route returns 401 or 403.
It arrives through a group endpoint filter rather than a bound parameter: a failed
parameter bind yields a framework 400 with an empty body, which is precisely the
response nobody can diagnose from a load harness, and a group filter cannot be forgotten
by the next endpoint added.

**The mapping lives in `SeatResults`, away from the endpoints.** Eighteen responses
decide what a client sees, and a wrong one is invisible until somebody hits it. Extracted,
all of them are unit-testable in microseconds with no host and no package; left inline,
proving the same thing would need a `WebApplicationFactory` and containers. Every switch
is exhaustive with no default arm, so adding an outcome breaks the build — which is the
entire reason those enums are closed sets.

**No OpenAPI.** Both Swashbuckle and `Microsoft.AspNetCore.OpenApi` are packages, and
`Encore.Api` holds zero `PackageReference` items on purpose (002's argument, applied to
the host). `ProblemDetails` and `TypedResults.Problem` are in the shared framework, so
structured errors cost nothing. The two pieces of middleware added —
`AddProblemDetails` and `UseExceptionHandler` — are framework too, and earn their place
by making every response one shape rather than problem+json from the modules and empty
bodies from the framework.

<!-- Expand later: whether a GET for seat availability is worth adding once something
     needs to browse rather than act, and whether the reason strings want to become a
     documented, versioned vocabulary once a real client depends on them. -->

---

## 015 — The test suite runs in a container, because the host will not run it

`dotnet test` on the development machine fails to load `Encore.Shared.dll` with
`FileLoadException ... 0x800711C7`. This was written off as an intermittent
environment gotcha twice. It is not intermittent, and it is worth stating what it
actually is, because the shape of the fix follows from the diagnosis.

Windows Smart App Control is a Code Integrity policy — `VerifiedAndReputableDesktop`
— and on this machine it is in **enforcement** mode, not evaluation
(`VerifiedAndReputablePolicyState = 1`). It admits code by one of two routes: a
signature from a publisher it trusts, or a vouch from Microsoft's cloud reputation
service. A freshly compiled assembly is unsigned, so route one is closed by
construction (`TotalSignatureCount = 0`, `PublisherName = Unknown`). Route two
returned nothing at all — `DefenderCloudCallRequested = true`,
`DefenderMadeCloudCall = false`, `DefenderCloudHTTPCode = 0x0`. Requested signing
level 2, validated 1, deny.

Three facts about that make it worse than it first looks:

**It is not a malware verdict.** `IsUnfriendlyFile = false`, no threat name, no
engine report. Defender did not decide the assembly was bad; it never decided
anything. Under enforcement, the absence of an answer *is* a refusal.

**Rebuilding cannot help, and the old advice to delete `bin`/`obj` was wrong.**
Reputation is keyed on file hash, so every rebuild produces a binary that has never
been seen and needs its own verdict from the mechanism that is not answering.
Deleting build output, building Release, publishing outside the repo and forcing a
new hash were all tried; all four failed identically, and there was never a reason
for any of them to work.

**Smart App Control has no exclusion mechanism.** No per-file, no per-folder, no
per-process. Defender's exclusion list does not apply to it. Turning it off is
one-way — Microsoft's design requires a clean Windows install to re-enable it.

So the fix cannot be local to the code, and the remaining honest options were sign
the assemblies with an OV/EV certificate, disable a security feature permanently, or
run the tests somewhere the policy does not apply. A Linux container is the third,
and it is the only one of the three that is reversible, costs nothing, and leaves
the machine's security posture alone.

The side benefit is the part worth keeping once the host problem is gone: the suite
now runs identically on any machine with Docker, and nobody has to discover this
diagnosis before they can run the tests.

**Sibling containers, not Docker-in-Docker.** The integration tests use
Testcontainers, so the test container needs a daemon. It gets the host's socket
rather than a nested daemon: no nested storage driver, no privileged container, and
the containers the tests spin up are siblings on the daemon the developer already
has running. The cost is one piece of knowledge the arrangement cannot infer —
sibling containers publish their ports on the *host*, so `localhost` inside the test
container is the wrong address and the connection is refused.
`TESTCONTAINERS_HOST_OVERRIDE` is what closes that gap.

**Behind a compose profile.** `docker compose up -d` still means "bring up the
dependencies I develop against" and cannot accidentally start a test run;
`docker compose run --rm tests` is the entire interface. The service deliberately
does not `depends_on` the postgres and redis services: those exist for `dotnet run`,
and the integration tests bring up their own throwaway instances. A test run that
used the developer's database would be a test run that could corrupt it.

**Source is copied into the image, not bind-mounted.** A bind mount would put
Windows `bin`/`obj` in front of a Linux build and produce failures that look like
code problems — which is precisely the class of confusion this entry exists to end.
Project files are copied and restored as their own layer, so editing source rebuilds
without re-resolving the package graph.

The host path is not abandoned: `dotnet build` still works on Windows, and
`dotnet test` still works for the subset of tests that never load the blocked
assembly. The container is what makes a *complete* run possible.

<!-- Expand later: whether CI should run this same image rather than a bespoke
     workflow, and whether the Api image should share the restore layer once there
     is something to deploy. -->

---

## 016 — Money is a decimal and a currency code, not a value object

`Event.Price` is a `decimal`, `Event.Currency` is a three-character string, and there is
no `Money` type. Postgres stores the amount as `numeric(19,4)`.

A value object earns its keep by *preventing* something — adding dollars to euros,
losing a currency on the way through a calculation, rounding at the wrong step. Nothing
in Catalog or Orders does arithmetic on money beyond summing an order's lines, and both
operands of that sum are the same currency by construction, because an order is for one
event and an event has one price. So `Money` would prevent nothing while costing an EF
owned-type mapping and a JSON-shape decision on every DTO that carries a price. That is
the ceremony 001 argues against, and the same reasoning that keeps these modules flat.

When money arithmetic gets hard — tax, fees, discounts, more than one currency in a
basket — `Money` arrives then, in whichever module owns the arithmetic. Not before, and
not on speculation.

**The column type is not a detail.** Never a floating-point type: it cannot represent
most decimal fractions exactly, so a summed total drifts from what the customer was
shown, and it drifts silently. Never the Postgres `money` type either — its scale is a
database-wide setting and its rendering is locale-dependent, so the same column means
different things on two servers. `numeric(19,4)` rather than `(19,2)` leaves room for
prices that are not whole cents without reopening the question later.

**Currency is per event, deliberately, and there is no system-wide default.** No
currency for this system is written down anywhere, and picking one silently would be
inventing a rule. Carrying it per event also makes the no-conversion property structural
rather than assumed: one event per order means one currency per order, so nothing in
this system ever converts, and no mixed-currency order is representable.

<!-- Expand later: whether a price of zero should be legal for a free event, and
     whether the currency code is worth validating against a real ISO 4217 list
     rather than a length check. -->

---

## 017 — One migrator per module, not one step in the host

This answers 013's open question — "whether Catalog/Orders/Payments get their own
migrators or one shared host-level step" — with **one each**. `CatalogMigrator` is a
copy of `InventoryMigrator` with the nouns changed, registered inside
`AddCatalogModule` behind `Catalog:MigrateOnStartup`, and Orders gets its own.

The obvious objection is right: this is roughly sixty near-identical lines per module,
and the instinct is to factor it into a generic `ModuleMigrator<TContext>`. Three
reasons not to.

**A host-level step would cost `Encore.Api` its zero-package property.** It would have
to name every module's `DbContext`, which means the host takes a reference on EF Core
and, having taken it, learns that modules have databases at all. 014 refused OpenAPI
over that same property; it would be strange to keep the host pure against Swashbuckle
and then hand it Entity Framework.

**It saves no coordination.** Each module already owns its own `__EFMigrationsHistory`
in its own schema (013), so there is nothing to sequence between them. A shared step
would still enumerate one context per module; it would only move that list somewhere it
does not belong.

**A module carries its migrator with it.** The day Inventory becomes its own service,
its migrator goes too, and nothing in the monolith needs unpicking — the same reason 013
moved the history table out of `public` in the first place. A host-level step is
precisely the thing that would need unpicking.

The shared-helper version was considered and rejected twice over. Putting
`ModuleMigrator<TContext>` in `Encore.Shared` would put EF Core into the assembly
`Inventory.Domain` references, which is the purity rule broken through the back door —
and `ENCORE001` would not catch it, because it only inspects `PackageReference` items.
A separate `Encore.Modules.Shared.Persistence` project would avoid that, but it is a new
project to save a hundred-odd lines, and it couples three modules' bootstrapping
together, which is the opposite of a seam.

Duplication is the price of the seam here, and it is cheap because the duplicated code
is inert: it has no rules in it, and a bug in one copy cannot be a bug in another.

<!-- Expand later: whether the run profiles should keep three separate
     MigrateOnStartup flags or one, once there are three modules setting them. -->

---

## 018 — Catalog's HTTP surface, and two rules it settles for everyone

Catalog gets CRUD, because that is what it is: `POST` and `GET` for venues and events,
under `/catalog`. No client identity on any route — a catalogue is public to read, and
creating a venue is operator-facing, the same footing as `POST /events/{eventId}/seats`,
which also carries no filter. When Identity exists, the write routes are the ones that
grow an authorisation check.

**The prefix is `/catalog`, even though Inventory already owns `/events/{eventId}`.**
The same event id therefore appears in two URL shapes: `/catalog/events/{id}` describes
the show, `/events/{id}/seats/...` acts on its seats. That is genuinely a little odd,
and the alternative — Catalog serving `/events` directly — is worse in a way that lasts
longer: two modules writing into one route namespace, so extracting either means
redesigning URLs rather than moving a routing rule. A prefix keeps the seam visible at
the URL, which is where a reader looks first.

Two rules settled here apply to every module that follows.

**A body naming something that does not exist is 409, not 404.** Posting an event whose
`venueId` matches no venue is refused `409 venue_not_found`. 404 is an answer about the
resource that was *addressed*, and `/catalog/events` was addressed perfectly well; it is
the world the body describes that is not there. Reserving 404 for the URL keeps it
meaning one thing. This is 014's status rule applied to a case 014 did not have: a
refusal about the state of the world is a conflict.

**An instant with no timezone is refused, not guessed.** `System.Text.Json` yields
`Unspecified` for a timestamp with neither a `Z` nor an offset, and Npgsql rejects a
non-UTC value for `timestamptz`, so left alone this arrives as a 500 from inside the
provider. The fix is not to assume UTC. A wall-clock time with no zone is a different
instant in London and in Los Angeles, and a show that goes on sale at the wrong one is
wrong in a way nobody notices until the day. `400 ambiguous_timestamp` says exactly what
is missing. This is what `CLAUDE.md`'s "time enters the system at exactly one place and
is converted once" looks like at the edge.

400s carry a `reason` too, which 014 did not explicitly say. The principle it stated —
the status names the class of failure, the reason names which one — is as useful for a
malformed request as for a refused one, and a client that already switches on `reason`
should not need a second mechanism for the 400 case.

`CatalogResults` is deliberately **not** the twin of `SeatResults`. That class exists to
switch exhaustively over closed outcome enums; Catalog has no use-case layer and no
enums, so there is nothing to switch on. What is left is worth centralising anyway — the
`reason` / `retriable` / `instance` shape — and the file says so, so nobody completes it
into a mapper that has nothing to map.

<!-- Expand later: whether the list endpoints need paging before anything real
     depends on them, and whether currency should be validated against an actual
     ISO 4217 list rather than a three-letter shape check. -->

---

## 019 — Catalog gets a contracts assembly too, and why that is not ceremony

Orders needs an event's price. It must not reference `Encore.Modules.Catalog` to get it,
because that would put `CatalogDbContext` and `Catalog.Models.Event` on Orders' compile
surface, and the first person who needed one more field would write a cross-schema join.
So Catalog gets `Encore.Modules.Catalog.Contracts` — an interface, a request, a response
and a closed status enum, zero packages, zero project references — plus an in-process
adapter, exactly as Inventory has.

The objection is fair and worth answering rather than waving away: 001 spends its whole
length arguing that these modules should not pay for abstraction, and this is
abstraction. But 001's objection is precise. It is against "three folders and an
interface to express *save this row*, with no invariant being protected and no
substitution ever actually performed". This fails all three clauses.

**The substitution is really performed, twice.** In tests, a fake `IEventPricing` is what
lets Orders' checkout tests run against one schema instead of two — without it every
Orders test needs Catalog's tables migrated alongside its own. And at extraction, a
read-mostly catalogue is the single easiest thing in this system to put behind a replica
or its own service, which is the optionality 001 says to pay for only where it will be
spent. Here it will be.

**An invariant is protected: the module boundary itself.** That is not a rule about seats
or prices, but it is the rule this repo is most about, and it is the only one with no
other enforcement mechanism. A project reference is forever; a comment asking people not
to use it is not.

**It is smaller than the thing it copies.** Four files against Inventory.Contracts' ten,
and the adapter is one projection.

The deciding argument is consistency. The repo has already made this exact call, for this
exact reason, one commit ago. Doing something different for the second inter-module call
— a direct reference, or an interface parked in `Encore.Shared` — would cost a reader
more in confusion than four files cost in ceremony. `Encore.Shared` in particular is the
wrong home: it is for contracts every module agrees on, `Inventory.Domain` references it,
and letting each module's public face accumulate there turns it into the back door 002
exists to keep shut.

The interface is named `IEventPricing` rather than `ICatalogQueries` so it can only grow
in one direction. A name that describes the owner invites every future question about the
catalogue to land on it; a name that describes the need does not.

<!-- Expand later: whether Orders should cache a price for the life of a checkout
     rather than reading it once, if the in-process call ever becomes a remote one. -->

---

## 020 — The contracts assembly may carry one number

`Encore.Modules.Inventory.Contracts` now holds `SeatReservationLimits.MaxHoldsPerClientPerEvent`,
and `HoldSeatCommandHandler` forwards to it. That is the first value, rather than a
shape, to appear in a contracts assembly, so it is worth saying why it is allowed and
what would not be.

Orders composes multi-seat checkouts, and the cap is four. Without a published limit its
only options are to hard-code 4 — a second copy of a rule, wrong the moment the number
changes — or to send five holds, be refused on the fifth, and compensate the four that
succeeded. That is four writes and four compensating releases, during a flash sale, to
learn a number that was never a secret. The README states it and every
`hold_cap_reached` response already ships it as `limit`.

So this publishes nothing; it deletes a duplicate. That is the test for anything else
that wants to live here: **a value belongs on the contract when a caller must know it
*before* acting, and when it is already observable afterwards.** A value that a caller
could only use to re-derive a decision Inventory owns does not belong here — the cap
itself is still judged by Inventory, against Inventory's clock and its own count of live
holds, so a caller reading this can size a request but cannot conclude it will succeed.
Seats held from another tab still count.

**It is a `static readonly` field, not a `const`.** A `const` is copied into the
consumer's assembly at compile time, so an Orders built against 4 would keep sending 4
after Inventory moved to 6, with nothing to notice. That is a curiosity while both live
in one process and a real bug the day `ISeatReservations` is served over HTTP — which is
the scenario this assembly exists for. The handler keeps the old name as a forwarder so
`SeatResults` and the existing tests do not churn.

<!-- Expand later: whether the hold duration should be published the same way once
     something outside Inventory needs to show a countdown. -->

---

## 021 — The order state model, and who is allowed to decide an order has expired

An order is `Pending`, and then it is `Confirmed`, `Cancelled`, `Expired` or `Failed`.
Those four are terminal. Nothing else exists, and in particular there are no payment
statuses — Payments has no behaviour yet, and inventing its vocabulary now would be
guessing. `Confirmed` is the status that splits when it arrives.

`Expired` is kept apart from `Cancelled` for exactly the reason 007 keeps
`SeatReleased(Expired)` apart from `(Cancelled)`: "your hold ran out" and "you changed
your mind" are different things to tell a customer, and order history cannot be
backfilled once the distinction has been thrown away. `Failed` is the one status that
means a person has to look — some seats sold and others did not, or a refusal that was
not expiry. There is no automatic recovery from it because there cannot be one: a sold
seat is terminal, so nothing can un-sell the half that worked.

### Orders records the expiry. Inventory decides it.

This is the sharpest rule in the module, and the one most likely to be "fixed" by
somebody later.

`Order.HoldsExpireAt` is the earliest `HoldExpiresAt` Inventory returned across the
order's holds. It is **copied, never computed** — Orders does not know the number five,
the same way it does not know what a seat costs until Catalog tells it. The earliest
rather than the latest, because an order needs all of its seats, so the first hold to
lapse is when the order stops being completable.

From that follow three prohibitions:

**Orders never refuses a confirm because `HoldsExpireAt` has passed.** It asks Inventory
and lets `SellSeatStatus.HoldExpired` be the thing that moves the order to `Expired`. Two
copies of the expiry rule, judged against two clocks, is a system that can tell a
customer their hold has gone while the seat is still theirs.

**`GET /orders/{id}` returns the stored status and does not derive expiry on read.**
Deriving would be Orders deciding, and it would also be wrong in the other direction: a
seat released early makes `HoldsExpireAt` optimistic. The field is returned so a client
can show a countdown; the status is returned because it is the fact.

**Orders never releases seats because a hold lapsed.** Inventory reclaims lapsed holds
itself, lazily on every read and write path. A release from Orders would be a second
authority over the same rule, and it would fire against Orders' clock.

**No Orders background sweep is built.** Inventory's own sweep is still Phase 7, and
Orders getting one first would be backwards. 007's criterion — "if a test cannot pass
with the sweep disabled, the sweep has become load-bearing and the design is broken" —
is satisfied here by construction, because no sweep exists to lean on. A stale `Pending`
row is untidy, not incorrect: it is resolved the moment anybody touches the order.

The test that pins all of this is
`Confirm_WhenHoldsLapsed_ShouldAskInventoryRatherThanItsOwnClock`: a fixed clock far past
`HoldsExpireAt`, a fake Inventory that reports `Sold`, and an assertion that the order
**confirms**. The day somebody adds an expiry check to Orders, that test fails.

### Three smaller calls

**`orders.orders` carries `xmin`.** Orders was called a table nobody contends for, and
mostly that is true — but confirm and cancel arriving together, which is one impatient
double-click, really do race this row, and without a token the loser can write
`Cancelled` over an order whose seats are already `Sold`. Inventory protects the seats
regardless, so this guards Orders' own record rather than an invariant. Three lines of
configuration and one `catch`, using a mechanism already in the house style.

**`CustomerId` became `ClientId`.** The stub's TODO said customer. Everything else in
this system — `X-Client-Id`, `HeldByClientId`, the hold cap — says client, and Identity
does not exist, so "customer" would name an entity nothing can produce.

**`OrderLine.Description` was dropped.** `EventId` and `SeatId` render an order, and a
copied event name is a second copy of a truth that can drift — the argument
`SeatActionResponse` already makes. Price is the exception and must be copied, precisely
because drifting is the thing it must not do.

<!-- Expand later: whether Confirmed splits into Paid/AwaitingPayment when Payments
     arrives, and whether Failed deserves a stored reason rather than only a log line. -->

---

## 022 — Orders' HTTP surface, and the service that has no port

Four routes under `/orders`, all carrying `X-Client-Id`: `POST /` opens a checkout,
`GET /{id}` reads one back, `POST /{id}/confirm` and `POST /{id}/cancel` end it.

**Confirm and cancel are actions, not a status a client may PATCH.** 014 made the same
call for seats and the reason carries further here: the set of endings is closed, and the
rules for reaching each one are not the client's to apply. A writable status field would
invite a client to declare an order `confirmed` without a single seat having been sold,
and the endpoint would have no principled way to refuse.

**Orders sends the holds.** A checkout prices the event, takes a hold on every seat, and
only then writes a row. The alternative — the client holds seats itself and hands the ids
to a checkout that trusts them — moves the composition into the browser and leaves this
module unable to tell a hold it caused from one it did not. It also makes
`SeatReservationLimits` pointless, since the thing sizing the request would no longer be
the thing that can read the limit.

**The checks are ordered by what they cost.** Everything knowable from the request alone
comes first, then Catalog, then the open-checkout read, and holds last. Holds are writes
against the hottest rows in the system; taking four of them before discovering the event
does not exist would be paying the worst price in the system for a typo.

**`CheckoutService` takes `OrdersDbContext` concretely, and there is no
`IOrderRepository`.** This is 001 applied honestly rather than selectively. A port earns
its place where it buys substitution, and nothing here will ever be substituted. The two
dependencies that *are* interfaces — `IEventPricing` and `ISeatReservations` — are
interfaces because they cross a module boundary and will one day cross a process one,
which is a different argument that this module does not get to borrow for its own
storage.

The bill arrives in the tests, and it is the right bill to pay. `CheckoutService` cannot
be unit tested without a database, so its tests run against real Postgres in
`Orders.IntegrationTests` while `Orders.UnitTests` holds only the HTTP mapping. That is
the visible cost of declining the port, and it is smaller than the cost of an abstraction
with one implementation forever. `EntityFrameworkCore.InMemory` was considered and
rejected: it does not enforce a partial unique index, so it would fake away the single
most load-bearing line in this module's schema.

**The service sits at the module root, not in an `Application/` folder.** Orders is flat
and has exactly one service. Catalog already set the precedent — its one adapter lives in
`Data/` rather than in a fourth folder built to hold a single query.

<!-- Expand later: whether the checkout should take a payment intent at the same time
     once Payments is real, and whether GET /orders wants a list endpoint before
     anything needs to browse. -->

---

## 023 — A partial checkout writes nothing, releases nothing, and tells you everything

Four seats are asked for and the third is gone. The seats that were held stay held, no
order row is created, and the response names every seat that failed with Inventory's own
reason for each.

The customer can then buy the rest or pick a replacement, and either one is just another
`POST /orders`. Re-holding the seats they already hold is free and safe:
`ISeatReservations` promises that holding a seat this client already holds succeeds
without moving the expiry.

**The alternative was to create the partial order and let it be amended**, and it loses on
three counts. `orders.orders` allows one `Pending` order per client per event, so a second
checkout would be refused and "choose another seat" would need a new route that mutates an
existing order. That route would make `Order.Total` — documented as snapshotted at
checkout — false, and would raise a question nothing has answered: does adding a line
re-read the price? If it does, one order can hold two prices for the same event, which is
the drift `OrderLine` exists to prevent. If it does not, the order pins a price read at an
arbitrarily earlier moment. And a mutable order gives confirm and cancel a third operation
to race.

**The holds are not compensated, and that is the part worth defending.** Releasing the
seats that succeeded would be tidier, and would cost the customer the two good seats they
just got, during a flash sale, because a third seat they can trivially swap was taken. 020
already made this argument in the other direction: four writes and four compensating
releases to learn something is not a price worth paying when the alternative is telling
the caller the truth.

Nothing is left behind. The partial selection lives in the client, and holds the customer
abandons lapse in five minutes through the lazy expiry Inventory already does on every
read and write path. There is not even a stale `Pending` row to sweep.

The cost, stated plainly: a client has to remember what it holds between attempts. That is
a client keeping its own basket, which is a thing clients do.

If this is ever reversed, reversing it is additive — an amend route can be added and this
behaviour kept for the no-order case. The other direction would mean deleting a route
clients had started using.

<!-- Expand later: whether the response should suggest alternative seats rather than only
     naming the unavailable ones, once anything knows the shape of a seat map. -->

---

## 024 — The client id filter is copied into Orders, not shared

`ClientIdEndpointFilter` now exists twice, once in Inventory and once in Orders, with
about forty lines duplicated between them. Promoting it to a shared home was the obvious
move and is the wrong one.

**`Encore.Shared` holds zero `PackageReference` items, and that is load-bearing.**
`Inventory.Domain` references Shared and is required to stay free of infrastructure; an
`IEndpointFilter` would pull `Microsoft.AspNetCore.App` into Shared and therefore into the
domain's transitive reach. The `ENCORE001` guard would not catch it, because it inspects
direct package references and this would arrive through a project reference — which is
exactly the known gap, and a poor thing to go walking into deliberately.

**019 already ruled on what Shared is for**: contracts every module agrees on, not a
drawer for whatever two modules happen to have in common. A third home — an `Encore.Web`
or similar — would be a new project carrying one class, which is the ceremony 001 exists
to refuse.

So it is copied, and the duplication is the cheapest of the three options. What makes it
safe is that the two copies are not really one thing: each module decides for itself which
of its routes claim an identity, and the day Inventory is extracted its copy leaves with
it rather than becoming a dependency the monolith still has to serve.

**The item keys differ, and that is not incidental.** Inventory stores under
`Encore.Inventory.ClientId` and Orders under `Encore.Orders.ClientId`. Both filters run in
one host against one `HttpContext`; a shared key would work perfectly right up until a
route carried both filters, and then it would work by accident.

<!-- Expand later: whether this collapses into one real authentication filter when
     Identity arrives, which is the event that would make the duplication genuinely
     wasteful rather than merely repeated. -->

---

## 025 — A repeated seat id is refused, not de-duplicated

`POST /orders` with `[A, A, B]` is `400 duplicate_seat`. An empty list is `400 no_seats`.

De-duplicating was the alternative and it is dishonest in two directions. It makes the cap
check wrong: five ids naming one seat is a request for one seat, and counting before
collapsing would refuse it for being too large, while counting after means the number the
client sent and the number that was judged are different numbers. And it returns an order
with fewer lines than the request had ids, with nothing saying why — the kind of quiet
mismatch that surfaces weeks later as "I asked for three seats and got two".

`OrderLine` already settles the underlying fact: one line per seat and no quantity,
because a seat is a thing you can buy exactly one of. So the only question was whether a
duplicate is the client's mistake or ours to absorb, and 018 already answered the general
form of it — an instant with no timezone is refused rather than guessed, because guessing
is how you stop finding out. A repeated id is a client bug, and failing at the boundary is
how it gets fixed.

Duplicates are judged **before** the cap, so the response names the real problem rather
than a consequence of it.

400 rather than 409 because this is knowable from the request alone, without asking
Catalog or Inventory anything. That is the same line the `too_many_seats` /
`hold_cap_reached` split is drawn on: contradicting a published number is a malformed
request, while being told you already hold four seats is a fact about the world. Both can
happen on one checkout, and they are not the same mistake.

<!-- Expand later: whether the same treatment should apply to a seat id that is
     syntactically fine but belongs to another event, which Inventory currently answers as
     seat_not_found. -->

---

## 026 — The on-sale gate, and why sales stay open after the show starts

Orders refuses a checkout before an event's `OnSaleAt`. Catalog states the window and does
not enforce it, because Catalog has no idea what a checkout is; this is where the
statement becomes a rule.

**It is a lower bound only.** Nothing refuses a checkout because `StartsAt` has passed.
Walk-up sales are real, a show that is half over still has seats worth selling, and an
upper bound would be inventing a rule nobody asked for in order to make the field
symmetrical. `StartsAt` is carried on `EventPricingResponse` so a caller can tell a
customer what they are buying into, and for nothing else.

**A null `OnSaleAt` means on sale now**, not a gate that opened in the year 1. That
distinction is already made in `Event`, and this is the code that depends on it.

`not_on_sale` is `409` rather than `400`: the request is well formed and it is the world
that is not ready, which is 018's rule. It is also the only refusal in this module that is
**retriable** in the honest sense — every other one either needs a different request or
needs something to have changed that the client cannot wait for, whereas this one stops
being true at an instant that is already public.

**The gate is judged against Orders' clock, and that is safe in a way the expiry rule is
not.** 021 forbids Orders judging hold expiry, because two clocks disagreeing there means
telling a customer their seat is gone while it is still theirs. The on-sale gate is the
opposite shape: a few seconds of clock skew opens or closes a sale marginally early or
late, nothing has been lost either way, and there is no second authority to disagree with
— Catalog states the instant and does not enforce it. Enforcing it in Inventory instead
would mean Inventory learning what an event costs and when it sells, which is the coupling
`IEventPricing` exists to avoid.

<!-- Expand later: whether a presale window for a subset of clients belongs here or in
     Identity, and whether the refusal should carry the on-sale instant so a client can
     show a countdown rather than re-reading the catalogue. -->

---

## 027 — Payments arrives, and what that changes about everything else

This entry is the index for the batch that follows. Payments was four stub files and an
`AddPaymentsModule` that returned `services` untouched — the exact ambiguity 004 named as
the problem with this module, a stub wearing a working module's clothes. Three earlier
entries queued it: 004 said it has to exist as working in-process code before Soundcheck
can extract it, 021 asked whether `Confirmed` splits when it arrives, and 022 asked whether
a checkout should take a payment intent.

The answers are 028 through 034. The one that supersedes something previously settled is
this one.

### `Confirmed` splits, and 021's closed set of endings is no longer closed

021 said an order is `Pending` and then `Confirmed`, `Cancelled`, `Expired` or `Failed`,
and that those four are terminal. `OrderStatus.AwaitingCapture` is now a fifth member and a
second non-terminal one: the seats are sold, the funds are held, and the capture has not
gone through. This entry supersedes that half of 021 rather than rewriting it, which is
what the log is for.

**Why not reuse `Failed`.** 021 defines `Failed` as the status that means a person has to
look, and this qualifies. But an operator looking at `AwaitingCapture` should retry a
capture, and an operator looking at `Failed` should apologise — those are different jobs,
and 021's own argument for keeping `Expired` apart from `Cancelled` applies with full
force: history cannot be backfilled once the distinction has been thrown away.

**Why no background job.** `AwaitingCapture` is resolved by the next touch — a retried
`POST /orders/{id}/confirm` retries the capture and nothing else, because the seats are
already sold and asking Inventory again would be round trips against the hottest rows in
the system for an answer nobody needs. A capture-retry sweep would be the pattern 007
forbids: if a test could not pass with it disabled it would have become load-bearing. 021
already settled the same shape for stale `Pending` rows — untidy, not incorrect, resolved
the moment anybody touches the order.

**Appended as 5, never inserted.** `Pending` is pinned to zero because
`ux_orders_client_event_pending` filters on the literal `"Status" = 0`, which no compiler
checks. That index deliberately still filters on `Pending` alone: an `AwaitingCapture`
order's seats are sold, so it should no more hold the one-open-checkout slot than a
`Confirmed` one does.

**No migration.** `Status` is a plain `integer` column with no check constraint, so a new
enum member needs no schema change. Worth stating because the absence of a migration in
this commit looks like an omission and is not.

The cost is real: an order can sit in `AwaitingCapture` indefinitely if nobody touches it,
which is money we are entitled to and have not taken. That is bounded by how often confirms
get retried, and it is the honest price of refusing to build a sweep before Phase 7.

<!-- Expand later: whether AwaitingCapture should carry the capture's failure count, and
     whether the Phase 7 reconciliation job — the one 031 needs anyway — should also sweep
     this. -->

---

## 028 — Authorise, sell, capture — and void when the sale does not complete

A confirm now secures the money, then sells the seats, then takes the money. A sale that
does not complete releases the authorisation.

**The argument is about which resource cannot be recovered.** A sold seat is terminal (007)
and there is no un-sell; money is the one of the two that can be given back. So any design
that sells before the money is secured risks permanent, unrecoverable inventory loss, and
any design that secures money before selling risks — at worst — a refund. The unrecoverable
step goes in the middle, between two that can be undone.

### What was rejected

**Sell first, then charge.** Cheapest, and wrong in the one direction this project cannot
afford. Seats sold to somebody who then fails to pay are gone, and nothing in the system
can bring them back.

**A single charge before selling.** Same ordering, one gateway round trip instead of two,
statuses `Succeeded`/`Declined`/`TimedOut`, and a refund when the sale falls over. Simpler
by a real margin, and plenty of production ticketing runs exactly this. It loses on two
points. The soft one: a customer whose seats vanish sees a charge and a refund on their
statement rather than a hold that quietly lapses. The one that decides it: **a lost
authorisation self-heals and a lost charge does not.** When the gateway times out we do not
know what happened at the other end — an uncaptured authorisation expires by itself, a
stray charge sits there until somebody reconciles it, and there is no reconciliation job
(see 031).

**Payment as a separate step after confirm.** The order reaches something like
`AwaitingPayment` with its seats already sold. Strictly worse than selling-then-charging:
the same exposure, more round trips, and a state in which a customer holds sold seats
having paid nothing.

### What it costs

Two gateway round trips on the hot path instead of one, plus a void path and a
capture-retry path that would not otherwise exist. The gateway is deliberately slow, so
confirm gets slower by one simulated latency. It also adds one order status and three
payment statuses of vocabulary to a system that had none.

**What would make me reconsider.** If the gateway were real and capture-after-authorisation
turned out never to fail, `AwaitingCapture` would be dead weight and single-charge-plus-
refund would be the honest simplification. If confirm latency became the bottleneck under
load testing, the authorisation moves earlier — to checkout, alongside the holds — and
confirm becomes capture-only.

**Where the opposite case is legitimate.** This is the biggest call in the batch and the
case for a single charge is real. I am picking two-phase because this project's subject is
that the seat is the scarce unrecoverable thing, so the payment design should be shaped
around protecting it — but that is a judgement about what the project is for, not a fact
about payments.

### The partial case, which 021 left open

`OrderStatus.Failed` carried the note: "Whether the customer is charged for a partial order
or refunded is a Payments question that does not exist yet." It exists now, and the answer
is **charged nothing**. Every path that does not end in every seat sold voids the
authorisation, including the mixed one. A person still has to look at a `Failed` order —
some seats really did sell and cannot be un-sold — but they are looking at a customer who
has not been charged, which is the better side to fail on.

### A decline does not end the order

Neither does a gateway timeout. The order stays `Pending` with its holds live, because
losing four seats over a typo'd expiry date is not a reasonable thing for this system to
do. Both come back `409` with `retriable: true`, and this is the one place in Orders where
that flag means "try again with something different" rather than "send the identical
request again" — worth knowing, and worth not smoothing over.

<!-- Expand later: whether the authorisation should move to checkout once load testing has
     an opinion about confirm latency, and whether a declined confirm should count against
     anything. -->

---

## 029 — `Payment` has a state machine, and stays in a flat module

`Payment` is constructed through `Payment.Create` and moves only through methods that check
its current state — the shape `Seat` has. It lives in `Encore.Modules.Payments/Models/`
with no `Ports/`, no `Adapters/` and no separate domain assembly.

**005 and 001/002 are separate arguments, and only the second is Inventory-specific.** 005
says aggregates are constructed by factory so that invariants are unbypassable. 001 and 002
say Inventory gets ports, adapters and its own assembly because it has a hard problem whose
mechanism will change and whose rules need testing against a fake clock. Taking the first
without the second is the consistent reading of both, not a compromise between them.

**Why `Payment` is not `Event`, `Venue` or `Order`.** Those have public setters because
they have no local invariants. Every rule about an order spans its row and Inventory's,
which is why `Order.cs` says public setters are its honest shape. A payment's rules are
decidable from one row: only an authorised payment may be captured, captured is terminal,
and the amount never moves after the gateway was asked. `new Payment { Status = Captured }`
would be a hole straight through all three.

**What this costs.** No compiler enforcement. `Payment` sits in the same assembly as
`PaymentsDbContext` and could name an EF Core type; that is precisely what 002 exists to
prevent, and here it is convention only. Acceptable because the real guard is not the C#:
the partial unique index in 030 is what stops a double charge, `xmin` is what stops a
confirm and a cancel disagreeing, and this class is the readable expression of rules the
database enforces. Same division as `Seat` and `xmin`, and as Orders and its one-open-
checkout index.

**No domain events.** `Seat` raises them because hold history has to be reconstructable
(007) and because the outbox will one day carry `SeatSold` out of the module. Nothing
subscribes to a payment — Orders reads the row directly, in process. Events with no
consumer would be the ceremony 001 argues against, and they cost nothing to add the day the
outbox arrives.

**`xmin` here too, for 021's reason.** Payments looked like another module over uncontended
tables, and it is, with the one exception Orders has: a confirm and a cancel arriving
together race this row *towards different answers*. Without a token the loser writes
`Voided` over money that was taken, and the record then says nobody was charged. The guard
in `Void` cannot catch it — both writers loaded the row while it still read `Authorized` —
so only the database can.

<!-- Expand later: whether Payment's transitions want their own test-only assembly boundary
     the day a second flat module grows a state machine, and whether the no-domain-events
     call should be revisited as part of the outbox work rather than after it. -->

---

## 030 — One live attempt per order, and what "live" means

`ux_payments_order_live` is a partial unique index on `order_id`, filtered to
`"Status" IN (0, 1, 2, 4)` — pending, authorised, captured, timed out. It is the real guard
against charging a customer twice, and the read in `InProcessOrderPayments` is a courtesy
that turns the common case into a readable answer instead of a constraint violation. Two
requests can both pass a check; the database has to be the one that says no.

The same shape as `ux_orders_client_event_pending`, deliberately. It is also the same trap:
the filter is a SQL literal that no compiler checks against `PaymentStatus`, so a wrong
value produces a silently different index rather than a build error. `Payment.LiveStatuses`
is the one C# definition, `PaymentTests.IsLive_ShouldMatchTheIndexFilter` pins the pair
together, and `PaymentsSchemaTests` proves the index behaves for all six statuses.

**What is deliberately outside the filter.** `Declined` and `Voided`. Both definitively
moved no money and never will, so neither should stop a customer trying again — with a
different card, which is a genuinely new attempt with a new key and a new row.

**What is deliberately inside it, and is the interesting one.** `TimedOut`. The honest
reading of "the gateway never answered" is "possibly holding funds", so a timed-out attempt
keeps the order's one slot. That is what makes 031 possible.

**The row is written before every gateway call.** A crash between the two leaves a
`Pending` row holding the slot, and the next attempt finds it and asks the same question
under the same key. Calling first and writing afterwards would leave nothing behind, so the
retry would invent a new key — and a new key against a gateway that did receive the first
call is a second authorisation.

<!-- Expand later: whether the idempotency-key index should be partial too, and what
     happens to this filter if a refund status ever arrives. -->

---

## 031 — A timed-out gateway call is recorded, not resolved

When the gateway does not answer an authorisation, the attempt is marked `TimedOut`, keeps
its idempotency key, and keeps the order's one live slot. The order stays `Pending`. A
retry calls `Payment.Retry`, which moves the same row back to `Pending` — same key, same
amount — so the gateway is asked the same question rather than a second one.

**Rejected: treat a timeout as a decline.** Cheap and wrong. It tells a customer their card
was refused when the funds may be held.

**Rejected: reconcile immediately by asking the gateway what happened.** That is the
reconciliation path, and it belongs with the outbox in Phase 7. Building half of it now
means a second authority over payment state with no dispatcher behind it.

**Rejected: a new row per attempt, keyed on `(orderId, attemptNumber)`.** This was the
original plan and it is worse. A second row needs a second key, and the index would have to
exclude `TimedOut` to allow it — which reopens exactly the double-authorisation this is
trying to prevent. Reusing the row is what makes reusing the key structural instead of
remembered.

**That also dissolves the question I expected to be arguing about.** Whether the key should
be derived or random only matters if a retry has to reconstruct it. It does not: the row
carries it. The key is `order-{orderId}-{paymentId}` because that is legible in a gateway's
logs when somebody has to chase a payment by hand, not because anything depends on the
derivation.

**What this leaves open, stated plainly.** Without reconciliation, a timed-out
authorisation that actually succeeded leaves funds held until the gateway expires them. The
idempotency key makes the retry safe; it does not resolve the original ambiguity. This is
the same shape as the missing expiry sweep — recorded, bounded, and closed by machinery
that arrives in a later phase rather than left open indefinitely. It is also the strongest
argument for 028's two-phase design: an authorisation nobody captures costs the customer
nothing, which is not true of a charge.

**A capture that times out is a different thing and is treated differently.** It leaves the
payment `Authorized`, because that is still exactly what is true — funds held, nothing
taken — and writing `TimedOut` there would throw away the gateway reference and make the
retry impossible. Only an authorisation can reach `TimedOut`.

<!-- Expand later: the reconciliation job, when the outbox exists — including whether it
     belongs in Payments or is the first real consumer of the dispatcher. -->

---

## 032 — The simulated gateway, and one simplification worth admitting

`SimulatedPaymentGateway` declines or hangs at configured rates after a configured delay.
Three calls: authorise, capture, void.

**Two rates, not three.** The stub asked for success, failure and timeout rates — a trio
that has to sum to one, and therefore a validation rule and an error message for when it
does not. `DeclineRate` and `TimeoutRate` only; success is the remainder, which cannot be
set wrong and needs nothing to check it.

**It honours an idempotency key within a process.** A repeated authorisation under a key it
has already answered gets the same answer back. Without that, the retry path in 031 would
pass tests it should fail. The memory dies with the process, which is honest — a real
gateway remembers for days, and nothing here should come to depend on a guarantee this one
cannot make. A timeout is deliberately *not* remembered: the whole point of that outcome is
that the other end's state is unknown, so a retry has to be free to land somewhere
different.

**Seeded randomness is locked; unseeded is not.** `Random.Shared` is already thread-safe; a
seeded `Random` is not, and an unsynchronised one under concurrent load returns garbage
rather than a reproducible sequence — which would quietly defeat the only reason to set a
seed.

**The simplification: a capture or a void is never declined.** A real gateway can refuse
one — an authorisation that lapsed or was revoked — but modelling that honestly needs a
real gateway's error taxonomy, and inventing one for a simulator would be guessing at a
vocabulary the way 021 refused to guess at payment statuses. An authorisation granted is
treated here as a commitment that will be honoured or not answered. This is a known gap
rather than a claim about payments, and it is the thing to fix first if a real provider
ever goes behind this interface.

<!-- Expand later: whether the gateway should simulate an authorisation expiring, which is
     the failure mode this design leans on hardest and currently never exercises. -->

---

## 033 — Payments' HTTP surface is read-only

Two routes: `GET /payments/{id}` and `GET /payments?orderId=`, both carrying `X-Client-Id`
and both scoped to it. The stub proposed a `POST /` and it is gone on purpose.

**A client that can charge itself has walked around the order flow entirely.** It could
authorise money against an order it does not own, or against no order at all, and this
module would have no principled way to refuse because it does not know what a checkout is.
Orders drives payment because Orders is the thing that knows what is owed. That is 022's
"confirm and cancel are actions, not a status a client may PATCH", pointed at the other end
of the same flow.

**The cost is real and worth stating.** This module has no HTTP path that exercises its
write side, so its integration tests carry that weight rather than a request in a scratch
file. That is the right place for it — the write side's interesting behaviour is a partial
unique index and a concurrency token, neither of which a hand-sent request would exercise
usefully — but it does mean the module cannot be poked at by hand the way Catalog can.

**Identity, unlike Catalog.** 018 gave Catalog no client filter because a catalogue is
public. A payment is the opposite of public, so every route here carries the header and
filters on it, and a payment belonging to somebody else is `404` rather than `403` — 011's
argument about seat-id enumeration, pointed at money.

**`ClientIdEndpointFilter` is now copied three times.** 024's argument still holds:
`Encore.Shared` holds zero packages because `Inventory.Domain` references it, an
`IEndpointFilter` would drag `Microsoft.AspNetCore.App` in through that door, and
`ENCORE001` would not catch it because it only inspects `PackageReference` items. But forty
lines is cheap and a hundred and twenty is where somebody starts wondering, so: if there is
ever a fourth, the exit is a small web-only shared assembly that `Inventory.Domain` does not
reference — not a drawer in `Shared`.

<!-- Expand later: whether a read of another client's payment should be 404 or simply
     absent from a list, and whether GET /payments wants paging before anything browses. -->

---

## 034 — Cancelling releases the seats

`CheckoutService.CancelAsync` now releases every seat on the order with
`SeatReleaseReason.Cancelled`, and voids any authorisation first. This reverses what a test
in `CheckoutServiceTests` used to pin, and closes an open question the method's own doc
comment has been carrying.

**Why this does not contradict 021.** 021 forbids Orders releasing seats *because a hold
lapsed*. That prohibition is about expiry specifically: a release on those grounds would be
a second authority over a rule Inventory owns, fired against Orders' clock, and it can tell
a customer their seat is gone while it is still theirs. A customer deliberately cancelling
is not a clock judgement. Nothing is being decided here that Inventory also decides.

**The evidence it was always meant to work this way.** `SeatReleaseReason.Cancelled` has
existed since 007 and has had no producer at all — a domain enum member nothing ever
raised. 007 split it from `Expired` precisely because "your hold ran out" and "you changed
your mind" are different things to tell a customer, and until now the system could only ever
say the first.

**Why it matters for the thing this project is about.** During a flash sale, leaving up to
four seats to lapse on their own after every cancellation strands the scarcest resource in
the system for five minutes at a time. The whole argument for Inventory's shape is that
contended seats are the hard problem; deliberately holding them longer than anybody wants
them is the opposite of taking that seriously.

**What it costs.** Releases are writes against the hottest rows in the system, bounded at
four per cancellation. Nothing checks whether one succeeded: a refusal means the hold was
already gone, which is the state being asked for, and Inventory reclaims lapsed holds
lazily on every path regardless.

**The money goes back first, and can refuse the cancellation.** If the void answers
`AlreadyCaptured`, a confirm won the race and the customer has been charged; cancelling
would write an ending that contradicts a completed sale. That returns `LostRace` rather
than `NotPending`, because the order row still reads `Pending` in memory and reporting a
status this method has not read would be a guess. The retry finds the order as it actually
stands.

**Where I am least certain.** This is a product call as much as a technical one. A shop
might reasonably want a cancelled order's seats held briefly in case the customer changes
their mind back, or want cancellation to be undoable — neither of which survives releasing
immediately. Nothing written down says either way, so I have taken the reading that serves
the contended-inventory problem, and this is the entry to argue with if that is wrong.

<!-- Expand later: whether a cancelled order should be re-openable, and whether the release
     should be best-effort-in-the-background once the outbox exists rather than inline on
     the cancel path. -->

---

## 035 — The working agreement changes: build first, explain after

**Effective 2026-09-19.** The first entry here about how the work is done rather than
about what was built. It exists because this log is append-only and this is a change to
a rule every earlier entry was produced under. Rewriting the working agreement without
recording that it changed would leave 001–034 looking as though they came from the same
process, which is the specific kind of quiet history-editing the log's own preamble
forbids.

**What changed.** The old agreement was propose-then-implement: anything touching a rule
or an API shape meant presenting options with trade-offs and confidence levels, then
waiting for explicit confirmation. The new one is build-by-default. Design and
implementation arrive together; a short recap of the decision and the alternative it beat
follows delivery rather than preceding it; comprehension checks and withholding code
pending my own design attempt are gone.

**What stayed, deliberately.** Three things. Decisions are still recorded here, numbered
and append-only. A pattern the problem does not justify still gets pushed back on, and
that push-back is now the *only* thing that ends in a question rather than in code. And
rules already written down stay settled — building faster is licence to decide what is
undecided, not to reverse what is decided.

**Why.** The confirmation gate was priced for a risk this project does not carry. The
expensive mistakes here are structural — flattening Inventory, layering Catalog — and
those are caught by the asymmetry rule, which survives untouched. What the gate actually
blocked was ordinary work, at the cost of a round trip each time.

**What it costs.** Wrong turns now get discovered in review rather than in conversation,
so the cheapest moment to catch them has been given up. The mitigation is that
consequential calls are flagged inline as they are made rather than after the fact, and
that this log carries the reasoning — which makes 035 a bet on the log's quality that
the previous mode did not need to make.

<!-- Expand later: whether the inline flag is actually being read at the moment it is
     written, or only when something has already gone wrong. -->

---

## 036 — Load-In's last gap: the purity guard becomes three rules in one file

`ENCORE001` has been carrying a hole since 002 wrote it, documented in `CLAUDE.md` as a
"known gap" and named again in 017, 024 and 033: it inspects `@(PackageReference)`, which
contains only the items its own csproj declares. Infrastructure arriving transitively
through a `ProjectReference` passed it cleanly. This entry closes that, and two other holes
found while closing it.

**The gap was worse than recorded, in two ways.** First, `FrameworkReference` was never
checked at all — one `<FrameworkReference Include="Microsoft.AspNetCore.App" />` puts the
entire ASP.NET Core surface on the compile surface with no package to show for it. Second,
and this is the one that mattered: **`Encore.Shared` had no guard whatsoever.** Its own
comment called its zero-package state "load-bearing" and named the exact failure mode —
"that dependency would reach the whole solution through the back door" — while nothing
enforced it. Since `Encore.Shared` is the Domain's only `ProjectReference`, it was the
single highest-leverage unguarded file in the repository. The three `.Contracts` csprojs
made the same unenforced claim in prose.

**Three rules, three codes.** `ENCORE001` no direct `PackageReference`; `ENCORE002` no
`FrameworkReference` but the implicit BCL one; `ENCORE003` nothing outside the BCL in the
resolved reference closure. `ENCORE001` keeps its exact text, because the code is quoted in
`CLAUDE.md`, `README.md`, four entries of this log and a source comment — retargeting it to
mean something broader would retroactively falsify all of them.

**Shared, not copied, and this is where 017 cuts the other way.** 017 accepted sixty
duplicated lines of migrator per module because that code is inert: "a bug in one copy
cannot be a bug in another". A build guard is the exact opposite — it *is* the rule, and a
copy that drifts is a project that has quietly stopped being guarded. By 017's own criterion
this one belongs in `Directory.Build.targets`.

**Opt-in by property, not selected by name.** A project sets `EncoreZeroDependency` and the
shared file says what that declaration costs. Name-based selection
(`EndsWith('.Contracts')`) would make enforcement invisible from the csproj and would let a
rename silently change what the build checks. The declaration sits directly under the
comment in each file that already claimed the property, which is the point: four comments
became four checked facts.

**How `ENCORE003` tells the BCL from everything else**, verified against the SDK with
`dotnet build -t:ResolveReferences -getItem:ReferencePath` rather than assumed. BCL
assemblies carry `NuGetPackageId = Microsoft.NETCore.App.Ref`; ASP.NET Core carries
`Microsoft.AspNetCore.App.Ref`; every real package carries its own id; an assembly resolved
from a `ProjectReference` carries none. So "has a `NuGetPackageId` that is not the base
targeting pack" is precisely "came from outside the BCL and outside this repo". It reads
what the compiler is about to be handed rather than what the csproj declares, which is why
one check covers the direct, the transitive and the framework-reference cases at once.

**Rejected: an allowlist over `@(ProjectReference)`.** Cheap, but it sees one hop — a
zero-package project referencing a package-carrying project would still pass — and an
allowlist of names goes stale. **Rejected: parsing `project.assets.json`**, which means
regex over JSON in MSBuild to re-derive what `ResolvePackageAssets` already computed and
handed over as items.

**`Encore.Api` gets `ENCORE001` alone**, via a second property. 014 refused OpenAPI and 017
refused a host-level migrator partly to keep the host free of packages of its own, and
nothing checked it. It cannot take the other two rules: the Web SDK adds ASP.NET Core and
composing four modules brings EF Core in transitively, which is what a host is for.

**Proven by making it fail, not by reading it.** Adding `StackExchange.Redis` to
`Encore.Shared` produces `ENCORE003` on `Encore.Modules.Inventory.Domain`, naming Redis and
its four transitive dependencies — the documented gap, reproduced and caught. A
`FrameworkReference` on `Encore.Shared` produces `ENCORE002` there and `ENCORE003` on the
Domain. This is the scenario 024 declined to create when it refused to promote the shared
endpoint filter into `Encore.Shared`; that argument was never only about the guard, so 024
and 033 stand exactly as written and their triggers are unchanged.

**What it costs.** A build failure now depends on SDK item metadata rather than only on
csproj text, so a future SDK that stopped populating `NuGetPackageId` would break these
builds. That is the right direction to fail in — loudly, on the first build, never silently
passing — and the repo pins one SDK through the test container.

<!-- Expand later: whether the guard should also refuse an InternalsVisibleTo out of a
     zero-dependency project, and whether ENCORE003 wants an allowlist mechanism the day a
     genuine BCL-adjacent package is argued for. -->

---

## 037 — The architecture test, and why it has no architecture-test library

002 left an open note asking how the assembly-boundary approach compares to a NetArchTest
one, and the Domain csproj carried a comment saying an architecture test "can later assert
the same rule at the namespace level". `tests/Encore.ArchitectureTests` is that test, and
the comparison now has an answer: the library was not needed.

**No NetArchTest, no ArchUnitNET.** This repo has no third-party test package beyond xunit —
no mocking library, no fluent assertions, hand-rolled fakes and raw `Assert` throughout.
Nearly every assertion here is an assembly-reference question that
`Assembly.GetReferencedAssemblies()` answers in a line, and the one namespace-level
assertion is three lines of LINQ over `GetTypes()`. A fluent DSL for that would be the
pattern the problem does not justify. The cost, stated plainly: failure messages are only as
good as the ones written by hand, so each assertion pays for a line of message-building.

**The test cannot see what it asserts about.** Every `ProjectReference` carries
`ReferenceOutputAssembly="false"` — build-order edges only, so the outputs never reach this
project's compile surface. A test proving `Encore.Api` has no consumers while itself
consuming `Encore.Api` would be its own counterexample. Assemblies are named as strings and
loaded from disk, located through `AssemblyMetadataAttribute` values injected at build time
rather than by walking up from the test binary looking for a solution file.

**Two mechanisms, because they answer two questions.** `GetReferencedAssemblies()` reports
only what the compiler actually emitted a reference to, so a *declared but unused*
`ProjectReference` is invisible to it — and that is exactly the latent violation someone
will later find and use. So `ProjectGraphTests` parses the csprojs and asserts the declared
graph, while `AssemblyReferenceTests` reads compiled metadata and asserts the real reach
including transitive. Neither subsumes the other.

**One trap worth recording.** Module-boundary assertions compare assembly names exactly,
never by prefix: `Encore.Modules.Inventory.Contracts` starts with
`Encore.Modules.Inventory`, so a prefix test would ban the very seam the rule exists to
permit.

**What it costs.** A twentieth project in the solution, and a suite that fails on a
legitimate architectural change until someone updates it — which is the point, but it does
mean the next module costs a few lines here too.

<!-- Expand later: whether this suite should also assert that no module registers another
     module's services, and whether it is the right home for the endpoint-route conventions
     018 settled in prose. -->

---

## 038 — `Seat.Create` rejects an empty id

Answers the open half of 005's note; 012 answered the other half by settling bulk creation
with generated ids. `Seat.Create` now throws `ArgumentException` if either `id` or `eventId`
is `Guid.Empty`.

**Why it is a real hole and not pedantry.** 005's whole case for the factory is that a seat
cannot be conjured into a state no rule approved. A seat with `Id == Guid.Empty` cannot be
addressed and collides on the primary key with the next one; a seat with
`EventId == Guid.Empty` belongs to no event. Both are exactly such states, reachable through
the one door 005 built to prevent them. The rest of the system already agrees an empty Guid
is not an identity — all three copies of `ClientIdEndpointFilter` refuse one at the edge —
so the factory accepting it was the inconsistency.

**`ArgumentException`, not `SeatTransitionException`.** The transition exception carries a
closed reason enum that `SeatResults` switches over exhaustively with no default arm (014);
adding a member would force an HTTP mapping for a case no HTTP request can produce. 012 set
the precedent: a nonsense seat count "is a malformed request rather than a refusal, so it
throws rather than returning a result the caller would have to branch on".

**The endpoint guard that had to come with it.** `CreateSeatMapCommandHandler` passes the
route's event id straight through, so `POST /events/00000000-.../seats` would have turned
from a bound request into a 500. `SeatEndpoints.CreateSeatMapAsync` now refuses an empty
event id with a 400 in the same shape as its seat-count check, one screen above. The
aggregate's throw stays as the belt behind that brace: the endpoint answers the caller, the
factory answers everybody else.

**What it costs.** Nothing in production can reach it — `CreateSeatMapCommandHandler`
generates the ids. The value is entirely in the seed script, the fixture and the bulk import
that do not exist yet, which is 005's own "tired teammate" argument and the reason a factory
is worth having at all.

<!-- Expand later: whether the same guard belongs on the command records themselves, once
     there is a validation story that is not one hand-written check per endpoint. -->

---

## 039 — `utcNow` must be UTC, and the aggregate is where that is checked

**Chosen rather than found.** `CLAUDE.md` requires `DateTime` with `Kind == Utc` everywhere
and says time enters the system at exactly one place, but nothing checked it anywhere. A
caller passing `DateTime.Now` failed at the Npgsql boundary, several layers from the
mistake, with a provider error about a `timestamptz`. `Seat.Hold`, `Release` and `Sell` now
guard their `utcNow` parameter and throw `ArgumentException` naming it.

**In the aggregate, not at the handler boundary.** The three handlers get their instant from
`TimeProvider.GetUtcNow().UtcDateTime`, which cannot return anything but UTC — a check there
would be tautological where it sits, and would protect nothing from the aggregate's other
callers: the unit tests, a future bulk operation, the sweep when Phase 7 brings it. The
domain receives the clock as a parameter, which makes "this is UTC" a precondition of those
three methods, and a precondition belongs with the method whose contract it is. This is not
a second place where time enters the system; it is the first place that checks what arrived.

**`Unspecified` is refused alongside `Local`.** A wall clock with no zone is a different
instant in London and in Los Angeles, so reading it as UTC would be a guess wearing the
costume of a conversion. 018 refuses an unzoned instant at the HTTP edge for the same
reason; this is that rule at a second edge, not a second rule.

**What it buys.** Every `IDomainEvent.OccurredAt` is now UTC by construction rather than by
convention, because the events are built from this parameter and `HoldExpiresAt` is derived
from it. Nothing downstream needs a check of its own.

**One guard method, not three inline copies** — 017's criterion again, the same way 036
argues it.

<!-- Expand later: whether Payments' own transition methods want the same guard, given that
     `Payment` takes `utcNow` the same way and is protected today only by every caller
     happening to be a handler. -->

---

## 040 — The expiry boundary, the reclaim edge, and what a no-op release leaves behind

Three behaviours the code has always had and nothing asserted. Writing the tests settled the
first half of 007's open note as a side effect, which is this entry's real subject.

**The boundary is exclusive, and now tested on both sides.** `EffectiveStatusAt` reads
`HoldExpiresAt <= utcNow`, so a hold at exactly its expiry instant is over. 007 said so in
prose; nothing tested it, and every test in `SeatTests` sat a full minute away from the
boundary on either side. Flipping that comparison to `<` would have changed behaviour at
exactly one instant and broken nothing in the suite. There are now four tests one tick
apart.

**A client re-holding after their *own* hold lapsed reclaims the seat.** 007 left this open,
calling neither reading settled — reclaim, or refuse as squatting. Reclaim is right, and the
reason is that by the time the question arises the hold has already lapsed, so the seat was
genuinely available to anyone who asked. Refusing only the previous holder would punish one
client for the single outcome the design treats as entirely normal, and would do it using a
rule — "you had it last" — that exists nowhere else in the aggregate. The test now asserts
the event pair by type and order (`SeatReleased(Expired)` then `SeatHeld`) rather than
counting two events, which is what makes the behaviour pinned rather than merely observed: a
count of two is satisfied by any pair at all.

**A no-op release leaves the stale row exactly as it was.** `Release` on a lapsed hold
returns early and deliberately does *not* tidy the columns — the row still reads `Held` by
the old client with an expiry in the past, and the next `Hold` reclaims it lazily. The
obvious guess is the opposite, so this is now a test of its own. Tidying there would give the
release path an opinion about expiry, which is precisely how the background sweep acquires
authority the design says it must never have.

**`Outcome.Refused` in the concurrency test was unreachable, and the assertion accepted it
anyway.** All fifty sessions load before the start gate, so every loser collides on the
write; the refusal arm could never fire, while the assertion read `LostRace or Refused` and
would have gone on passing if the loading strategy silently changed. It now asserts
`LostRace == 49` and `Refused == 0`, and a second test loads *after* the seat is taken so
that refusal is the only legal answer. The pair says one thing: **when you lose depends on
when you read.**

**Not done, and why.** That second test is the tenth copy of the same `PostgreSqlBuilder`
and doubles this class's container startups, because xunit constructs an instance per test.
Sharing containers through `ICollectionFixture` is a change across nine test classes, and
adding one test to one of them is not the trigger. The trigger is a second class needing the
*same* container.

<!-- Expand later: whether the boundary deserves the same treatment in the handler tests,
     where HoldExpiresAt is compared against a TimeProvider rather than a literal. -->

---

## 041 — Correcting 007: `Sell` already tells a non-holder the truth

007's open note says `Sell` "currently says `HoldExpired`, which is untrue for them" when a
caller who never held the seat tries to buy it after someone else's hold lapsed. **That is
no longer true, and has not been for some time.** `Seat.Sell` switches on both the status and
whether the holder is the caller, and answers `NotTheHolder`; the behaviour is pinned by
`SeatTests.Sell_WhenAnotherClientsHoldHasLapsed_ShouldSayNotTheHolder`.

This gets its own entry rather than a line inside 040 for one reason: the log is append-only
and its value depends entirely on a reader being able to trust that an open note is actually
open. A stale one is worse than no note, because it sends somebody to fix something that is
already fixed. 007 stands as written, as every entry does; this is the entry that says its
note has been overtaken.

<!-- Expand later: whether open notes want a convention for being marked answered from the
     entry that answers them, now that this has happened twice. -->

---

## 042 — `xmin` leaves the three migrations

`CLAUDE.md` states flatly that `xmin` is a system column and "must never appear in a
migration's `CREATE TABLE`". It appeared in the C# `CreateTable` of all three initial
migrations — Inventory, Orders and Payments — as
`xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)`. The lines are
gone.

**Why obey the rule rather than amend it.** The line is inert today only because *this*
provider strips it from the emitted SQL. A provider change, a hand-written DDL script, or a
reader copying the migration into psql each turn it into
`ERROR: column name "xmin" conflicts with a system column name`. The rule costs three
deleted lines to obey, and a written rule is settled — contradicting one is a proposal, and
not one worth making when the alternative is this cheap.

**What actually decided it.** `ConcurrentHoldTests` tells its reader that migrating "proves
the generated migration applies against real Postgres, including that it does not try to
create the xmin system column" — while the migration's C# literally did. A false comment
standing next to the most-defended mechanism in the project is worse than either the line or
the rule.

**Why editing an applied migration is safe here specifically.** The general warning is about
edits that make the migration and the model snapshot describe different schemas. They never
disagreed: the snapshots and Designer files carry `RowVersion`/`xmin` as a *model property*,
not as a `CreateTable` column, and `dotnet ef migrations add` diffs the current model against
the snapshot. Editing only the `Up()` body touches neither side. Nothing is deployed, the dev
databases are one `docker compose down -v` away, and the integration suite migrates from
scratch against a throwaway container on every run — so a wrong edit fails loudly inside one
test run rather than quietly in six weeks.

**The part that makes it stick.** Regenerating any of these migrations would put the line
straight back and nothing would notice, so
`MigrationConventionTests.NoMigrationShouldCreateTheXminSystemColumn` reads every migration
source and refuses the `CreateTable` form. Deliberately narrow: `SeatConfiguration`'s
`HasColumnName("xmin")` is the mapping, which is correct and is the whole mechanism, and the
snapshots' property entries are untouched.

<!-- Expand later: whether the same source test should assert the reverse — that every table
     carrying a concurrency token maps it to xmin rather than to a real column. -->

---

## 043 — Load-In is closed

The phase's three outcomes were modular monolith, DDD tactical patterns, TDD foundation.
What "closed" means here is narrower and more useful than "finished": **every claim the
phase makes is now enforced by the build or by a test rather than by prose.**

Before 036–042, four of the five zero-dependency projects were guarded only by a comment,
the one guard that existed was blind to the case it was most likely to meet, module
boundaries were enforced by nobody, two domain preconditions went unchecked, the exclusive
expiry boundary was stated and never tested, and the flagship concurrency test contained an
assertion that accepted an outcome it could not produce. None of that was visible from the
outside, which is the point — a claim nothing checks reads exactly like a claim that holds.

**Explicitly left to later phases, and not for want of noticing:** the outbox and its
dispatcher, and with them the reconciliation path 031 needs and the domain events 029
declined for `Payment` (Soundcheck); the expired-hold sweep, load testing and chaos
(Showtime); CI, deployment, observability and the write-up (On Tour). Notifications and
Identity do not exist, so `X-Client-Id` remains a claimed identity. Container sharing across
the integration suite and the fourth `ClientIdEndpointFilter` both have triggers recorded,
and neither has been met.

<!-- Expand later: whether a phase should end with an entry like this at all, or whether the
     roadmap table in CLAUDE.md is the better home for the claim. -->

---

## 044 — The outbox drain belongs to the unit of work, not to the repository

Two comments had been disagreeing about where domain events get written to the outbox since
before either was reachable. `EfSeatRepository.SaveAsync` said to drain `seat.DomainEvents`
there; `InventoryDbContext` said a `SaveChanges` override would do it. Nothing was built
either way, so nothing was broken — but the two were describing different designs, and
whichever got written first would have quietly falsified the other comment. The override
wins. The repository's TODO is gone, replaced by a pointer.

**Timing does not decide it, and that is worth saying because it is the first objection.**
Both locations see the events at a usable moment. The three handlers call
`ClearDomainEvents()` *before* their transition — a scrub of any previous rejected attempt,
not a drain — so by the time `SaveAsync` runs, the events raised by this attempt are on the
instance and `SaveChangesAsync` has not yet been called. An override sees the same events at
the same point through `ChangeTracker.Entries<Seat>()`. Atomicity does not decide it either:
both add outbox rows to the same context before one `SaveChangesAsync`, so both get the
state change and the events in a single transaction, which is the only guarantee an outbox
is actually for.

**What decides it is that the repository is handed one aggregate and the context commits
all of them.** `SaveAsync(seat)` knows about the seat it was passed. `SaveChangesAsync`
persists everything the context is tracking, and the context is scoped per request — a
four-seat checkout drives four `HoldAsync` calls through one `InventoryDbContext`, so by the
fourth the tracker holds four seats. Those two sets happen to coincide today only because
every mutation is followed immediately by its own `SaveAsync`. That is a property of the
current call pattern, not of the design, and an outbox whose completeness rests on a call
pattern is an outbox that loses an event the first time somebody changes one.

**The evidence is already in the file, not hypothetical.** `EfSeatRepository` calls
`SaveChangesAsync` twice — once in `SaveAsync` and once in `AddRangeAsync` — and the TODO
sat on only one of them. A drain written where it was proposed would have been incomplete on
the day it was written. It costs nothing today because `Seat.Create` raises no events, which
is a fact about this week's domain rather than a guarantee; 012 has bulk creation raising
nothing only because there is nothing yet worth raising. An override is blind to no write
this context makes.

**What the override costs, stated rather than glossed.** It has to *discover* which
aggregates have events by walking the tracker, where the repository would have been handed
one directly. `Seat` has no base class and no marker interface — `Encore.BuildingBlocks.Domain`
was removed deliberately and `Seat` manages its own list — so the override will name `Seat`
concretely and filter on a non-empty `DomainEvents`. That is honest while there is one
aggregate and slightly awkward when there is a second, at which point the answer is a marker
interface in `Encore.Shared` next to `IDomainEvent`, not the base class that was removed.

**One hazard recorded now so it is not rediscovered later.** A rejected save leaves its
outbox rows in the tracker as `Added`. All three seat handlers retry once after a lost race,
and the retry raises the same event again — so without removing the stale entries, the
eventual commit writes the event twice. This is true of either location and is the reason
the surviving TODO names it.

**Nothing is implemented.** The outbox is still Soundcheck (043), and this entry settles only
which component will own the drain when it arrives.

<!-- Expand later: whether the override should also be where ClearDomainEvents() is called,
     given the handlers already clear defensively before each attempt, and whether a second
     aggregate should arrive with the marker interface rather than after it. -->

---

## 045 — `Payment` gets `Seat`'s two guards

039's closing note asked "whether Payments' own transition methods want the same guard, given
that `Payment` takes `utcNow` the same way and is protected today only by every caller
happening to be a handler". They do. `Payment.Create`, `Authorize`, `Decline`, `TimeOut`,
`Retry`, `Capture` and `Void` now all check `utcNow.Kind`, and `Create` refuses an empty
`id`, `orderId` or `clientId` the way `Seat.Create` has since 038. `Seat` is untouched.

**This was a documentation bug before it was a code bug.** The class remark on `Payment` has
said "Time is a parameter, never a reading — as it is for `Seat`" since 029, and the half of
that sentence after the dash was not true: `Seat` had checked what arrived since 039 and
`Payment` had not. A comment claiming a guarantee the type does not provide is worse than no
comment, because it is exactly what a reader checks instead of the code — 041's argument
about stale open notes, pointed at a class remark. The remark now says what is enforced.

**What the gap actually cost.** Two of the seven methods write their instant to a
`timestamptz` column, so a non-UTC value reaching those surfaced at the Npgsql boundary as a
provider error several layers from the caller. The other five write to `AttemptedAt` or
`ResolvedAt` on a row that may not be saved in the same breath, or to neither — so a `Local`
instant could be recorded, read back as UTC and quietly misreport when money moved, with
nothing failing anywhere. That second case is the one worth closing.

**The guard goes before the idempotent return, not after.** `Capture` and `Void` return early
when they are already in their terminal state. Putting the check after that return would
report a caller's bad clock only sometimes, depending on state the caller cannot see, which
is not a precondition so much as a lottery. `Seat.Hold` guards ahead of its own idempotent
same-client path for the same reason, and two tests pin it here.

**In `Create` the checks read in parameter order**, so the identity guards come first and the
`utcNow` guard last — while the transition methods guard the instant first, because there it
is the significant precondition and the state guard follows it. That is `Seat`'s arrangement
in both places, not a new one.

**The guard method is duplicated, and this one is not 024's kind of duplication.** The
`ClientIdEndpointFilter` copies cannot be shared because `Encore.Shared` holds zero packages
and an `IEndpointFilter` would drag `Microsoft.AspNetCore.App` through the door
`Inventory.Domain` depends on. Nothing like that applies here: the guard is seven lines of
BCL, and `Encore.Shared` could host it tomorrow. It is duplicated because sharing it means
changing `Seat`, which this change was scoped out of, and because 019 is clear that Shared
carries contracts every module agrees on rather than whatever two modules happen to have in
common. **Chosen rather than found** — no existing rule covers a pure-BCL helper wanted by
two aggregates, and this picks copying for now. If a third aggregate wants it, that is the
trigger to promote it next to `IDomainEvent` rather than write it a third time.

**What it costs.** Nothing in production can reach either guard. `InProcessOrderPayments` is
the only production caller and it takes its instant from
`TimeProvider.GetUtcNow().UtcDateTime` and its ids from a bound request. The value is
entirely in the tests, the reconciliation path 031 still needs, and the bulk or seed code
that does not exist yet — which is 005's "tired teammate" argument and the reason a factory
is worth having at all.

<!-- Expand later: whether the guard belongs in Encore.Shared beside IDomainEvent once a
     third caller wants it, and whether Order should get the identity guards too given it is
     a POCO with public setters and no factory to put them in. -->

---

## 046 — The three seat handlers share one lock vocabulary

`HoldSeatCommandHandler` was the reference shape and the other two had drifted from it in
four places. All four are now one copy in `SeatLocks`, an internal static class in
`Application/`: the five-second TTL, the seat resource key, the client resource key, and the
release-from-a-`finally` helper. `SellSeatCommandHandler` also binds `utcNow` once at the top
of `AttemptAsync` as the other two always have, instead of reading the clock inline at the
`Seat.Sell` call.

**The resource keys were the one worth doing first, and they were not on the list.** A drifting
TTL is untidy and a drifting release helper is a maintenance cost, but `$"seat:{seatId}"`
written out in three files is a latent correctness bug: two handlers spelling the same seat's
resource differently would take two different locks, each believing it held the one that
matters. Mutual exclusion would simply stop happening, silently, at the moment it is wanted,
and nothing would fail — the seat row's `xmin` would still keep the system correct, so the
symptom would be a throughput collapse under contention with no error anywhere. That is the
worst shape a bug can have in this module, and it is why this went slightly beyond the four
items it was asked to converge.

**017's criterion, and why the migrators stay duplicated while this does not.** 017 accepted
sixty duplicated lines per module on the grounds that the code is inert — "a bug in one copy
cannot be a bug in another". These are not inert: they are read at run time by three callers
racing for the same rows, and a divergence in one copy changes what another copy does. 036
made the same distinction when it moved the build guards into one file. The migrators are
unaffected and stay as they are.

**`Application/`, not `Ports/`.** `Ports/` states a contract that adapters implement. None of
this is part of that contract — `ReleaseIfHeldAsync` is a caller's discipline, not an
implementor's obligation, and the TTL and key formats are decisions this layer makes about
how it uses the port rather than anything the port promises. Every caller today is in
`Application/`, and the expired-hold sweep will be too.

**The helper takes no `CancellationToken` at all**, which is stronger than the convention it
replaces. The port's doc comment has always said callers should release with
`CancellationToken.None`, because a release runs from a `finally` after the write has already
happened and a client that hung up must not be able to strand a lock until its TTL expires.
Not accepting a token is what stops the next caller passing the wrong one; the rule is now
unable to be broken rather than merely written down.

**The two reload-after-a-lost-race implementations stay as they are, and that was checked
rather than assumed.** `EfSeatRepository.GetByIdAsync` and `InProcessOrderPayments.ReloadAsync`
both end up calling `EntityEntry.ReloadAsync`, and there the resemblance stops. The first
takes an **id**, runs on **every read**, searches the change tracker, falls back to a query
when nothing is tracked, and maps a detached entry to `null` because the row may have been
deleted. The second takes an **entity the caller already holds**, runs **only from a catch
block** after a rejected save, resets the entry from `Modified` to `Unchanged` so the reload
will take, and has no null case because the row is known to exist. Different inputs, different
triggers, different return types, different edge cases; 009's move is a one-line EF idiom that
both happen to need, the way both happen to need `SaveChangesAsync`.

There is also a hard structural barrier, which settles it independently. A shared helper needs
a home both modules can reference. `Encore.Shared` cannot be it: that project carries
`EncoreZeroDependency`, so an EF Core reference there fails the build outright with
`ENCORE001` and `ENCORE003`, and `Inventory.Domain` references it, so the domain-purity rule
would go too. The only alternative is a new `Encore.Persistence` assembly referenced by two
modules — a cross-module implementation dependency, bought for two methods that do not share
behaviour. Left alone, deliberately.

<!-- Expand later: whether the expired-hold sweep should take a seat lock at all when it
     arrives, given it writes rows nobody is contending for by definition, and whether
     SeatLocks is where a per-resource TTL would go if the sweep wants a longer one. -->

---

## 047 — Correcting 045, and the summary that described a drain nobody wrote

Two statements went into the tree describing things that are not in it. Neither is a bug —
no behaviour changes here, and only one comment moves — but both are the kind of note that
sends a reader looking for a counterpart that was never written, which is the failure 041
argued is worse than having no note at all.

**`ClearDomainEvents` was documented as the drain's method, and the drain has no caller.**
`Seat.ClearDomainEvents` read *"Drops the recorded events once they have been handed to the
outbox."* Nothing has ever handed them anywhere. Every call site is an application handler
scrubbing a rejected attempt *before* its transition — the discipline 008 introduced and 044
describes at length — so the summary named the one caller that does not exist and omitted
the three that do. Whoever wrote the drain against that sentence would have gone looking for
an existing post-save call to join, found none, and had to re-derive the timing from the
handlers anyway. The summary now says what the callers actually do, and records the outbox's
use of it as open rather than as settled.

**This does not close 044's note, which asked a different question.** 044 left open whether
the override should *also* call `ClearDomainEvents` once the drain exists, given the handlers
already clear defensively before each attempt. That is still open and still worth asking.
What is settled is narrower: the summary was describing a timing that no code uses.

**045 claimed a `Seat` arrangement in two places where `Seat` only has one.** It says the
identity guards coming before the `utcNow` guard in `Payment.Create`, and the `utcNow` guard
coming first in the transition methods, are together *"`Seat`'s arrangement in both places,
not a new one."* The transitions half is exactly right: `Seat.Hold`, `Release` and `Sell` all
guard the instant, then the state. The factory half has nothing to mirror — `Seat.Create(Guid
id, Guid eventId)` takes no clock at all, so there is no ordering there to copy. The rule
`Payment.Create` actually follows is parameter order, which is a good rule and the one the
code comment gives. **045 stands as written, as every entry does**; this is the entry saying
that one clause of it cites a precedent the file does not contain.

**No code changed for that second one, and that is the part worth recording.** The comment in
`Payment.Create` was checked and is accurate: its "as in `Seat`" attaches to the clause about
the transition methods, not to the factory, so it claims only the thing that is true. The
overclaim is in this log's summary of the change, not in the change. Editing the code to
match a wrong entry would have been the worse of the two available mistakes, and the reflex
to do it is exactly what an append-only log has to be read carefully enough to resist.

**The dated audit snapshot is left alone.** `STATUS_2026-09-18.md` quotes the
`EfSeatRepository` TODO that 044 deleted, and cites line numbers that have since moved. It is
a snapshot with a date in its name, and a snapshot that gets corrected stops being a record
of what was true on its date. Left stale deliberately — but the convention is worth stating
once, because nothing in the repo currently distinguishes a file that is frozen from one that
is merely out of date.

<!-- Expand later: whether dated snapshot files want a one-line header declaring themselves
     frozen, now that one of them holds a quotation that no longer matches the tree, and
     whether 044's question about the override calling ClearDomainEvents is better answered
     by the drain not needing it at all. -->

---

## 048 — The load harness arrives before the outbox, and it is k6

Two decisions here: that measurement comes next, ahead of the phase the roadmap says
is next, and that the thing doing the measuring is a container rather than a project.

**Why before the outbox, which is what Soundcheck actually asks for.** The outbox writes
rows into the same transaction as every seat write. It lands directly on the hot path —
the one this project exists to argue about — and it lands there permanently. Measure
afterwards and there is one number, with no way to say what the outbox cost; measure
first and the outbox's cost is a subtraction anyone can check. A baseline taken after the
change it is meant to bracket is not a baseline, and this is the last moment it can be
taken cheaply. 004 already set the precedent for working out of phase order when the
ordering is arbitrary and the reason is not: Payments arrived during Load-In because
Strangler Fig needs something to strangle.

**It also finishes a commit that is already in the tree.** The api image and the `load`
compose profile were added so "a load harness has something to point at", and until now
nothing pointed at it. A target with no harness is the same unspent kind of work as a
purity rule with no test, which is what 036 through 042 spent themselves closing.

**What was actually missing, stated precisely.** `ConcurrentHoldTests` is not weak and is
not replaced: fifty tasks, one seat, exactly one winner, no Redis lock anywhere in the
test, so it proves correctness rests on `xmin` alone. What it cannot say is what the hold
path *costs*. There is no number anywhere in this repo — not a p99, not a throughput
figure, not the point where the Redis lock stops reducing contention and starts being
another round trip. The claim the project leads with was, until this entry, entirely
qualitative.

**k6 over NBomber, and the reason is the purity rules rather than taste.** NBomber is the
.NET answer and would have been the more natural-looking choice in a C# repo. It also
means a fifth test project, a `PackageReference`, and a new assembly that
`Encore.ArchitectureTests` and `ENCORE001`-`003` now have to have an opinion about — a
load harness is not domain code, not a module, and not a test project, so every existing
rule would need a carve-out written for it. k6 in a container needs none of that: the
script is not in the solution, not in the build, and not in the reference graph. The cost
is a second language in the repo for one file, and that the harness can only reach the
system over HTTP. The second is not really a cost. The deployed surface is the only
surface a real flash sale can touch, and a harness that could call `HoldSeatCommandHandler`
directly would be measuring something no customer can reach.

**One threshold is an assertion; the rest are declarations.** `seats_sold` counts 200s
from `/purchase`, and the threshold is `count<=SALE_SEATS`. That is the oversell
invariant, expressed where it has never been expressed before — under sustained load
against a real Kestrel, a real Postgres and a real Redis rather than against fifty tasks
in one process. If it ever trips, k6 exits non-zero and the run fails, exactly as a unit
test would. `unexpected_responses==0` is the second real assertion: a 500 or a timeout is
a fault, while a 409 is the system working. Everything else in the `thresholds` block is
`>=0` and always passes — k6 only surfaces a tagged submetric in the summary if a
threshold names it, so those lines exist to make the refusal breakdown and the per-phase
latency visible, and they assert nothing.

**The counter is a count of distinct seats only because of how the script is written**,
which is worth recording because it is load-bearing and invisible. Re-purchasing a seat
you already bought is an idempotent success (007), so a client that retried would be
counted twice and could fake an oversell. Every iteration therefore uses a client id
nobody else uses, buys at most once, and retries nothing.

**No latency threshold, deliberately.** There is no p99 target here, because inventing one
before the first run would mean either a number so loose it can never fail or a number
that fails for reasons nobody can interpret. An SLO asserted before a measurement is a
guess wearing a test's clothing. **The trigger for adding one:** three runs on the same
machine with the same parameters, at which point the spread is known and a threshold set
just outside it means something.

**Two scenarios, sequenced rather than simultaneous.** `contention` puts every client on
five seats and releases immediately, so the pool never drains and pressure stays at
maximum for the whole window — the hot-path number. `flash_sale` puts them on a real seat
map and buys, so inventory drains and the refusal mix shifts from `already_held` to
`already_sold` the way it would on sale day. Run together, neither latency figure could be
attributed to either shape, so the second starts five seconds after the first ends.

**Scoped out on purpose: the order path.** `POST /orders` and `/confirm` are the fuller
flash sale, and they are also where the simulated gateway's declines and timeouts live
(032). Mixing a fake gateway's latency into the first seat-contention baseline would
produce a number that measures the simulation. That scenario is the obvious next one, and
it wants the baseline this entry establishes before it is worth running.

**The summaries are gitignored.** One machine, one day, no CI to produce them consistently
— published in the repo they would read as a claim about how the system performs, which
is not a claim a laptop can make. The harness ships; the numbers stay local until there is
somewhere honest to run them.

<!-- Expand later: whether the p99 threshold should be set from the first three runs or
     wait for CI to produce them on consistent hardware, and whether the order-path
     scenario belongs in this same script or in a second one given it measures a different
     system. -->

---

## 049 — Correcting 014's "one shape", and an OpenAPI document that is not a package

The host was run and every route exercised against it. The modules were exactly as
documented — twenty-six scenarios, no surprises, not one refusal reported the wrong
`reason`. The host itself was not, in one specific way, and the same session produced a
document describing the surface.

**014's claim about the middleware is false as written, and it was measurable all along.**
It says `AddProblemDetails` and `UseExceptionHandler` "earn their place by making every
response one shape rather than problem+json from the modules and empty bodies from the
framework." They do not. `AddProblemDetails` registers a body factory and changes no
response by itself; `UseExceptionHandler` reaches unhandled exceptions and nothing else.
Everything the framework produces *before* an endpoint runs was still coming back empty.
Measured on the running host: `404` for an unmatched route, `405` for the wrong verb,
`415` for the wrong content type and `400` for a body that will not bind were each a
zero-length body with no `content-type` at all.

**The entry contradicted itself, which is the part worth noticing.** Four paragraphs
above that sentence, 014 explains that the client id arrives through an endpoint filter
rather than a bound parameter because "a failed parameter bind yields a framework 400 with
an empty body, which is precisely the response nobody can diagnose from a load harness."
It knew. It solved that one case at the endpoint and then described the middleware as
though it had solved the general one. A claim nothing checks reads exactly like a claim
that holds — 043's line, landing this time on the entry that made it.

**The fix is `UseStatusCodePages`, one framework line.** It is what asks the registered
factory for a body when the pipeline is about to return a bare status. All four now come
back as `application/problem+json`, and module responses are byte-for-byte unchanged.
**014 stands as written**, as every entry does; this records that one of its sentences
described an outcome the code did not produce.

**What the fix deliberately does not do is give those responses a `reason`.** A framework
refusal is about the request being malformed, not about the state of the world, so there
is no closed vocabulary to draw one from and inventing `route_not_found` would be minting
a promise no handler makes. Clients still branch on `reason` only for module refusals,
which is 014's rule intact rather than weakened.

**The document: Swagger UI at `/docs/`, over a hand-written `openapi.json`, and still
zero packages.** 014 refused Swashbuckle and `Microsoft.AspNetCore.OpenApi` because both
are packages and `Encore.Api` holds none on purpose. That refusal is untouched — nothing
here adds a `PackageReference`, `ENCORE001` still passes, and a `<script src>` is not a
NuGet dependency. What is new is the document itself and two more pieces of framework
middleware, `UseDefaultFiles` and `UseStaticFiles`, to serve it from `wwwroot`.

**Served by the host rather than opened off disk, and that is not incidental.** A page on
`file://` is a null origin, so every request Try it out made would be refused, and the
only fixes would be a CORS policy this API should not need or a second server to host the
page. Serving it from the host puts the page and the API on one origin and the problem
disappears. The UI assets come from a CDN instead of being vendored: three megabytes of
minified JavaScript in a repo that argues about dependency discipline would be the wrong
trade, and the honest consequence is that `/docs/` needs a network connection while the
API does not.

**The cost, stated rather than glossed: nothing regenerates this document and nothing
tests it.** It was written from the endpoints, the result mappers and the closed reason
enums, then checked against a running instance route by route — which makes it accurate
today and says nothing about tomorrow. Add a route and no build fails, no test reddens,
and the document is quietly wrong. That is the exact failure mode 036 through 042 spent
themselves closing everywhere else, reintroduced deliberately in one file because the
alternative was a package.

**Two ways to close it, and the first is not mine to take.** Overturning 014 and taking
`Microsoft.AspNetCore.OpenApi` would generate the document from endpoint metadata that
already carries `WithName` and `WithSummary`, so it could not drift — at the price of the
host's zero-package property, which 002, 014 and 036 each spent something to establish.
That is a decision the owner makes, not an implementation detail. The cheaper half-measure
needs no package at all: a test resolving `EndpointDataSource` and asserting that the set
of route patterns and the set of paths in `openapi.json` are equal. It would catch the
drift that matters — a route added, removed or renamed — while saying nothing about
whether a response body still matches its schema.

<!-- Expand later: whether the EndpointDataSource parity test is worth writing before the
     package question is settled, given it would be thrown away if the package arrives;
     and the smaller thing noticed while exercising the API — `price` comes back `49.50`
     from POST /catalog/events and `49.5000` from the GET, because one echoes the bound
     request and the other reads a numeric(18,4) column, which is numerically identical
     and textually not. -->

---

## 050 — The host-side Postgres port moves to 55432

`dotnet run` died on `Npgsql.PostgresException 28P01: password authentication failed for
user "encore"` against a connection string that is correct, a container that is healthy,
and a role whose password is exactly what the string says.

**The asymmetry was the diagnosis, and it is the thing to recognise next time.** Every
container path was green at the same moment: the full suite, the k6 baseline, the `api`
service serving requests. Only `dotnet run` failed. Those two populations differ in
exactly one way — container-to-container traffic resolves `postgres` on the compose
network and never touches a published port, while `dotnet run` and `dotnet ef` go through
`localhost`. So a collision on the host's port is invisible to everything this repo uses
to check itself, and visible only from the one path nothing automated takes.

**The cause: a native PostgreSQL 18 Windows service, auto-start, already owns
`0.0.0.0:5432` and `[::]:5432`.** Anything reaching `localhost:5432` reached PostgreSQL 18,
which has no `encore` role with that password, so it refused — correctly, and about a
different server than the one the message makes you think of.

**What made it expensive is that Docker reports the mapping anyway.** `docker ps` prints
`0.0.0.0:5432->5432/tcp` for `encore-postgres` while the native service holds the
listener, so the most obvious check agrees with the wrong hypothesis. The contrast that
settles it is on the same machine: port 6379 is owned by `com.docker.backend`, which is
what a published port actually looks like when Docker won it. Checking the owning process
is the check worth doing; reading `docker ps` is not.

**The evidence, in the order that closed it.** Connecting to the container over the compose
network with `encore`/`encore` succeeds. Connecting to `host:5432` with the identical
credentials fails `28P01`. Therefore `host:5432` is not the container. After the move,
`host:55432` answers as PostgreSQL **16.15** — the container's version, not 18.

**Moving the host side rather than the service is 015's call again.** 015 put the test
suite in a container because Smart App Control is machine-wide, has no exclusion mechanism,
and is not this repo's to fix. A PostgreSQL service somebody installed is the same species
of condition: it belongs to the machine, it may be wanted, and disabling it needs elevation
and is a change to the developer's computer rather than to this project. 5432 is also the
single likeliest port on earth to collide, and a portfolio repo that fails on first run for
anybody who has ever installed Postgres is making a bad argument before anyone reads a line
of it. **Stopping the service is still the right fix for a machine that does not want
PostgreSQL 18**, and it is the owner's to make, not this log's.

**The design-time factories moved too, and that was the near-miss.** All four
`*DbContextFactory` classes carry the same `localhost;Port=5432` fallback, so
`dotnet ef database update` and `dotnet ef migrations add` were pointed at PostgreSQL 18
as well. Nobody had run one since the service appeared. A migration applied to the wrong
server is a worse afternoon than a host that will not start, because it fails silently in
the direction of looking like it worked.

**What deliberately did not change: the container-internal port.** It is still 5432 inside,
so the `api` service, the load harness and every Testcontainers suite are untouched — which
is the same reason none of them caught this and none of them could have.

**One thing to check rather than assume.** Anything ever written through `localhost:5432`
went into PostgreSQL 18 and is still sitting there; its data directory holds one database
beyond the three a default install creates. The container's `encore` database is a
different database on a different server, and this change silently switches which one
`dotnet run` talks to. If real work was done against the native server, it did not move
and this entry did not move it.

<!-- Expand later: whether the four connection strings want to come from one environment
     file rather than being repeated in appsettings.json and four factories, now that a
     single number has to be changed in nine places; and whether /health should say which
     server and version it reached, since "wrong Postgres" and "no Postgres" currently look
     nothing alike but neither is visible until something throws. -->

---

## 051 — The outbox row carries two identities, and promises less than it looks like it does

`inventory.outbox_messages` holds an event's published name, its payload as `jsonb`, and
the bookkeeping a delivery needs. Two of its columns are identities, and the difference
between them is most of this entry.

**`Id` is a sequence and orders rows. `MessageId` is a GUID and identifies an event.** They
are separate because `Id` is not safe to publish and `MessageId` is no use for ordering.
Postgres assigns `Id` at INSERT while transactions commit in whatever order they finish, so
two rows can be committed in the opposite order to their ids — which means a consumer
tracking "the last id I saw" would skip rows written by a transaction that started earlier
and landed later. That is the classic outbox bug, and it is silent: the skipped event is
never redelivered, because nothing remembers it was missed.

**So the guarantee this design actually makes is per-transaction.** The events raised by one
save are inserted together, get consecutive ids, and reach handlers in that order — which is
exactly the property 007 needs, since a lazy reclaim raises `SeatReleased(Expired)` and
`SeatHeld` inside one write and the log is worthless if those two arrive the wrong way round.
Across transactions there is no promise, and the dispatcher sidesteps the question entirely by
claiming on `ProcessedAt IS NULL` rather than on a high-water mark (053). Writing the limit
down is the point: an ordering guarantee nobody stated is one somebody will assume is stronger
than it is.

**All three events are published, not just `SeatSold`.** Only `SeatSold` has a consumer (055),
so publishing the other two costs an INSERT on the hottest path in the system — a hold under
contention writes a row every time, and the baseline puts that at roughly 116 holds a second.
`SeatSold` alone would be materially cheaper. It loses to 007's argument, the same one
`SeatReleaseReason` was added under: **an append-only log is the one place YAGNI has an
asymmetric cost.** A consumer can be added later; history cannot. The share of holds that lapse
rather than convert is the number that says whether five minutes is the right window, and it is
unanswerable for every hold written before the row existed. The cost is the thing 048 built a
baseline to measure, so it gets measured rather than argued about (056).

**Processed rows are marked, not deleted.** The table therefore only grows, which is fine for
inserts and would not be fine for an index over the whole of it — so
`ix_outbox_messages_unprocessed` is filtered to `"ProcessedAt" IS NULL` and indexes only the
backlog, which in a healthy system is nearly empty whatever the table's size. Same mechanism as
`ux_payments_order_live` and `ux_orders_client_event_pending`, used here for cost rather than
for uniqueness. **A retention sweep is deliberately not built**: nothing here currently has an
opinion about how long an event is worth keeping, and inventing ninety days now would be the
same guess wearing a policy's clothing that 048 refused to make about a p99.

**`jsonb` rather than `text`, and it is a real trade rather than a default.** `text` is cheaper
to write and this is the hot path. `jsonb` wins because a stuck message is diagnosed by querying
into its payload, and a payload nobody can query is a blob with a timestamp beside it — the
integration tests already lean on that, scoping their assertions with `JsonContains`. It also
refuses malformed JSON at the INSERT rather than at the consumer.

**No `xmin`, which every other table here carries.** Those tables have two writers meeting on a
row. This one does not: the dispatcher claims with `FOR UPDATE SKIP LOCKED`, so a row is handed
to exactly one reader and a second reader is given a different one. A concurrency token would
guard a race the claim has already made unreachable.

**`MessageId` is indexed but not unique.** Uniqueness here would be a constraint checked on
every insert on the hottest write path, guarding against a bug in the drain rather than against
anything a user can do. The index that has to be unique is the *consumer's*, because that is
where a duplicate does damage — which is where 055 puts it.

<!-- Expand later: whether the retention sweep should arrive alongside the expired-hold sweep in
     Phase 7, given both are cleanup jobs whose timing must never be load-bearing; and whether a
     dead-letter count belongs on /health, since nothing currently surfaces a message that has
     stopped being retried. -->

---

## 052 — The drain clears on success, and 044 was wrong to call that optional

044 settled that `InventoryDbContext.SaveChanges` owns the drain, and left a question in its
closing note: "whether the override should also call `ClearDomainEvents()`, given the handlers
already clear defensively before each attempt". It reads as a tidiness question. It is not.
**Without it there is a duplicate-write bug reachable from the checkout path that exists
today.**

**The mechanism.** `InventoryDbContext` is scoped, and a four-seat checkout drives four
`HoldAsync` calls through one instance of it — 044 says so itself, in the paragraph explaining
why the repository cannot own the drain. Each handler calls `seat.ClearDomainEvents()` before
*its own* transition, which scrubs the seat it is about to touch and nothing else. So after seat
one saves, seat one stays tracked with its `SeatHeld` still on the instance; when seat two
saves, the drain walks the tracker, finds seat one still holding an event, and writes it again.
Seat one's hold is published four times, seat two's three times, and so on — ten rows for four
holds.

**The handlers cannot fix this and it is not their job to.** By the time seat two is being held,
seat one is somebody else's aggregate as far as that handler is concerned. The component that
walks every tracked aggregate is the only one positioned to know which ones it has drained —
which is the argument 044 used to put the drain here rather than in the repository. It simply
did not follow it one step further.

**After the base call, never before.** A rejected save must leave the events on the instance so
the retry can re-raise over them, which is what the handlers' defensive clear is for. Clearing
first would throw away the record of an attempt that never happened and leave the retry
publishing nothing.

**This is the second half of a pair, and 044 recorded only the first.** It named the hazard that
stale `Added` outbox rows survive a rejected save into the retry; that one is handled at the top
of the drain by detaching them. This is its mirror image — events surviving a *successful* save
into the next one. Both are "state left behind by one save leaking into another", and a drain
that handles one and not the other is half-built.

`Hold_WhenSeveralSeatsAreHeldOnOneContext_ShouldWriteEachEventExactlyOnce` is the test. Delete
the clear and it reports ten rows where it wants four.

**047 gets its answer too.** It recorded that `Seat.ClearDomainEvents`'s summary named a drain
with no caller, and rewrote it to describe the three callers that existed. There are now four,
the fourth is the one the original sentence was reaching for, and the summary says what each is
for.

**044 stands as written**, as every entry does. What is corrected is narrower than its argument:
the question it left open had only one available answer, and calling it open invited somebody to
answer it the other way.

<!-- Expand later: whether the handlers' defensive clear still earns its place now that the drain
     clears on success, or whether the retry path could rely on the override alone — they
     overlap, and the overlap is currently load-bearing in exactly one direction. -->

---

## 053 — The dispatcher: what it claims, what it retries, and the order it deliberately does not keep

`OutboxDispatcher` is the repo's first `BackgroundService`. It polls, claims a batch, delivers
it, and records what happened.

**`BackgroundService`, not `IHostedLifecycleService`.** The four migrators use the lifecycle
interface because they must finish before Kestrel opens the socket (013). Nothing here has that
requirement — a message that waits a second while the host starts has lost nothing — and a loop
that never ends would block startup forever if it ran in `StartingAsync`.

**`FOR UPDATE SKIP LOCKED`, for a second instance that does not exist yet.** Rows another
dispatcher holds are passed over rather than waited on, so two hosts drain one table in parallel
and neither delivers the other's message. There is one host today. This is the cheap half of the
cloud deploy On Tour wants, and a claim query that could not be run twice would have to be
rewritten then rather than extended — which is the test 001 sets for whether optionality is
worth paying for.

**Claimed on `ProcessedAt IS NULL`, never on a high-water mark**, for 051's reason: ids are
assigned at INSERT and commits do not follow suit, so a cursor skips rows silently. Filtering on
the column the delivery itself writes cannot.

**Raw SQL, because EF Core cannot express a locking clause** — and the locking clause is the
entire point of the query. An adapter is where raw SQL belongs; nothing above the port learns
that this is Postgres. The string is a compile-time constant built from
`InventoryPersistence.Schema`, so the schema cannot drift out of step and no input reaches it.

**A failing message backs off and lets the queue move past it.** Exponential from two seconds,
capped at five minutes, and past five attempts the row falls out of the claim predicate entirely
and sits there as a dead letter — readable, no longer consuming attempts, not deleted.

**The cost of that is stated rather than glossed: a failing message is overtaken.** The
alternative is blocking the queue behind it, which preserves a global ordering across
transactions that 051 explains this design never promised, at the price of one bad row stopping
every good one. Choosing that would be spending a real outage to protect a guarantee nobody has.

**The loop catches everything.** A tick that throws — a database that went away, a claim that
deadlocked — logs and waits rather than ending the service. An uncaught exception in
`ExecuteAsync` ends a `BackgroundService` silently and stops delivery for the life of the
process, which is the one outcome worse than a slow outbox.

**Nothing about seat correctness depends on any of this**, and that is checked rather than
claimed: `ConcurrentHoldTests`, `ConcurrentSellTests` and `ConcurrentHoldCapTests` never register
a dispatcher and pass unchanged. It is 007's criterion for the expiry sweep, pointed at
publication — *if a test cannot pass with it disabled, it has become load-bearing and the design
is broken.*

**`Inventory:Outbox:Enabled` defaults to true, and the migrator's flag defaults to false.** The
same question with opposite answers, because the risks are opposite: a migrator that ran by
default would rewrite a database as a side effect of booting, while a dispatcher that did not run
by default would silently stop delivering. A default is only safe relative to what goes wrong
when it is wrong.

**No MediatR, and no reflection either.** `OutboxEventCatalog.Register<T>` closes over the generic
at registration, so each entry is an ordinary delegate by the time a message arrives and the
compiler has already checked that the payload type and the handler interface agree. The
alternative — resolving a `Type` from the row and calling `MakeGenericMethod` — pays for that on
every message and fails at run time when it is wrong. An unregistered name throws rather than
being skipped, so a misconfigured event becomes a dead letter somebody can see instead of an
event that silently never arrives.

<!-- Expand later: whether the poll should become LISTEN/NOTIFY once delivery latency is
     something anybody measures, and whether a dead-lettered message wants a way back — nothing
     can currently retry one without an UPDATE by hand. -->

---

## 054 — What crosses the wire is a contract, not a domain record

`SeatHeld` stays in `Inventory.Domain`. `SeatHeldV1` is a new record in `Inventory.Contracts`,
and `SeatEventPublication` is the one place that maps between them.

**The cheaper design was `JsonSerializer.Serialize(domainEvent)`**, and it is genuinely cheaper:
three records and a translation method would not exist. It loses because it makes `Seat`'s field
names a published wire format. Renaming a field inside the aggregate would then be a breaking
change to every consumer — including rows already sitting in the outbox, which would describe
fields that no longer exist and could not be read by the code that was meant to read them. That
is 003's argument, which says a port speaks the module's language and never the adapter's,
pointed at the payload instead of at a signature.

**This is optionality that will actually be spent, which is the test 001 sets.** Soundcheck
exists to move a module out of process. The moment Inventory is its own service the payload is a
genuine contract between two deployables, and the cost of having treated it as one from the
start is three records.

**The reason enum becomes a string.** An enum crossing a boundary is an integer, and an integer
means something only while both ends agree on member order — so inserting a member into
`SeatReleaseReason` would silently re-label every row already written. `"cancelled"` and
`"expired"` cannot drift that way. Same family of trap as `ux_orders_client_event_pending`
filtering on the literal `"Status" = 0` (027), and here it is avoidable rather than merely pinned
by a test.

**The name is chosen, not derived.** `inventory.seat.sold.v1` rather than a CLR type name,
because a type name drags the namespace and the assembly into the contract, and a repo that
renames a folder should not break its own consumers. The `.v1` is the versioning story and it is
deliberately cheap: a breaking payload change gets a new name and a new record beside the old
one, and the dispatcher carries both until the last consumer moves. Rows already written keep
meaning what they meant.

**An unmapped domain event throws at the first save that raises it.** A fourth `IDomainEvent`
with no entry in `SeatEventPublication` fails loudly in development rather than being silently
dropped — an event raised, never published and gone for good, which is precisely the failure an
outbox exists to prevent. 008 makes the same call about unhandled refusal reasons: better a loud
failure now than a plausible wrong answer later.

**The names live in the contracts assembly as `static readonly`, not `const`**, for 020's exact
reason — a `const` is copied into the consumer at compile time, which is a curiosity in one
process and a real bug the day this is served over HTTP.

<!-- Expand later: whether the contracts should carry a schema of some kind once a consumer
     exists that this repo does not compile, and whether a V2 should arrive as a new record or by
     making V1's fields nullable — the second is cheaper and the first is honest. -->

---

## 055 — Notifications is the fifth module, and the first with nothing to map

A flat module — `Models/`, `Data/`, and no `Endpoints/` — that consumes `SeatSoldV1` and writes a
row. It is the first consumer of anything this system publishes, and it exists so the dispatcher
has somewhere to dispatch.

**Why it is here at all, rather than an outbox with no consumer.** 029 refused domain events for
`Payment` on the grounds that "events with no consumer would be the ceremony 001 argues against".
The same objection lands on a dispatcher that delivers to nobody: it would be machinery proving
nothing, and every claim about at-least-once delivery, deduplication and ordering would be
untestable end to end. One real consumer is the smallest thing that makes the mechanism honest.

**It is flat, and it stays flat.** One handler, one table, no invariant spanning rows, nothing
that will ever be substituted. `Notification` is a POCO with public setters like `Order`, not a
factory-constructed state machine like `Payment` — 029's test is whether a type has rules
decidable from its own row, and this has none. It is written once and never transitions.

**There is no `MapNotificationsModule`, and that asymmetry is the interesting part.**
`CLAUDE.md` describes the seam as an `Add`/`Map` pair, and this module has only the first half
because it serves no routes: its entire inbound surface is a handler resolved from the container
by another module's dispatcher. A `Map` method that mapped nothing would exist to complete a
pattern rather than to do anything. **Chosen rather than found** — the rule describes four
modules that all serve HTTP, and this is the first that does not.

**It carries no `FrameworkReference` on `Microsoft.AspNetCore.App` either**, which every other
module does. It needs `IHostedLifecycleService` for its migrator and `IConfiguration` for its
connection string, and those are two small abstraction packages; taking the whole web framework
to get them would put Kestrel and the middleware pipeline on the compile surface of a module that
will never serve a request.

**The read side is missing on purpose, and this is 033's trigger being honoured rather than
spent.** A `GET /notifications` is private data, so by 033 it carries `X-Client-Id` — which would
be the **fourth** copy of `ClientIdEndpointFilter`. 033 named that exact number and named the
exit: *"a small web-only shared assembly that `Inventory.Domain` does not reference — not a drawer
in Shared."* Writing the fourth copy anyway would quietly spend a trigger that was recorded
specifically so it would not be spent quietly, and building the shared assembly in the same change
as the outbox would be two arguments in one commit. So the route waits, and until then this module
is exercised the way Payments' write side already is — by integration tests rather than by a
request anybody can send. 033 accepted that cost explicitly; this is the same cost for the same
reason.

**Idempotency is a unique index on `MessageId`, and the handler's catch is a courtesy.** Delivery
is at-least-once, so this module will eventually be handed one event twice. A read-then-write check
is one that two concurrent deliveries both pass — the same argument 030 makes about
`ux_payments_order_live`, and `Handle_WhenTwoDeliveriesRace_ShouldRecordOneNotification` is the
test that would fail if somebody replaced the index with a check. The violation is caught by
constraint name and narrowly: catching every `DbUpdateException` would file a dropped connection
under "already handled" and mark a message delivered when it was not, which is the mistake 010
records the Redis adapter avoiding by translating only two exception types.

**Two timestamps, kept apart.** `OccurredAt` is copied from the event, for 021's reason — this
module records an answer another module gave, and a second clock reading would be a second
authority over when the sale happened. `CreatedAt` is read here. The gap between them is the
outbox's delivery latency, which is the one number this table can report and nothing else in the
system can.

<!-- Expand later: whether the web-only shared assembly is worth building on its own or should
     wait until something else wants it too, and whether a notification should carry a rendered
     message once any channel exists to send one — it currently records that a client should be
     told something without any opinion about the words. -->

---

## 056 — What the outbox cost, and the half that costs it

048 took a baseline before the outbox so its cost would be a subtraction rather than a
single unattributable number. This is that subtraction. It also corrects the expectation I
went in with, which was wrong in an instructive direction.

**Three configurations, same machine, same parameters** (50 VUs on 5 seats for 60s, then
100 VUs on 500 seats for 60s). Times are milliseconds.

| | hold p99, contention | hold p99, sale | purchase p99 | iterations |
|---|---|---|---|---|
| Before the outbox (three runs) | 41.1 / 41.9 / 50.2 | 33.8 / 34.2 / 41.8 | 44.2 / 55.5 / 60.5 | 425,299 |
| Drain only, dispatcher off | 48.4 | 47.4 | 57.4 | 398,048 |
| Drain and dispatcher | 78.2 | 66.0 | 141.4 | 304,071 |

**The headline: the half that cannot be turned off is nearly free, and the half that can is
the whole cost.**

**The drain lands inside the baseline's own spread on two of the three latencies.** Hold p99
under contention (48.4) and purchase p99 (57.4) both sit within the 41–50 and 44–60 ranges
three pre-outbox runs produced. Hold p99 on the sale path (47.4) is the exception and sits
about 13% above the top of its range. Throughput falls about 6%. So writing one extra row
inside every seat transaction — the thing that makes the sale and the announcement of the
sale inseparable — costs approximately nothing measurable at this load.

**The dispatcher is where the money goes.** Turning it on moves hold p99 from 48.4 to 78.2
(+62%), purchase p99 from 57.4 to 141.4 (+146%), and costs 24% of throughput.

**I expected the opposite of what happened, and the wrong prediction is worth recording.**
Going in, the obvious worry was the INSERT on the hot path: a hold under contention writes a
row every time, and the baseline puts that at over a hundred holds a second. That turned out
not to matter, for a reason the refusal counts make obvious in hindsight — **only about
15,000 of 304,000 iterations write anything at all.** A refused hold throws inside
`Seat.Hold` before `SaveAsync` is ever reached, so the overwhelming majority of flash-sale
traffic never touches the drain. The write path was never where the volume was.

**Where it actually goes.** The dispatcher shares one Postgres instance and one connection
pool with the API, and each delivered message costs three round trips against that shared
resource: the `SELECT ... FOR UPDATE SKIP LOCKED` claim, the consumer's own INSERT, and the
UPDATE that marks the row. Fifteen thousand messages over two minutes is not a large number
in isolation; it is a large number when it is competing with the request path for the
database the request path is contending on. The outbox did not slow the hot path down by
writing to it. It slowed it down by putting a second workload next to it.

**What that suggests, and what it does not.** The dispatcher is the tunable half — batch
size, poll interval, a connection pool of its own, or moving it out of the API process
entirely, which is where Soundcheck is heading anyway. None of those touch the drain. The
one thing that must not happen is "optimising" the outbox by weakening the atomicity, since
the atomicity is the entire product.

**A second result fell out of the same run, and it is the more important one.** With the
dispatcher off, **21,948 events accumulated undelivered** — and the run still sold 500 of
500 seats with no oversell and no unexpected response. 007's criterion says that if a test
cannot pass with the background job disabled, the job has become load-bearing and the design
is broken. Until now that was demonstrated by unit and integration tests. It is now
demonstrated under sustained load against a real Kestrel, a real Postgres and a real Redis,
with a backlog two thousand times the size of anything the tests produce. Delivery is late;
nothing is wrong.

**Delivery was also complete when it was enabled.** 15,334 rows written, 15,334 processed,
none pending, none retried, and `notifications.notifications` holding exactly 500 rows for
exactly 500 seats sold. At roughly 128 events per second against a batch of 50 that loops
immediately when full, the dispatcher never fell behind — so the backlog stayed empty and
051's argument for the partial index held rather than being tested.

**`OUTBOX_ENABLED` is now a compose variable**, so this experiment is repeatable rather than
a thing that happened once. The drain has no equivalent flag and will not get one: turning
off the half that has to be atomic would not be a measurement, it would be a different
system.

**What this is not.** One laptop, one run per configuration, no CI. The baseline's own p99
spread across three identical runs was 22%, which is the right yardstick for how much of any
single difference to believe — it comfortably covers the drain's numbers and comfortably
fails to cover the dispatcher's. Treat the first as "no measurable cost" and the second as
"a real effect whose size is approximate".

**Still no latency threshold, and 048's trigger is met but no longer sufficient.** 048 said
three runs on one machine would establish the spread and a threshold could then be set just
outside it. Three runs happened and the spread is known — but there are now three
configurations of this system rather than one, and an SLO asserted before choosing which one
ships would be measuring a system nobody has decided to run. The trigger moves: set the
threshold once the dispatcher's arrangement is settled.

<!-- Expand later: whether the dispatcher wants its own connection pool or its own process,
     and which of the two the extraction makes free; and whether a run with the dispatcher on
     but no consumer registered would separate the claim-and-mark cost from the consumer's
     INSERT, which this pair of runs cannot. -->

---

## 057 — Reconciliation: the gateway is asked what it did, and a timeout stops being permanent

031 made a timed-out authorisation *safe* and left it *unresolved*. The row keeps its
idempotency key so a retry asks the same question rather than a second one, and it keeps
the order's one live slot so nothing else can authorise underneath it. What it could not
do was make the ambiguity go away, because only the gateway knows, and nothing asked. This
asks: `PaymentReconciler` sweeps attempts that have been `TimedOut` for longer than
`MinimumAge`, calls `SimulatedPaymentGateway.LookUpAsync` with the key the row is carrying,
and settles the attempt on what comes back.

**It is in Payments, not behind the outbox, and that is 031's open question answered.** 031
left it as "whether it belongs in Payments or is the first real consumer of the dispatcher".
Payments, decisively. The outbox carries facts that are already decided; a timed-out
authorisation contains no fact to carry — its whole content is that nobody knows. Publishing
"something ambiguous happened to order X" and having a consumer go and ask the gateway would
put the authority over payment state in a different module from the one that owns the row,
which is the second-authority hazard 031 refused to build half of. What the outbox unblocked
is not the mechanism but the *precedent*: 053 is the argument that a background worker may
own a slow, retrying, at-least-once job without any invariant depending on it, and this is
the second worker built on that argument.

### The three answers, and the fourth that is not one

`GatewayRecord` is a separate enum from `GatewayOutcome` because a lookup answers a different
question — not "what did this call do" but "what, if anything, is on record".

| Record | What it means | What the attempt becomes |
|---|---|---|
| `Authorized` | Funds are held under this key | Released at the gateway, then `Voided` |
| `Declined` | The gateway received it and refused | `Declined` |
| `NotFound` | The gateway has no record: it never arrived | `Abandoned` |
| `Unknown` | The lookup itself got no answer | unchanged, still `TimedOut` |

**`NotFound` against `Unknown` is the distinction the whole thing rests on.** One is an
answer — the gateway looked and there is nothing there — and the other is the absence of one.
Collapsing them would let a failed lookup be read as proof that nothing happened, which is the
worst available reading: it would hand an order its live-attempt slot back while the customer's
funds were still held, and the next confirm would authorise a second time.

### `PaymentStatus.Abandoned`, and why the existing members would each have been a lie

**Rejected: `Declined`.** 031 refused to treat a timeout as a decline because it tells a
customer their card was refused when it may not have been. An attempt the gateway never
received was not refused either; the objection arrives one step later, unchanged.

**Rejected: `Voided`.** A void releases an authorisation that existed. There was none, and a
row claiming otherwise sends whoever chases it to the gateway for a reference that does not
exist.

**Rejected: `Retry` back to `Pending`, leaving the request path to finish it.** Tempting,
because the adapter already knows how to resume a `Pending` row and no new status is needed.
It is worse than doing nothing: `Pending` is live, so the row would keep the order's slot,
and if no further confirm ever came it would sit there forever — the same orphan, wearing a
status that also lies about there being a call in flight.

`Abandoned = 6` is terminal and not live. It costs no migration: the index filter is
`"Status" IN (0, 1, 2, 4)` and a new non-live member is simply absent from it.
`PaymentTests.IsLive_ShouldMatchTheIndexFilter` gained a row, which is the only thing pinning
the enum to that SQL literal.

### A hold it finds is released, not recorded

**The sweep does not write `Authorized` and stop.** That would be bookkeeping: the funds would
still be held, and 031's actual complaint — held until the gateway expires them days later —
would be untouched. It voids, and only then writes, in one transition (`ResolveAsVoided`).

**Why releasing is Payments' call and not an intrusion into Orders' lifecycle.** 028 authorises,
sells, then captures, and a confirm whose authorisation times out returns before selling
anything. So a `TimedOut` row never has sold seats behind it, and 028's own rule — a sale that
does not complete voids the authorisation — is already the rule that applies. The void simply
never happened, because nobody knew there was anything to void. The sweep is not deciding an
order is dead; it is finishing a decision this module already made.

**Nothing is written when the void gets no answer.** Recording the authorisation without having
released it would swap one orphan for a worse one: an `Authorized` row nobody will ever capture
and that no sweep looks at. The row stays `TimedOut` and the next sweep tries again.

**Three transitions, not loosened guards on the existing three.** `ResolveAsVoided`,
`ResolveAsDeclined` and `ResolveAsAbandoned` all refuse anything but `TimedOut`, reusing
`NotTimedOut`. Letting `Authorize` or `Decline` accept a timed-out row would let the ordinary
request path write a settled answer it never actually received, which is exactly the property
the separation protects. They also leave `AttemptedAt` alone: no attempt was made, an answer was
read back, and the funds were held when the original call reached the gateway rather than when
we found out.

### Shape of the worker, and where it deliberately differs from the dispatcher

**No claim, no `FOR UPDATE SKIP LOCKED`.** The dispatcher locks because delivering a message
twice is a real cost. Here the expensive half is a *read* at the gateway, which two instances
may safely duplicate, and the write is arbitrated by `xmin` like every other write in this
module. Holding a Postgres row lock across a call to a third party would be the worse trade by
a distance — a confirm touching that row would block for as long as the gateway felt like
taking. One scope per row, so a row losing on `xmin` leaves the rest of the sweep with a clean
change tracker.

**It always sleeps, even after a full batch — the opposite of the dispatcher.** A message the
dispatcher fails to deliver has its next attempt pushed into the future, so a full batch there
really does mean more work is ready now. A row this fails to resolve is still timed out, still
old enough, and still first in the next sweep's ordering, so looping on a full batch would mean
hammering the gateway with the same unanswerable questions as fast as it can refuse to answer
them.

**`PollInterval` is one minute, not the outbox's one second.** An undelivered event is a fact
the system already owns and is merely late in passing on. An unresolved authorisation is a
question only a third party can answer, and asking more often does not make the answer arrive
sooner. This is also the direct lesson of 056: the outbox's cost was not the write on the hot
path, it was a second workload competing for the same database, and a third one polling every
second would be repeating a mistake that has already been measured once.

**`MinimumAge` is five minutes, which is one seat-hold duration.** A confirm whose authorisation
timed out aborts before selling, so the customer's only route back is another confirm — which
finds the row and retries it under the same key. Past five minutes the seats that confirm was
for have certainly expired, so no confirm that could still succeed is racing the sweep. The race
is survivable either way; this is about not doing pointless work and not voiding an
authorisation somebody is seconds from using.

**`Enabled` defaults true**, on `OutboxOptions.Enabled`'s reasoning rather than
`MigrateOnStartup`'s: a reconciler that did not run by default would silently leave funds held.
`RECONCILER_ENABLED` is a compose variable for the same reason `OUTBOX_ENABLED` is — 056's
baseline needs to be able to switch a background workload off to attribute its cost.

### The simulator had to change, and it corrects an earlier note

`SimulatedPaymentGateway` recorded nothing for a timed-out call, with a comment saying a timeout
is deliberately not remembered so that "the ambiguity is not trivially resolvable and the retry
path stays tested". **That was wrong in an instructive direction, and the note is superseded
rather than deleted.** What the gateway records is not visible to the caller, so recording it
removes no ambiguity from the only side that experiences it — and a retry under the same key
getting a consistent answer back is precisely what a real idempotent gateway does. Never
recording it made one branch of reconciliation *unreachable*: every lookup would have answered
`NotFound`, so "the authorisation landed" could never be produced and the branch that actually
returns somebody's money would have looked tested while being unreachable.

So a timeout is now two events wearing one name. The gateway decides whether the caller hears
anything (`TimeoutRate`), and separately, when they do not, whether the request arrived at all
(`LostRequestRate`, default 0.5). A request lost outbound leaves nothing on record; one whose
answer was lost leaves a decision the gateway repeats when asked. The caller still cannot tell
them apart — that is what makes a timeout ambiguous — but `LookUpAsync` can.

`LostRequestRate` does not reopen the sum-to-one problem `PaymentSimulationOptions` refuses. It
is on a different axis: the first two rates divide every call into answered-yes, answered-no and
unanswered, and this one divides the unanswered ones.

### One new catch on the request path

`InProcessOrderPayments.AuthorizeAsync` now catches `DbUpdateConcurrencyException` on its first
save and answers `ConcurrentAttemptInFlight`. The reconciler is the first writer of these rows
that is not a request, so a confirm that reads a `TimedOut` row, calls `Retry`, and saves after
the sweep has settled it is newly reachable — and without the catch it is a 500 for a situation
the caller can simply retry. It must come before the `IsDuplicateLiveAttempt` clause, which
filters a base type of it.

**Stated plainly: no test forces that interleaving.** Every settled status is non-live, so the
server-side filter in `LiveAsync` excludes a resolved row and the retry path is never entered —
the only way to reach the catch is a conflict landing between that read and that save, and
nothing in the suite can hold the two apart. The clause is defensive, and it is cheaper than the
alternative of discovering it in a log.

### What this does not do

**`Payment` still raises no domain events, so nothing is announced.** 029 refused them on the
grounds that events with no consumer are ceremony, and that is still true of a capture; it is
noticeably less true of "the authorisation we could not account for has been released", which an
order sitting `Pending` would like to know about. Announcing it means Payments getting an outbox
of its own — the drain, the table, the dispatcher, all currently Inventory's — and that is a
second argument that should not ride along inside this one. It also collides with 027, which
chose to resolve `AwaitingCapture` by the next confirm rather than by a background job, and that
choice deserves re-examining on its own terms rather than by implication. **Deferred, named, and
the next obvious chunk of Payments work.**

**One branch has no integration test, deliberately.** A lookup that finds funds and then fails to
release them leaves the row timed out. Reaching it needs the lookup to answer and the void not
to, and both are governed by the single `TimeoutRate` knob — so forcing it would mean adding a
knob to the simulator whose only purpose is to be a test's seam.

**The simulator does not forget a released key.** After the sweep voids, a lookup under that key
would still report the funds held. Nothing looks: an attempt is only reconciled while it is timed
out, and settling it removes it from that set for good. Doing better needs a reference-to-key map,
and `ReferenceFor` is deliberately one-way.

<!-- Expand later: whether Payments gets its own outbox or the reconciler's outcome reaches
     Orders some other way, which is the 029/027 pair above; and whether a reconciled hold should
     ever be captured rather than voided, which only becomes a question if some future path can
     time out an authorisation after seats are already sold. -->
