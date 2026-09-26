#!/usr/bin/env bash
#
# Encore's chaos harness (019). k6 drives the traffic and asserts the invariants; this
# script owns the timeline, injects the faults and reads the evidence back out of
# Postgres, writing one report per session to load/results/.
#
#   bash load/chaos.sh                 every run below, in order
#   bash load/chaos.sh redis stall     only those
#   bash load/chaos.sh baseline        the strangled baseline on its own
#   bash load/chaos.sh orders          multi-seat orders, confirm racing cancel
#
# One fault per run, so one fault's aftermath (a backlog, unresolved payments, a drained
# seat map) does not feed the next measurement, and a failed threshold does not take the
# other runs down. The orders run injects nothing: its fault is the customer.
#
# Everything runs against the `strangled` profile, because stopping the Payments service
# needs a Payments service.
#
# Not set -e: a k6 run that fails a threshold is a result to record, not an error.
set -uo pipefail

cd "$(dirname "$0")/.." || exit 1

RESULTS_DIR="load/results"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
REPORT="${RESULTS_DIR}/chaos-${STAMP}.md"

COMPOSE="docker compose --profile strangled"

# Every table the runs touch, truncated between runs so each run's evidence is its own.
# Parents first; CASCADE does the rest. A table missing here accumulates across runs.
TABLES='catalog.venues, catalog.events, inventory.seats, inventory.outbox_messages, notifications.notifications, orders.orders, orders.order_lines, payments.payments, payments.gateway_ledger'

# Short control windows for the fault runs, where they are a warm-up and a same-day
# control. The baseline run keeps k6's full-length defaults.
CONTROL_CONTENTION_SECONDS=15
CONTROL_SALE_SECONDS=15

# k6's gap between scenarios, mirrored from the script: faults are injected in the gaps.
GAP=5

mkdir -p "$RESULTS_DIR"

# ---------------------------------------------------------------------------
# Plumbing
# ---------------------------------------------------------------------------

say() {
  printf '\n\033[1m== %s\033[0m\n' "$*"
}

report() {
  printf '%s\n' "$*" >> "$REPORT"
}

# One scalar or small table from Postgres; -At leaves only the answer.
psql_q() {
  $COMPOSE exec -T postgres psql -U encore -d encore -At -F' | ' -c "$1" 2>/dev/null
}

# Drops k6's per-second progress lines, which otherwise bury the results.
k6_digest() {
  grep -vE '^running \(|^ *(contention|flash_sale|redis_[a-z_]+|payments_[a-z_]+|stall_sale|reconciler_checkout|orders_mixed|Run) +(•|✓|↓|\[)| Container |^ *$' "$1"
}

# Captures a container's whole log once, so every count reads the same instant. EF Core
# logs every command, so the log is large and one pass matters.
capture_log() {
  $COMPOSE logs "$1" --no-log-prefix > "$2" 2>/dev/null
}

reset_data() {
  psql_q "TRUNCATE ${TABLES} RESTART IDENTITY CASCADE;" > /dev/null
}

# Brings the stack up with the current exports. Compose recreates a container whose
# environment changed, which is how a startup-only setting moves. Built once per
# session: the source cannot change between runs, and a stale build is a wrong baseline.
BUILT=0

bring_up() {
  say "compose up ($*)"

  if [ "$BUILT" -eq 0 ]; then
    $COMPOSE up -d --wait --build postgres redis payments-api api-strangled || return 1
    BUILT=1
    return 0
  fi

  $COMPOSE up -d --wait postgres redis payments-api api-strangled
}

# Waits for the marker venue k6's setup() writes last. The clock starts when this
# returns, so faults land in the planned window rather than wherever setup left off.
await_marker() {
  local marker="$1"
  local waited=0

  while [ "$waited" -lt 180 ]; do
    if [ "$(psql_q "SELECT count(*) FROM catalog.venues WHERE \"Name\" = '${marker}';")" = "1" ]; then
      return 0
    fi
    sleep 1
    waited=$((waited + 1))
  done

  echo "chaos: the marker '${marker}' never appeared; the run's setup probably failed." >&2
  return 1
}

# ---------------------------------------------------------------------------
# Evidence
# ---------------------------------------------------------------------------

