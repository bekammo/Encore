# Configuration

Every setting Encore reads, where it's set, and its default. Keys use ASP.NET Core's `:`
form; as environment variables, `:` becomes `__`, so `Inventory:ExposeSeatRoutes` is
`Inventory__ExposeSeatRoutes`. A number in brackets, like (014), is the entry in
[DECISIONS.md](../DECISIONS.md) that explains the setting.

Values come from three places:
- **`dotnet run`**: `appsettings.Development.json` holds the connection strings for the
  docker-compose Postgres and Redis, and the launch profile in
  `Properties/launchSettings.json` sets the rest.
- **docker-compose**: each service's `environment` block. A variable written as
  `${VAR:-default}` can be overridden from the shell or a `.env` file (see
  [`.env.example`](../.env.example)).
- **Anywhere else**: the defaults in code, listed below.

## Required

There's no fallback for these. A host refuses to start if a secret it needs is missing. A
missing connection string fails the first time its module touches the database, which is at
startup when that module migrates on startup.

| Key | Used by | Notes |
|---|---|---|
| `ConnectionStrings:Catalog`, `:Orders`, `:Inventory`, `:Payments`, `:Notifications` | each module's `DbContext` | One string per module, even when they all name one database: the names are the extraction seam (017). |
| `ConnectionStrings:Redis` | Inventory's hold-cap lock | Never read when `Inventory:HoldCapLock` is `Postgres`. A Redis that's down doesn't stop the host (004). |
| `Operator:ApiKey` | Catalog and Inventory, when they map operator writes | The `X-Operator-Key` for venues, events and seat maps (030). |
| `Payments:ServiceToken` | `Encore.Payments.Api` only | The `X-Service-Token` guarding `/internal/payments` (018). |
| `Orders:Payments:ServiceToken` | Orders, only when `Orders:Payments:BaseAddress` is set | Must equal the service's `Payments:ServiceToken`. |

## Behaviour

| Key | Default | What it does |
|---|---|---|
| `{Module}:MigrateOnStartup` | `false` | Applies that module's migrations before Kestrel opens. Only the run profiles set it (017). |
| `Inventory:ExposeSeatRoutes` | `false` | Maps `/hold`, `/release` and `/purchase`, which sell without an order or a payment (030). |
| `Inventory:HoldCapLock` | `Redis` | `Postgres` serialises the hold cap on an advisory lock instead (005). |
| `Inventory:RedisLock:ConnectTimeoutMs` | `1000` | Redis connect timeout. |
| `Inventory:RedisLock:Cooldown` | `00:00:01` | After Redis fails to answer, how long the lock stops asking it. `00:00:00` asks every time (019). |
| `RateLimiting:PerIp:Enabled` | `true` | Per-IP token bucket on `POST /orders` and the hold route (030). |
| `RateLimiting:PerIp:PermitsPerSecond` | `10` | Tokens added per second. |
| `RateLimiting:PerIp:Burst` | `20` | Bucket size. |
| `Orders:Payments:BaseAddress` | unset | When set, Orders calls Payments over HTTP instead of in process (018). |
| `Orders:Payments:Timeout` | `00:00:10` | Per call to the Payments service. |
| `Orders:Payments:ConnectTimeout` | `00:00:01` | Bounds a stopped container that swallows connections (019). |

## Background jobs

Each job can be switched off, so a run can measure what it costs. No invariant depends on any
of them (006): with one off, nothing is oversold, charged twice or partly sold. What stops is
the follow-up work, and the last column says what's left waiting.

| Section | `Enabled` | Other keys and defaults | Switched off |
|---|---|---|---|
| `Inventory:ExpiredHoldSweep` | `true` | `PollInterval` 00:01:00, `BatchSize` 200 | Lapsed rows still read `Held`; every path already treats them as available. |
| `Inventory:Outbox` | `true` | `PollInterval` 00:00:01, `BatchSize` 50, `DeliveryTimeout` 00:00:02, `MaxBatchDuration` 00:00:05, `MaxAttempts` 5, `BaseBackoff` 00:00:02, `MaxBackoff` 00:05:00 | Events pile up undelivered (016). |
| `Inventory:Outbox:Retention` | `true` | `KeepDelivered` 30.00:00:00, `PollInterval` 01:00:00, `BatchSize` 1000 | Delivered rows are kept for good. |
| `Orders:CaptureSweep` | `true` | `PollInterval` 00:01:00, `BatchSize` 20, `MinimumAge` 00:01:00 | An order owed its capture waits for the customer to confirm again. If they never do, the authorisation lapses with the seats sold (025). |
| `Orders:OrderExpirySweep` | `true` | `PollInterval` 00:01:00, `BatchSize` 20, `Grace` 00:01:00 | An abandoned order stays `Pending`, blocks the customer's next checkout for the event, and keeps its authorisation held (031). |
| `Payments:Reconciliation` | `true` | `PollInterval` 00:01:00, `BatchSize` 20, `MinimumAge` 00:05:00 | A timed-out authorisation that did hold funds keeps them held (014). |

