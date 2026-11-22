---
paths:
  - "src/Encore.Modules.Inventory*/**"
---

# Inventory module

Inventory is the one hexagonal module (001). The rules below come from DECISIONS 002–006
and `Directory.Build.targets`.

## Layers and build rules (002)

- `Encore.Modules.Inventory.Domain` holds `Seat`, its events and its exceptions, in the
  `Encore.Modules.Inventory.Domain` namespace. Its only ProjectReference is `Encore.Shared`.
- Domain, `Inventory.Contracts` and `Encore.Shared` set `<EncoreZeroDependency>true</EncoreZeroDependency>`.
  It turns on these build errors:
  - `ENCORE001`: any `PackageReference`.
  - `ENCORE002`: any `FrameworkReference` other than `Microsoft.NETCore.App`.
  - `ENCORE003`: any non-BCL assembly in the resolved closure, including one arriving through
    a ProjectReference. A package added to `Encore.Shared` therefore fails the Domain too.
  When one fires, move the dependency into `Encore.Modules.Inventory/Adapters/` behind a
  `Ports/` interface. Don't remove the property.
- `Application/` must not name `Encore.Modules.Inventory.Adapters` or any type declared under
  `Adapters/`, migrations included. Depend on a `Ports/` interface instead.
- `Ports/` must not name an adapter type, not even in an XML doc comment.
  A port declares a Domain exception (e.g. `ConcurrentSeatModificationException`), and the
  adapter translates the infrastructure exception into it.
- Telemetry (`ActivitySource`, `Meter`, `Adapters.Telemetry`) is emitted only from `Adapters/`.
  `Application/` and Domain code report through return values instead.

## The seat state machine (003)

- Create a seat only with `Seat.Create`. Change state only through `Hold`, `Release`, `Sell`
  and `ExpireHold`, never by setting `Status`.
- Pass `utcNow` (UTC) to a transition, never an expiry. The aggregate sets the 5-minute hold.
- Keep these rules: re-holding your own seat is a no-op that doesn't move the expiry; a
  lapsed reclaim raises `SeatReleased(Expired)` before `SeatHeld`; there is no
  `Available` → `Sold` path; `Sold` keeps `HeldByClientId` and is terminal; only the holder
  may release; `HoldExpiresAt <= utcNow` counts as expired.
- A new refusal is a new `SeatTransitionReason` member on `SeatTransitionException`, not a
  new exception type. A handler catches only the reasons its transition can produce, maps
  them to its closed outcome enum, and lets the rest propagate.
- A seat command's event id is checked: a mismatch answers `SeatNotFound`. A repeated seat id
  is refused with `400 duplicate_seat`, not de-duplicated.

## Concurrency (004)

- `xmin` (`RowVersion`) settles every race for a seat. Don't add a per-seat lock: removing
  one bought 18% throughput (015).
- Retry a lost race exactly once: detach the stale seat, call `ClearDomainEvents()`, reload.
  Don't write a retry loop.
- Redis is never a correctness dependency. The lock adapter maps only Redis connection and
  timeout exceptions to `Unavailable`. Keep `AbortOnConnectFail = false` and release with
  `CancellationToken.None`.

## The hold cap (005)

- The cap is `SeatReservationLimits.MaxHoldsPerClientPerEvent` (4). It stays `static readonly`,
  because a `const` is baked into callers and would go stale once the call crosses a process.
- `Application/` enforces the cap with a Postgres count under the client + event lock
  (`ClientHoldLock`). `HeldByAnother` refuses with `ConcurrentRequestInFlight`. `Unavailable`
  proceeds and accepts a possible breach: keep it that way rather than refusing holds.
- `Inventory:HoldCapLock` selects `Redis` (default) or `Postgres`. A new lock implementation
  must pass the cap suite that runs against both.

## Expiry (006)

- Every read and write path treats a `Held` seat with `HoldExpiresAt <= utcNow` as `Available`,
  hold counts included. Correctness must not depend on `ExpiredHoldSweeper`. Cover new
  expiry-dependent behaviour in `ExpiryWithoutTheSweepTests`.
- The sweep's SQL only nominates seat ids, and it calls `Seat.ExpireHold` per seat. Use that
  instead of a bulk `UPDATE`, which would publish no events.
- `ExpireHold` sets only `Status` and keeps `HeldByClientId` / `HoldExpiresAt`, so `Sell`
  answers `HoldExpired` the same before and after a sweep. Only a release, a sale or a new
  hold changes that pair. Anything that counts holds filters on `Status = Held`.
