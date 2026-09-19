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