## The simulated gateway

`Payments:Simulation` shapes the fake payment provider (014).

| Key | Default | What it does |
|---|---|---|
| `DeclineRate` | `0` | Share of authorisations declined. |
| `TimeoutRate` | `0` | Share of calls that never answer. |
| `LostRequestRate` | `0.5` | Of the authorisations that never answer, the share whose request never arrived. No effect while `TimeoutRate` is 0. |
| `CaptureDeclineRate` | `0` | Of answered captures, the share refused; the order becomes `payment_due` (034). |
| `MinLatency` / `MaxLatency` | `00:00:00.050` / `00:00:00.250` | Each call's simulated latency. |
| `Seed` | unset | Fixes the random sequence. |

## Telemetry

| Variable | Default | What it does |
|---|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | unset | Telemetry is off unless this names a collector (021). |
| `OTEL_METRIC_EXPORT_INTERVAL` | 5000 ms when unset | The SDK's own default is 60 s, too long for a one-minute sale. |

## docker-compose variables

| Variable | Default | Sets |
|---|---|---|
| `OPERATOR_API_KEY` | `local-operator-key` | `Operator:ApiKey` on the API, and the key k6's setup sends |
| `PAYMENTS_SERVICE_TOKEN` | `local-development-token` | `Payments:ServiceToken` and `Orders:Payments:ServiceToken` |
| `RATE_LIMIT_ENABLED` | `false` | `RateLimiting:PerIp:Enabled`. Off because every k6 request comes from one address |
| `OUTBOX_ENABLED` | `true` | `Inventory:Outbox:Enabled` |
| `SWEEP_ENABLED` | `true` | `Inventory:ExpiredHoldSweep:Enabled` |
| `ORDER_EXPIRY_SWEEP_ENABLED` | `true` | `Orders:OrderExpirySweep:Enabled` |
| `RECONCILER_ENABLED` | `true` | `Payments:Reconciliation:Enabled` on the monolith |
| `RECONCILER_EVERYWHERE` | `false` | A second reconciler on the strangled monolith, to test their lock (014) |
| `RECONCILER_MIN_AGE` / `RECONCILER_POLL` | `00:05:00` / `00:01:00` | `Payments:Reconciliation:MinimumAge` / `:PollInterval`, on `payments-api` and `api-strangled` only |
| `REDIS_CONNECT_TIMEOUT_MS` | `1000` | `Inventory:RedisLock:ConnectTimeoutMs` |
| `REDIS_LOCK_COOLDOWN` | `00:00:01` | `Inventory:RedisLock:Cooldown` |
| `HOLD_CAP_LOCK` | `Redis` | `Inventory:HoldCapLock` |
| `PAYMENTS_TIMEOUT_RATE` / `PAYMENTS_LOST_REQUEST_RATE` | `0` / `0.5` | `Payments:Simulation:*` on the Payments service |
| `PAYMENTS_CAPTURE_DECLINE_RATE` | `0` | `Payments:Simulation:CaptureDeclineRate` on the Payments service |
| `ENCORE_LOG_LEVEL` | `Information` | `Logging:LogLevel:Encore`, on `payments-api` and `api-strangled` only |
| `OTEL_ENDPOINT` | empty | `OTEL_EXPORTER_OTLP_ENDPOINT` |

The k6 harness reads its own knobs (`CONTENTION_VUS`, `SALE_SEATS`, `CHAOS_PHASES`, …), passed
with `docker compose run -e` and documented at the top of
[`load/flash-sale.js`](../load/flash-sale.js).

`dotnet ef` builds each context through a design-time factory that reads
`ENCORE_{CATALOG,ORDERS,INVENTORY,PAYMENTS,NOTIFICATIONS}_CONNECTION`, falling back to the
docker-compose Postgres on port 55432.
