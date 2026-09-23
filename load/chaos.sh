#!/usr/bin/env bash
#
# Encore's chaos harness. DECISIONS 064.
#
# The k6 script beside this one drives the traffic and asserts the invariants.
# It cannot break anything: k6 has no access to the Docker daemon and should not
# have. This script owns the other half — the timeline, the faults, and reading
# the evidence back out of Postgres afterwards — and writes one report per
# session to load/results/.
#
#   bash load/chaos.sh                 every run below, in order
#   bash load/chaos.sh redis stall     only those
#   bash load/chaos.sh baseline        the third load configuration on its own
#
# Five runs, one fault each, rather than one long run carrying all four. A fault's
# aftermath is half of what it is about — a backlog, a set of unresolved payment
# rows, a drained seat map — and a single run would feed each aftermath into the
# next fault's measurement. Separate runs also mean a k6 threshold failing on one
# fault does not take the other three down with it.
#
# Everything runs against the `strangled` profile, because one of the four faults
# is "stop the Payments service" and that service only exists there. That makes
# the baseline run below the third configuration 056 asked for and 061 left open.
#
# Not set -e. A k6 run that fails a threshold exits non-zero, and that is a
# result this script has to record rather than an error it should die on.
set -uo pipefail

cd "$(dirname "$0")/.." || exit 1

RESULTS_DIR="load/results"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
REPORT="${RESULTS_DIR}/chaos-${STAMP}.md"

COMPOSE="docker compose --profile strangled"

# Every table the runs touch, truncated between them so that each run's evidence
# is about that run. Ordered parents-first with CASCADE doing the rest.
#
# The gateway ledger is here since 073. 066 added the table after this list was
# written, so the first session to run against it let the ledger accumulate across
# all five runs: 950 ledger rows beside 655 payments at the end. Every join went
# payments-to-ledger and keys are per attempt, so no reported number was wrong, but
# a count of the ledger on its own would have been.
TABLES='catalog.venues, catalog.events, inventory.seats, inventory.outbox_messages, notifications.notifications, orders.orders, orders.order_lines, payments.payments, payments.gateway_ledger'

# The baseline pair that opens every k6 invocation. Full length for the baseline
# run, because that one is a measurement meant to sit in 056's table; short for
# the fault runs, where it is a warm-up and a same-day control rather than the
# subject.
CONTROL_CONTENTION_SECONDS=15
CONTROL_SALE_SECONDS=15

# k6's own gap between scenarios. Hardcoded there; mirrored here because this
# script has to know where the gaps are — that is where faults get injected.
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

# One scalar or one small table out of Postgres. -A -t so the output is the
# answer and nothing else, which is what makes it safe to paste into the report.
psql_q() {
  $COMPOSE exec -T postgres psql -U encore -d encore -At -F' | ' -c "$1" 2>/dev/null
}

# k6 prints a progress block every second, and the first session's report was
# 3,500 lines of it around 200 lines of results. Only the digest and the banner
# are worth keeping.
k6_digest() {
  grep -vE '^running \(|^ *(contention|flash_sale|redis_[a-z_]+|payments_[a-z_]+|stall_sale|reconciler_checkout|Run) +(•|✓|↓|\[)| Container |^ *$' "$1"
}

# A container's whole log, captured once into a file the greps then share.
#
# The first session called `docker compose logs` seven times while
# reconciliation was still running, so its counts were snapshots of seven
# different instants and disagreed with each other by a handful. They are also
# not small: EF Core logs every command at Information, so one run of this is
# 1.3 million lines and a single pass takes minutes.
capture_log() {
  $COMPOSE logs "$1" --no-log-prefix > "$2" 2>/dev/null
}

reset_data() {
  psql_q "TRUNCATE ${TABLES} RESTART IDENTITY CASCADE;" > /dev/null
}