seat_evidence() {
  report ''
  report '```'
  report "seats by status (0 available, 1 held, 2 sold)"
  report "$(psql_q 'SELECT "Status", count(*) FROM inventory.seats GROUP BY 1 ORDER BY 1;')"
  report ''
  report "seats sold with no owner (must be 0): $(psql_q 'SELECT count(*) FROM inventory.seats WHERE "Status" = 2 AND "HeldByClientId" IS NULL;')"
  report "seats total: $(psql_q 'SELECT count(*) FROM inventory.seats;')"
  report '```'
}

outbox_evidence() {
  report ''
  report '```'
  report "outbox: $(psql_q 'SELECT count(*) FILTER (WHERE "ProcessedAt" IS NOT NULL) || '"'"' delivered, '"'"' || count(*) FILTER (WHERE "ProcessedAt" IS NULL) || '"'"' pending, '"'"' || count(*) FILTER (WHERE "ProcessedAt" IS NULL AND "Attempts" >= 5) || '"'"' dead-lettered, '"'"' || count(*) FILTER (WHERE "Attempts" > 0) || '"'"' retried'"'"' FROM inventory.outbox_messages;')"
  report "notifications written: $(psql_q 'SELECT count(*) FROM notifications.notifications;')"
  report ''
  report 'delivery latency, CreatedAt - OccurredAt, milliseconds'
  report "$(psql_q 'SELECT round(percentile_disc(0.5) WITHIN GROUP (ORDER BY EXTRACT(EPOCH FROM ("CreatedAt" - "OccurredAt")) * 1000)) || '"'"' med, '"'"' || round(percentile_disc(0.95) WITHIN GROUP (ORDER BY EXTRACT(EPOCH FROM ("CreatedAt" - "OccurredAt")) * 1000)) || '"'"' p95, '"'"' || round(percentile_disc(0.99) WITHIN GROUP (ORDER BY EXTRACT(EPOCH FROM ("CreatedAt" - "OccurredAt")) * 1000)) || '"'"' p99, '"'"' || round(max(EXTRACT(EPOCH FROM ("CreatedAt" - "OccurredAt")) * 1000)) || '"'"' max'"'"' FROM notifications.notifications;')"
  report '```'
}

money_evidence() {
  report ''
  report '```'
  report 'orders by status (0 pending, 1 confirmed, 2 cancelled, 3 expired, 4 failed, 5 awaiting_capture, 6 payment_due)'
  report "$(psql_q 'SELECT "Status", count(*) FROM orders.orders GROUP BY 1 ORDER BY 1;')"
  report ''
  report 'payments by status (0 pending, 1 authorized, 2 captured, 3 declined, 4 timed_out, 5 voided, 6 abandoned)'
  report "$(psql_q 'SELECT "Status", count(*) FROM payments.payments GROUP BY 1 ORDER BY 1;')"
  report ''
  report "orders with more than one live payment attempt, live as ux_payments_order_live counts it (must be 0): $(psql_q 'SELECT count(*) FROM (SELECT "OrderId" FROM payments.payments WHERE "Status" IN (0, 1, 2, 4) GROUP BY 1 HAVING count(*) > 1) AS breaches;')"
  report '```'
}

# ---------------------------------------------------------------------------
# The runs
# ---------------------------------------------------------------------------

# The only run with no fault: the baseline scenarios against the strangled pair, with the
# same parameters as the monolith's baseline (019).
run_baseline() {
  say 'run 1/5 — baseline on the extracted configuration'
  reset_data

  $COMPOSE run --rm \
    -e RUN_LABEL=baseline-strangled \
    load-strangled > "$REPORT.tmp" 2>&1

  local k6status=$?

  report '## Baseline — the extracted configuration, no fault'
  report ''
  report 'The third load configuration (019): the same 50 VUs on 5 seats then 100 VUs'
  report 'on 500 seats, against `api-strangled` + `payments-api` rather than the monolith.'
  report 'Neither scenario touches Orders or Payments, so what this measures is the cost of'
  report 'the arrangement rather than of the extraction itself.'
  report ''
  report "k6 exit status: ${k6status} (0 means every invariant it asserts held)"
  report ''
  report '```'
  report "$(k6_digest "$REPORT.tmp")"
  report '```'
  rm -f "$REPORT.tmp"

  seat_evidence
  outbox_evidence
}

