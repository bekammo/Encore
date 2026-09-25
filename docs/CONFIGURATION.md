# Configuration

Everything Encore reads, where it is set, and what it defaults to. Keys use ASP.NET Core's
`:` form. As environment variables, `:` becomes `__`, so `Inventory:ExposeSeatRoutes` is
`Inventory__ExposeSeatRoutes`.

Values are set in three places:
- **`dotnet run`**: `appsettings.Development.json` holds the connection strings for the
  docker-compose Postgres and Redis, and the launch profile in `Properties/launchSettings.json`
  sets the rest.
- **docker-compose**: every service's `environment` block. A variable in `${VAR:-default}` form
  can be overridden from the shell or from a `.env` file (see [`.env.example`](../.env.example)).
- **Production defaults**: the code. A key with no value falls back to the default below.

## Required

A host with any of these missing refuses to start.

| Key | Used by | Notes |
|---|---|---|
| `ConnectionStrings:Catalog`, `:Orders`, `:Inventory`, `:Payments`, `:Notifications` | each module's `DbContext` | One string per module even when they name one database: the names are the extraction seam (017). |
| `ConnectionStrings:Redis` | Inventory's hold-cap lock | A Redis that is down does not stop the host (004). |
| `Operator:ApiKey` | Catalog and Inventory, when they map operator writes | The `X-Operator-Key` for venues, events and seat maps (030). |
| `Payments:ServiceToken` | `Encore.Payments.Api` only | The `X-Service-Token` guarding `/internal/payments` (018). |
| `Orders:Payments:ServiceToken` | Orders, only when `Orders:Payments:BaseAddress` is set | Must equal the service's `Payments:ServiceToken`. |

## Behaviour

| Key | Default | What it does |
|---|---|---|
| `{Module}:MigrateOnStartup` | `false` | Applies that module's migrations before Kestrel opens. Set only by run profiles (017). |
| `Inventory:ExposeSeatRoutes` | `false` | Maps `/hold`, `/release` and `/purchase`, which sell without an order or a payment (030). |
| `Inventory:HoldCapLock` | `Redis` | `Postgres` serialises the hold cap on an advisory lock instead (005). |
| `Inventory:RedisLock:ConnectTimeoutMs` | `1000` | Redis connect timeout. |
| `Inventory:RedisLock:Cooldown` | `00:00:01` | How long the lock stops asking Redis after a refusal. `00:00:00` asks every time (019). |
| `RateLimiting:PerIp:Enabled` | `true` | Per-IP token bucket on `POST /orders` and the hold route (030). |
| `RateLimiting:PerIp:PermitsPerSecond` | `10` | Tokens added per second. |
| `RateLimiting:PerIp:Burst` | `20` | Bucket size. |
| `Orders:Payments:BaseAddress` | unset | When set, Orders calls Payments over HTTP instead of in process (018). |
| `Orders:Payments:Timeout` | `00:00:10` | Per call to the Payments service. |
| `Orders:Payments:ConnectTimeout` | `00:00:01` | Bounds a stopped container that swallows connections (019). |

## Background jobs

Each can be switched off, so a run can price it. Correctness never depends on any of them (006).

| Section | `Enabled` | Other keys and defaults |
|---|---|---|
| `Inventory:ExpiredHoldSweep` | `true` | `PollInterval` 00:01:00, `BatchSize` 200 |
| `Inventory:Outbox` | `true` | `PollInterval` 00:00:01, `BatchSize` 50, `DeliveryTimeout` 00:00:02, `MaxBatchDuration` 00:00:05, `MaxAttempts` 5, `BaseBackoff` 00:00:02, `MaxBackoff` 00:05:00 |
| `Inventory:Outbox:Retention` | `true` | `KeepDelivered` 30.00:00:00, `PollInterval` 01:00:00, `BatchSize` 1000 |
| `Orders:CaptureSweep` | `true` | `PollInterval` 00:01:00, `BatchSize` 20, `MinimumAge` 00:01:00 (025) |
| `Orders:OrderExpirySweep` | `true` | `PollInterval` 00:01:00, `BatchSize` 20, `Grace` 00:01:00 (031) |
| `Payments:Reconciliation` | `true` | `PollInterval` 00:01:00, `BatchSize` 20, `MinimumAge` 00:05:00 (014) |

## The simulated gateway

`Payments:Simulation` shapes the fake payment provider (014).

| Key | Default | What it does |
|---|---|---|
| `DeclineRate` | `0` | Share of authorisations declined. |
| `TimeoutRate` | `0` | Share of calls that never answer. |
| `LostRequestRate` | `0.5` | Of the unanswered, the share whose request never arrived. It has no effect while `TimeoutRate` is 0. |
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
| `RECONCILER_MIN_AGE` / `RECONCILER_POLL` | `00:05:00` / `00:01:00` | `Payments:Reconciliation:MinimumAge` / `:PollInterval` |
| `REDIS_CONNECT_TIMEOUT_MS` | `1000` | `Inventory:RedisLock:ConnectTimeoutMs` |
| `REDIS_LOCK_COOLDOWN` | `00:00:01` | `Inventory:RedisLock:Cooldown` |
| `HOLD_CAP_LOCK` | `Redis` | `Inventory:HoldCapLock` |
| `PAYMENTS_TIMEOUT_RATE` / `PAYMENTS_LOST_REQUEST_RATE` | `0` / `0.5` | `Payments:Simulation:*` on the Payments service |
| `ENCORE_LOG_LEVEL` | `Information` | `Logging:LogLevel:Encore` |
| `OTEL_ENDPOINT` | empty | `OTEL_EXPORTER_OTLP_ENDPOINT` |

The k6 harness reads its own knobs (`CONTENTION_VUS`, `SALE_SEATS`, `CHAOS_PHASES`, …), passed
with `docker compose run -e`. They are documented at the top of
[`load/flash-sale.js`](../load/flash-sale.js).