# Brings the stack up with whatever is currently exported. Compose recreates a
# container whose environment changed, which is the only way to move a setting
# that is read once at startup — every knob these runs turn is one of those.
#
# Built once per session rather than once per run, for the reason the compose
# file gives about `--build`: a measurement of stale binaries is worse than a
# stale test, because it is compared against later. Once is enough, because the
# source cannot change between the runs of one session.
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

# Waits for the marker venue k6's setup() writes as its last act, and echoes
# nothing. The clock starts when this returns.
#
# Pinning the timeline to the end of setup rather than to the start of the
# container is the difference between injecting into the window that was planned
# and injecting into whichever window seat-map creation happened to leave running.
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
  report 'orders by status (0 pending, 1 confirmed, 2 cancelled, 3 expired, 4 failed, 5 awaiting_capture)'
  report "$(psql_q 'SELECT "Status", count(*) FROM orders.orders GROUP BY 1 ORDER BY 1;')"
  report ''
  report 'payments by status (0 pending, 1 authorized, 2 captured, 3 declined, 4 timed_out, 5 voided, 6 abandoned)'
  report "$(psql_q 'SELECT "Status", count(*) FROM payments.payments GROUP BY 1 ORDER BY 1;')"
  report ''
  report "orders with more than one live payment attempt (must be 0): $(psql_q 'SELECT count(*) FROM (SELECT "OrderId" FROM payments.payments WHERE "Status" IN (0, 1, 4) GROUP BY 1 HAVING count(*) > 1) AS breaches;')"
  report '```'
}

# ---------------------------------------------------------------------------
# The runs
# ---------------------------------------------------------------------------