# Fault 3. Redis stopped between two identical windows; stopping it in the gap keeps one
# window from blending both states into one number.
#
# A purchase takes no lock and a hold takes the client lock (004, 005), so buy latencies
# are the control and hold latencies are what losing Redis costs. About 1,000 ms per hold
# would mean FailFast is not in effect (019).
run_redis() {
  say 'run 2/5 — Redis stopped mid-run'
  reset_data

  local marker="chaos-marker-redis-$$"
  local up_start=$((CONTROL_CONTENTION_SECONDS + GAP + CONTROL_SALE_SECONDS + GAP))
  local redis_seconds=30
  local down_start=$((up_start + redis_seconds + GAP))

  $COMPOSE run --rm \
    -e RUN_LABEL=redis \
    -e CHAOS_PHASES=redis \
    -e CHAOS_MARKER="$marker" \
    -e CONTENTION_SECONDS=$CONTROL_CONTENTION_SECONDS \
    -e SALE_SECONDS=$CONTROL_SALE_SECONDS \
    -e REDIS_SECONDS=$redis_seconds \
    load-strangled > "$REPORT.tmp" 2>&1 &

  local k6pid=$!

  await_marker "$marker" || { wait $k6pid; return 1; }
  local t0=$SECONDS

  # Two seconds into the gap, so the stop lands between windows despite clock drift.
  sleep_until $((down_start - GAP + 2)) "$t0"
  say 'stopping redis'
  $COMPOSE stop redis

  sleep_until $((down_start + redis_seconds + 2)) "$t0"
  say 'starting redis'
  $COMPOSE start redis

  wait $k6pid
  local k6status=$?

  report '## Fault 3 — Redis stopped mid-run'
  report ''
  report 'Two windows of identical shape — 200 VUs of hold-and-release on 5 seats, plus 50'
  report 'VUs of hold-and-purchase on 500 — with Redis stopped for the second. The lock is'
  report 'supposed to be a contention optimiser and not the correctness mechanism, so what'
  report 'is under test is that losing it changes the numbers and nothing else.'
  report ''
  report "k6 exit status: ${k6status} (0 means every invariant it asserts held)"
  report ''
  report '```'
  report "$(k6_digest "$REPORT.tmp")"
  report '```'
  rm -f "$REPORT.tmp"

  seat_evidence
}

# Fault 1. Stop the Payments service between two windows of real checkouts.
#
# Reconciliation needs authorisations that reached the gateway and lost their answer,
# which a stopped service cannot produce; PAYMENTS_TIMEOUT_RATE produces them (014).
run_payments() {
  say 'run 3/5 — payments-api stopped mid-flash-sale'
  reset_data

  export PAYMENTS_TIMEOUT_RATE=0.35
  export PAYMENTS_CAPTURE_DECLINE_RATE=0
  export RECONCILER_MIN_AGE=00:00:20
  export RECONCILER_POLL=00:00:05
  # The reconciler logs an unanswered lookup and a lost xmin race at Debug; both are counted.
  export ENCORE_LOG_LEVEL=Debug
  bring_up 'payments fault'
  reset_data

  local marker="chaos-marker-payments-$$"
  local up_start=$((CONTROL_CONTENTION_SECONDS + GAP + CONTROL_SALE_SECONDS + GAP))
  local payments_seconds=20
  local down_start=$((up_start + payments_seconds + GAP))

  $COMPOSE run --rm \
    -e RUN_LABEL=payments \
    -e CHAOS_PHASES=payments \
    -e CHAOS_MARKER="$marker" \
    -e CONTENTION_SECONDS=$CONTROL_CONTENTION_SECONDS \
    -e SALE_SECONDS=$CONTROL_SALE_SECONDS \
    -e PAYMENTS_SECONDS=$payments_seconds \
    -e PAYMENTS_VUS=5 \
    -e CHECKOUT_SEATS=6000 \
    load-strangled > "$REPORT.tmp" 2>&1 &

  local k6pid=$!

  await_marker "$marker" || { wait $k6pid; return 1; }
  local t0=$SECONDS

  sleep_until $((down_start - GAP + 2)) "$t0"
  say 'stopping payments-api'
  $COMPOSE stop payments-api

  report '## Fault 1 — payments-api stopped mid-flash-sale'
  report ''
  report 'Two windows of one-seat checkouts, with the Payments service stopped for the'
  report 'second. 018 claims that an unreadable answer becomes `TimedOut`, that the order'
  report 'stays `Pending` and that no seat is sold against funds nobody holds. This asks'
  report 'that of a service that is genuinely not there, under load, rather than of a'
  report 'stubbed handler.'
  report ''
  report '### Immediately after the outage window, before the service came back'

  sleep_until $((down_start + payments_seconds + 2)) "$t0"
  money_evidence

  say 'starting payments-api'
  $COMPOSE start payments-api

  wait $k6pid
  local k6status=$?

  report ''
  report "k6 exit status: ${k6status} (0 means every invariant it asserts held)"
  report ''
  report '```'
  report "$(k6_digest "$REPORT.tmp")"
  report '```'
  rm -f "$REPORT.tmp"

  say 'waiting 90s for the reconciler to settle what it can'
  sleep 90

  report '### After 90 seconds of reconciliation'
  report ''
  report 'A `TimedOut` row only exists where the authorisation actually reached the gateway'
  report 'and the answer was lost. A confirm made while the service was stopped never'
  report 'created a row at all, which is a distinction worth keeping in view when reading'
  report 'the counts below.'
  money_evidence
  seat_evidence

  report ''
  report '```'
  # Named for the run: fault 2 captures payments-api too, and a shared name was overwritten.
  local pay="${RESULTS_DIR}/log-payments-api-payments-${STAMP}.txt"
  capture_log payments-api "$pay"

  report 'reconciler said:'
  report "  $(grep -c 'reconciled:' "$pay") settled"
  report "  $(grep -c 'reported Authorized' "$pay") of them found funds held, and voided them"
  report "  $(grep -c 'reported NotFound' "$pay") of them the gateway had no record of, and were abandoned"
  report "  $(grep -c 'still unresolved' "$pay") left alone because the lookup itself went unanswered"
  report "  $(grep -c 'but the void got no answer' "$pay") left timed out because the void went unanswered"
  report '```'
}

