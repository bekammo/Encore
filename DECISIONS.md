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