# The third load configuration, and the only run here with no fault in it. Same
# script, same parameters and same seat pools as the two configurations in 056,
# pointed at the pair of processes instead of the monolith.
run_baseline() {
  say 'run 1/5 — baseline on the extracted configuration'
  reset_data

  $COMPOSE run --rm \
    -e RUN_LABEL=baseline-strangled \
    load-strangled > "$REPORT.tmp" 2>&1

  local k6status=$?

  report '## Baseline — the extracted configuration, no fault'
  report ''
  report 'The third configuration 056 asked for: the same 50 VUs on 5 seats then 100 VUs'
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

# Fault 3 in the brief, run second because it is the cheapest to set up.
#
# Two windows of the identical shape with the lock present in one and absent in
# the other. Redis is stopped in the gap between them, which is why the gap
# exists: an outage injected inside a window measures a mixture of both states
# and calls it one number.
#
# Since 076 a purchase takes no lock and a hold takes one (the client lock), so the
# buy latencies here are a control and the hold latencies are the whole of what
# losing Redis costs. That is also the reading that measures 074's FailFast: about
# 1,000 ms per hold means it did not take, and roughly baseline means it did.
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

  # Two seconds into the gap, so the stop lands between the windows however the
  # two clocks drift.
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
# The reconciliation half needs authorisations that reached the gateway and lost
# their answer, which a stopped service cannot produce — a request that never
# arrives leaves no row to reconcile. PAYMENTS_TIMEOUT_RATE is what produces
# those, and it is why this run needs its own compose environment.
run_payments() {
  say 'run 3/5 — payments-api stopped mid-flash-sale'
  reset_data

  export PAYMENTS_TIMEOUT_RATE=0.35
  export RECONCILER_MIN_AGE=00:00:20
  export RECONCILER_POLL=00:00:05
  # The reconciler reports an unanswered lookup and a lost xmin race at Debug,
  # and both are things this run is trying to count.
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
  report 'second. 061 claims that an unreadable answer becomes `TimedOut`, that the order'
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
  # Named for the run. Fault 2 captures payments-api as well, and when both used
  # one filename the file on disk after a full session was fault 2's (073).
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

# Fault 4. Block the outbox's consumer rather than the dispatcher itself.
#
# There is no switch for "stall the dispatcher", and adding one to production code
# for a chaos run would be the tail wagging the dog. Locking
# notifications.notifications is the surgical version: SeatSold is the only event
# with a registered handler, so its delivery blocks on the consumer's INSERT while
# the seat path — a different schema, a different table — carries on untouched.
# That isolates delivery from the request path, which is exactly the separation
# 056 had to infer from two whole runs.
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

  # The lock lives for as long as the transaction does, so the sleep has to
  # happen inside psql rather than beside it. 20s is deliberately under Npgsql's
  # 30s command timeout: the question here is what a blocked dispatcher does to a
  # backlog, and a longer stall would answer a different question about retries
  # and backoff instead.
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

  # One round trip per sample, not two. The first session issued two `docker
  # compose exec` calls per line and each costs about a second, so its samples
  # were three seconds apart on paper and five in fact — which is why its "lock
  # released" line landed seventeen seconds after the lock actually released.
  # The timestamps in notifications are the numbers to trust; this loop is for
  # watching the shape, and it should at least not lie about when it looked.
  local expires=$((stall_at + stall_seconds))
  local samples=0

  while [ $samples -lt 8 ]; do
    report "t+$((SECONDS - t0))s  $(psql_q 'SELECT '"'"'pending: '"'"' || count(*) FILTER (WHERE "ProcessedAt" IS NULL) || '"'"'  delivered: '"'"' || count(*) FILTER (WHERE "ProcessedAt" IS NOT NULL) FROM inventory.outbox_messages;')"
    sleep 2
    samples=$((samples + 1))
  done

  wait $lockpid

  # When the lock was scheduled to expire, not when this loop got back to
  # noticing. pg_sleep is exact and the sampling loop is not.
  say 'lock released'
  report "the lock was released at t+${expires}s"
  report '```'
  report ''
  report '### Recovery'
  report ''
  report '```'

  # 073 found the bug in this criterion: pending reads zero only once arrivals
  # stop, and a run where 100 sales a second keep landing never reaches that —
  # the loop would call the backlog undrained forever, which is a fact about the
  # sale continuing and not about the stall's aftermath. "Drained" is asked as a
  # question about age instead: is anything undelivered older than 5s, which is
  # MaxBatchDuration's own budget and comfortably above the ~1s a message takes
  # in steady state (PollInterval, then DeliveryTimeout if it is unlucky). A
  # message younger than that is still in ordinary flight, not backlog.
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

# Fault 2. 061's single-owner rule, deliberately broken.
#
# Written in 064 to measure what the missing lease cost. The lease exists since
# 074 (a transaction-scoped advisory lock per sweep), so this run now asks whether
# it holds: two reconcilers, and the expectation is zero xmin races lost.
run_reconcilers() {
  say 'run 5/5 — two reconcilers over one table'
  reset_data

  export PAYMENTS_TIMEOUT_RATE=0.5
  export RECONCILER_MIN_AGE=00:00:20
  export RECONCILER_POLL=00:00:05
  export RECONCILER_EVERYWHERE=true
  # The reconciler says "was resolved by something else while this sweep was
  # asking" at Debug, and that line is the whole question here.
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
  report '061 recorded that the reconciler must run in exactly one process, enforced by "a'
  report 'compose setting and a comment". This run turns the setting on in both processes on'
  report 'purpose. Since 074 each sweep takes a Postgres advisory lock first, so the question'
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

  # Both logs captured once, first, and every count below read from the files.
  # The first session asked the daemon seven separate times while reconciliation
  # was still running, so its seven numbers described seven different moments and
  # did not add up. They are large — EF Core logs every command at Information, so
  # this is over a million lines a side — which is also why one pass matters.
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
  RUNS=(baseline redis stall payments reconcilers)
fi

report "# Encore chaos session — ${STAMP}"
report ''
report 'Produced by `load/chaos.sh`. Every number below was read out of the running system:'
report "the k6 digests are each run's own output, and everything in a \`psql\` block was"
report 'queried from Postgres after the fault. DECISIONS 064 is the write-up.'
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
    *) echo "chaos: unknown run '${run}'" >&2 ;;
  esac
done

say "report written to ${REPORT}"