# Fault 4. Block the outbox's consumer rather than the dispatcher, which has no switch and
# should not get one for a chaos run. Locking notifications.notifications stalls SeatSold's
# delivery (the only event with a handler) while the seat path, on another table, carries
# on: delivery separated from the request path (016).
run_stall() {
  say 'run 4/5 — the outbox dispatcher stalled behind its consumer'
  reset_data

  local marker="chaos-marker-stall-$$"
  local start=$((CONTROL_CONTENTION_SECONDS + GAP + CONTROL_SALE_SECONDS + GAP))
  local stall_at=$((start + 15))
  local stall_seconds=20

  $COMPOSE run --rm \
    -e RUN_LABEL=stall \
    -e CHAOS_PHASES=stall \
    -e CHAOS_MARKER="$marker" \
    -e CONTENTION_SECONDS=$CONTROL_CONTENTION_SECONDS \
    -e SALE_SECONDS=$CONTROL_SALE_SECONDS \
    load-strangled > "$REPORT.tmp" 2>&1 &

  local k6pid=$!

  await_marker "$marker" || { wait $k6pid; return 1; }
  local t0=$SECONDS

  sleep_until "$stall_at" "$t0"
  say "locking notifications.notifications for ${stall_seconds}s"

  # The lock lives as long as the transaction, so the sleep runs inside psql. 20s stays
  # under Npgsql's 30s command timeout: this asks what a stall does to the backlog, not
  # how retries and backoff behave.
  psql_q "BEGIN; LOCK TABLE notifications.notifications IN ACCESS EXCLUSIVE MODE; SELECT pg_sleep(${stall_seconds}); COMMIT;" > /dev/null &
  local lockpid=$!

  sleep 5
  report '## Fault 4 — the outbox dispatcher stalled'
  report ''
  report 'A paced 100 sales a second for 60 seconds, with `notifications.notifications`'
  report 'held under ACCESS EXCLUSIVE for 20 of them. `SeatSold` is the only event with a'
  report "registered handler, so this blocks delivery at the consumer's INSERT and leaves"
  report 'the seat path — another schema, another table — completely alone.'
  report ''
  report '### Backlog while the consumer was blocked'
  report ''
  report '```'

  # One round trip per sample: each `compose exec` costs about a second and skews the
  # timestamps. The notifications timestamps are the numbers to trust; this shows the shape.
  local expires=$((stall_at + stall_seconds))
  local samples=0

  while [ $samples -lt 8 ]; do
    report "t+$((SECONDS - t0))s  $(psql_q 'SELECT '"'"'pending: '"'"' || count(*) FILTER (WHERE "ProcessedAt" IS NULL) || '"'"'  delivered: '"'"' || count(*) FILTER (WHERE "ProcessedAt" IS NOT NULL) FROM inventory.outbox_messages;')"
    sleep 2
    samples=$((samples + 1))
  done

  wait $lockpid

  # When the lock was due to expire: pg_sleep is exact, the sampling loop is not.
  say 'lock released'
  report "the lock was released at t+${expires}s"
  report '```'
  report ''
  report '### Recovery'
  report ''
  report '```'

  # Drained means nothing undelivered is older than 5s (MaxBatchDuration), not that pending
  # is zero, which never happens while sales keep landing. Steady-state delivery takes
  # about 1s, so a younger message is in flight, not backlog.
  local drained=-1
  local counts pending stale

  while [ $((SECONDS - t0 - expires)) -lt 120 ]; do
    counts=$(psql_q 'SELECT count(*) FILTER (WHERE "ProcessedAt" IS NULL), count(*) FILTER (WHERE "ProcessedAt" IS NULL AND "OccurredAt" < now() - interval '"'"'5 seconds'"'"') FROM inventory.outbox_messages;')
    pending="${counts%% | *}"
    stale="${counts##* | }"
    report "t+$((SECONDS - t0))s  pending: ${pending}  stale (undelivered, >5s old): ${stale}"

    if [ "$stale" = "0" ]; then
      drained=$((SECONDS - t0 - expires))
      break
    fi

    sleep 2
  done

  if [ "$drained" -ge 0 ]; then
    report "backlog first observed drained (nothing undelivered older than 5s) ${drained}s after the lock expired"
  else
    report 'backlog was still draining when this stopped watching'
  fi

  report ''
  report 'The sampling above is coarse. The precise answer is the delivery-latency'
  report 'distribution below, which is computed from CreatedAt - OccurredAt on the'
  report 'rows themselves and owes nothing to how fast this loop could poll.'
  report '```'

  wait $k6pid
  local k6status=$?

  report ''
  report "k6 exit status: ${k6status} (0 means every invariant it asserts held)"
  report ''
  report '```'
  report "$(k6_digest "$REPORT.tmp")"
  report '```'
  rm -f "$REPORT.tmp"

  seat_evidence
  outbox_evidence
}

# Fault 2. Two reconcilers over one table, the single-owner rule broken on purpose. Each
# sweep takes an advisory lock (014), so the expectation is zero lost xmin races.
run_reconcilers() {
  say 'run 5/5 — two reconcilers over one table'
  reset_data

  export PAYMENTS_TIMEOUT_RATE=0.5
  export PAYMENTS_CAPTURE_DECLINE_RATE=0
  export RECONCILER_MIN_AGE=00:00:20
  export RECONCILER_POLL=00:00:05
  export RECONCILER_EVERYWHERE=true
  # The reconciler logs a lost race at Debug, and that line is the question here.
  export ENCORE_LOG_LEVEL=Debug
  bring_up 'two reconcilers'
  reset_data

  local marker="chaos-marker-reconcilers-$$"

  $COMPOSE run --rm \
    -e RUN_LABEL=reconcilers \
    -e CHAOS_PHASES=reconciler \
    -e CHAOS_MARKER="$marker" \
    -e CONTENTION_SECONDS=$CONTROL_CONTENTION_SECONDS \
    -e SALE_SECONDS=$CONTROL_SALE_SECONDS \
    -e RECONCILER_VUS=5 \
    -e CHECKOUT_SEATS=6000 \
    load-strangled > "$REPORT.tmp" 2>&1

  local k6status=$?

  report '## Fault 2 — two reconcilers over one table'
  report ''
  report 'The reconciler is meant to run in exactly one process, and only a compose setting'
  report 'says so. This run turns the setting on in both processes on purpose. Each sweep'
  report 'takes a Postgres advisory lock first (014), so the question'
  report 'is whether that lease holds: both processes settle work, none of it twice, and no'
  report 'sweep loses an xmin race.'
  report ''
  report "k6 exit status: ${k6status}"
  report ''
  report '```'
  report "$(k6_digest "$REPORT.tmp")"
  report '```'
  rm -f "$REPORT.tmp"

  say 'waiting 90s for both reconcilers to sweep'
  sleep 90

  money_evidence

  # Both logs captured once, first, so every count below reads the same moment. Each is
  # over a million lines, so one pass matters.
  local pay="${RESULTS_DIR}/log-payments-api-reconcilers-${STAMP}.txt"
  local mono="${RESULTS_DIR}/log-api-strangled-reconcilers-${STAMP}.txt"

  say 'capturing both logs'
  capture_log payments-api "$pay"
  capture_log api-strangled "$mono"

  local a b both
  a=$(grep -o 'Payment [0-9a-f-]\{36\} reconciled' "$pay" | awk '{print $2}' | sort -u)
  b=$(grep -o 'Payment [0-9a-f-]\{36\} reconciled' "$mono" | awk '{print $2}' | sort -u)
  both=$(comm -12 <(printf '%s\n' "$a") <(printf '%s\n' "$b") | grep -c .)

  report ''
  report '```'
  report "payments-api settled:    $(printf '%s\n' "$a" | grep -c .) attempts"
  report "api-strangled settled:   $(printf '%s\n' "$b" | grep -c .) attempts"
  report "settled by both:         ${both} attempts"
  report ''
  report "lost the xmin race:      $(( $(grep -c 'was resolved by something else' "$pay") + $(grep -c 'was resolved by something else' "$mono") ))"
  report "lookup went unanswered:  $(( $(grep -c 'still unresolved' "$pay") + $(grep -c 'still unresolved' "$mono") ))"
  report ''
  report 'what each process decided the gateway had said:'
  report "  payments-api  authorized: $(grep -c 'reported Authorized' "$pay")  declined: $(grep -c 'reported Declined' "$pay")  not-found: $(grep -c 'reported NotFound' "$pay")"
  report "  api-strangled authorized: $(grep -c 'reported Authorized' "$mono")  declined: $(grep -c 'reported Declined' "$mono")  not-found: $(grep -c 'reported NotFound' "$mono")"
  report '```'
}

# Multi-seat orders: confirmed, cancelled, and both at once. No fault is injected; the
# customer is the fault. Puts the all-or-none sale (011) and the seats-before-money cancel
# (012) under load. The gateway answers everything and refuses one capture in twenty, so
# every order has a readable ending, payment_due included (034).
run_orders() {
  say 'orders — multi-seat checkouts, confirm racing cancel'
  reset_data

  export PAYMENTS_TIMEOUT_RATE=0
  export PAYMENTS_CAPTURE_DECLINE_RATE=0.05
  export RECONCILER_EVERYWHERE=false
  export ENCORE_LOG_LEVEL=Information
  bring_up 'orders'
  reset_data

  $COMPOSE run --rm \
    -e RUN_LABEL=orders \
    -e CHAOS_PHASES=orders \
    -e CONTENTION_SECONDS=$CONTROL_CONTENTION_SECONDS \
    -e SALE_SECONDS=$CONTROL_SALE_SECONDS \
    load-strangled > "$REPORT.tmp" 2>&1

  local k6status=$?

  report '## Orders — multi-seat checkouts, and a confirm racing a cancel'
  report ''
  report 'One to four seats per order, then a confirm, a cancel, or both sent at once.'
  report 'k6 can only see answers. What has to hold is about rows: no order partly sold'
  report '(011), no order whose seats sold without the money being taken or held, and no'
  report 'money taken for seats that did not sell (012). One capture in twenty is refused; that'
  report 'order must read payment_due, and its customer confirms once more (034). Those are read below.'
  report ''
  report "k6 exit status: ${k6status} (0 means every invariant it asserts held)"
  report ''
  report '```'
  report "$(k6_digest "$REPORT.tmp")"
  report '```'
  rm -f "$REPORT.tmp"

  money_evidence
  order_evidence
}

# The orders phase's invariants, one row per order. A seat counts as this order's
# sale when it is sold to the order's client: every iteration uses a fresh client
# id, so a client's sold seats can only have come from its one order.
order_evidence() {
  local q
  q=$(cat <<'SQL'
WITH per_order AS (
  SELECT o."Id" AS order_id,
         o."Status" AS status,
         count(*) AS seats,
         count(*) FILTER (WHERE s."Status" = 2 AND s."HeldByClientId" = o."ClientId") AS sold
  FROM orders.orders o
  JOIN orders.order_lines l ON l."OrderId" = o."Id"
  JOIN inventory.seats s ON s."Id" = l."SeatId"
  GROUP BY o."Id", o."Status"
),
money AS (
  SELECT "OrderId",
         bool_or("Status" = 2) AS captured,
         bool_or("Status" = 1) AS authorized
  FROM payments.payments
  GROUP BY "OrderId"
),
checked AS (
  SELECT per_order.*,
         coalesce(money.captured, false) AS captured,
         coalesce(money.authorized, false) AS authorized
  FROM per_order
  LEFT JOIN money ON money."OrderId" = per_order.order_id
)
SELECT v.label, v.value
FROM (
  SELECT
    count(*) AS orders,
    count(*) FILTER (WHERE seats > 1) AS multi_seat,
    count(*) FILTER (WHERE sold > 0 AND sold < seats) AS partly_sold,
    count(*) FILTER (WHERE sold > 0 AND status NOT IN (1, 5, 6)) AS sold_not_confirmed,
    count(*) FILTER (WHERE sold > 0 AND NOT captured AND NOT authorized AND status <> 6) AS sold_no_money,
    count(*) FILTER (WHERE captured AND sold < seats) AS paid_not_sold,
    count(*) FILTER (WHERE status = 1 AND NOT captured) AS confirmed_not_captured,
    count(*) FILTER (WHERE status IN (2, 3, 4) AND authorized) AS ended_still_authorized,
    count(*) FILTER (WHERE status = 6) AS payment_due,
    count(*) FILTER (WHERE status = 6 AND sold < seats) AS payment_due_not_sold
  FROM checked
) AS c
CROSS JOIN LATERAL (VALUES
  (1, 'orders', c.orders),
  (2, 'of them multi-seat', c.multi_seat),
  (3, 'partly sold (must be 0)', c.partly_sold),
  (4, 'seats sold, order not confirmed (must be 0)', c.sold_not_confirmed),
  (5, 'seats sold, no money taken or held, not payment_due (must be 0)', c.sold_no_money),
  (6, 'money taken, seats not all sold (must be 0)', c.paid_not_sold),
  (7, 'confirmed, money not taken (must be 0)', c.confirmed_not_captured),
  (8, 'ended, authorisation still held (a void that timed out; 031)', c.ended_still_authorized),
  (9, 'payment_due: sold, capture refused, still owed (034)', c.payment_due),
  (10, 'payment_due, seats not all sold (must be 0)', c.payment_due_not_sold)
) AS v(n, label, value)
ORDER BY v.n;
SQL
)

  report ''
  report '```'
  report 'order invariants, read from Postgres'
  report "$(psql_q "$q")"
  report '```'
}

# Sleeps until `offset` seconds after `t0`, and not at all if that is already past.
sleep_until() {
  local offset="$1"
  local t0="$2"
  local target=$((t0 + offset))
  local remaining=$((target - SECONDS))

  if [ "$remaining" -gt 0 ]; then
    sleep "$remaining"
  fi
}

# ---------------------------------------------------------------------------
# Session
# ---------------------------------------------------------------------------

RUNS=("$@")
if [ ${#RUNS[@]} -eq 0 ]; then
  RUNS=(baseline redis stall payments reconcilers orders)
fi

report "# Encore chaos session — ${STAMP}"
report ''
report 'Produced by `load/chaos.sh`. Every number below was read out of the running system:'
report "the k6 digests are each run's own output, and everything in a \`psql\` block was"
report 'queried from Postgres after the fault. DECISIONS 019 is the write-up.'
report ''
report "Runs in this session: ${RUNS[*]}"

bring_up 'clean'

for run in "${RUNS[@]}"; do
  case "$run" in
    baseline) run_baseline ;;
    redis) run_redis ;;
    payments) run_payments ;;
    stall) run_stall ;;
    reconcilers) run_reconcilers ;;
    orders) run_orders ;;
    *) echo "chaos: unknown run '${run}'" >&2 ;;
  esac
done

say "report written to ${REPORT}"

# The "(must be 0)" rows are invariants, not statistics, so a breach fails the session and
# anything running this can tell (020). A row with no number is a failure too: the query
# did not answer, and silence is not a pass.
breaches=$(grep -F '(must be 0)' "$REPORT" | grep -Ev '\(must be 0\)[^0-9]*[^0-9]0[[:space:]]*$')

if [ -n "$breaches" ]; then
  say "invariant breached"
  printf '%s\n' "$breaches"
  exit 1
fi
