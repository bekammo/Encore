// Encore's flash-sale load harness, and the traffic half of the chaos rig (019).
//
// ConcurrentHoldTests proves one winner per seat; it is the correctness proof and stays
// the important one. It is not a measurement: it says nothing about the hold path's p99,
// where the Redis lock stops helping, or whether xmin rejections become a retry storm.
// This file measures those and asserts the invariant while it does: if the API ever
// sells more seats than exist, the seats_sold threshold fails the run.
//
// Two baseline scenarios, run one after the other so each number is attributable:
//
//   contention — many clients, a handful of seats, hold then immediately release.
//                The pool never drains, so pressure stays full. The hot-path number.
//   flash_sale — many clients, a real seat map, hold then purchase. Inventory drains
//                as it would on sale day, and refusals shift from already_held to
//                already_sold.
//
// No sleep between iterations: a flash sale is everyone pressing the button at once.
//
// == The chaos scenarios ==
//
// CHAOS_PHASES selects them. Empty, the default, registers only the two scenarios
// above, so a baseline run stays comparable with earlier ones.
//
//   redis       two identical windows, Redis stopped for the second. One window with
//               an outage in the middle would measure a mixture.
//   payments    two windows of real checkouts, payments-api stopped for the second.
//               Under test is a behaviour, not a latency.
//   stall       a paced rate of sales while the outbox's consumer is blocked. Delivery
//               latency is read from notifications afterwards; this side cannot see it.
//   reconciler  checkouts against a gateway that loses answers, while two reconcilers
//               sweep one table.
//   orders      multi-seat checkouts, confirmed, cancelled, or both at once. Nothing
//               is injected; the fault is the customer. Puts 011's all-or-none sale
//               and 012's cancel under load rather than in a test with hooks.
//
// This script injects nothing; k6 has no access to the Docker daemon. load/chaos.sh
// owns the timeline, and gaps between windows keep clock skew from landing a fault in
// the wrong one.
import { sleep } from 'k6';
import http from 'k6/http';
import { Counter, Rate, Trend } from 'k6/metrics';

const BASE_URL = __ENV.ENCORE_BASE_URL || 'http://api:8080';

// Venues, events and seat maps are the operator's writes (030). Compose passes the same
// default to the API, so the two agree unless OPERATOR_API_KEY overrides both.
const OPERATOR_KEY = __ENV.OPERATOR_KEY || 'local-operator-key';

// Where handleSummary writes the summary. Empty runs without a mounted volume and
// prints the digest on stdout only.
const RESULTS_DIR = __ENV.ENCORE_RESULTS_DIR === undefined ? '/results' : __ENV.ENCORE_RESULTS_DIR;

// Names this run in the summary filename and the digest; a chaos session puts several
// runs in one directory.
const RUN_LABEL = __ENV.RUN_LABEL || 'baseline';

const CONTENDED_SEATS = Number(__ENV.CONTENDED_SEATS || 5);
const CONTENTION_VUS = Number(__ENV.CONTENTION_VUS || 50);
const CONTENTION_SECONDS = Number(__ENV.CONTENTION_SECONDS || 60);

const SALE_SEATS = Number(__ENV.SALE_SEATS || 500);
const SALE_VUS = Number(__ENV.SALE_VUS || 100);
const SALE_SECONDS = Number(__ENV.SALE_SECONDS || 60);

// Long enough for one scenario's in-flight requests to land before the next competes
// for the same connection pool.
const GAP_SECONDS = 5;

// Which faults this invocation is shaped for. One per invocation, so a fault's
// aftermath does not leak into the next fault's measurement.
const PHASES = (__ENV.CHAOS_PHASES || '')
  .split(',')
  .map(function (name) { return name.trim(); })
  .filter(function (name) { return name.length > 0; });

function running(phase) {
  return PHASES.indexOf(phase) !== -1;
}

const BASELINE = PHASES.length === 0;

// -- Redis outage ------------------------------------------------------------
// Two windows, one variable: same scenarios, same VUs, same pool sizes, so the p99
// delta is attributable to the lock.
const REDIS_VUS = Number(__ENV.REDIS_VUS || 200);
const REDIS_SALE_VUS = Number(__ENV.REDIS_SALE_VUS || 50);
const REDIS_SECONDS = Number(__ENV.REDIS_SECONDS || 30);
// Split in half, one half per window, so neither inherits the other's drained pool.
const REDIS_SEATS = Number(__ENV.REDIS_SEATS || 1000);
const REDIS_HALF = Math.floor(REDIS_SEATS / 2);

// -- Payments outage ---------------------------------------------------------
const PAYMENTS_VUS = Number(__ENV.PAYMENTS_VUS || 8);
const PAYMENTS_SECONDS = Number(__ENV.PAYMENTS_SECONDS || 20);
const CHECKOUT_SEATS = Number(__ENV.CHECKOUT_SEATS || 3000);

// -- Dispatcher stall --------------------------------------------------------
// A paced arrival rate, the one place in this file where that is right: a backlog
// needs a sustained, known event rate, and a mob drains any seat map in seconds and
// then produces nothing, which would measure a stall against silence.
const STALL_RATE = Number(__ENV.STALL_RATE || 100);
const STALL_SECONDS = Number(__ENV.STALL_SECONDS || 60);
const STALL_SEATS = Number(__ENV.STALL_SEATS || 6500);

// -- Double reconciler -------------------------------------------------------
const RECONCILER_VUS = Number(__ENV.RECONCILER_VUS || 8);
const RECONCILER_SECONDS = Number(__ENV.RECONCILER_SECONDS || 45);

// -- Multi-seat orders and the confirm/cancel race ---------------------------
// Each iteration opens an order for one to four seats and does one of three things
// with it. RACE_SHARE of them send a confirm and, while it is in flight, a cancel for
// the same order: the same customer pressing both buttons.
//
// The cancel leaves after a random delay across the confirm's duration. Sent together,
// a ~11 ms cancel settles every race before a ~320 ms confirm reaches the sale, and the
// interleaving that matters (012) is a cancel between the sale and the capture.
const ORDERS_VUS = Number(__ENV.ORDERS_VUS || 10);
const ORDERS_SECONDS = Number(__ENV.ORDERS_SECONDS || 30);
const ORDERS_SEATS = Number(__ENV.ORDERS_SEATS || 6000);
const RACE_SHARE = Number(__ENV.RACE_SHARE || 0.4);
const CANCEL_SHARE = Number(__ENV.CANCEL_SHARE || 0.2);
const RACE_DELAY_MAX_MS = Number(__ENV.RACE_DELAY_MAX_MS || 700);

// Every answer a confirm or cancel is documented to give. Declared up front because k6
// reports a tagged submetric only when a threshold names it.
const CONFIRM_ANSWERS = ['confirmed', 'awaiting_capture', 'order_failed', 'holds_expired', 'lost_race',
  'order_not_pending', 'payment_declined', 'payment_timed_out'];
const CANCEL_ANSWERS = ['cancelled', 'lost_race', 'order_not_pending'];

// When set, setup() creates a venue with this name as its very last act. chaos.sh
// polls for it and starts its clock, so the injection timeline is pinned to the end of
// setup rather than to the start of a container.
const CHAOS_MARKER = __ENV.CHAOS_MARKER || '';

const holdLatency = new Trend('hold_latency', true);
const purchaseLatency = new Trend('purchase_latency', true);
const confirmLatency = new Trend('confirm_latency', true);
const holdsWon = new Counter('holds_won');
const holdsRefused = new Counter('holds_refused');
const holdWinRate = new Rate('hold_win_rate');
const seatsSold = new Counter('seats_sold');
const saleRefused = new Counter('sale_refused');
const releaseRefused = new Counter('release_refused');
const unexpected = new Counter('unexpected_responses');

// The checkout path, which the baseline scenarios never touch: the only one that
// reaches Orders and so Payments.
const ordersCreated = new Counter('orders_created');
const ordersConfirmed = new Counter('orders_confirmed');
const ordersAwaitingCapture = new Counter('orders_awaiting_capture');
const checkoutRefused = new Counter('checkout_refused');
const confirmRefused = new Counter('confirm_refused');
const confirmTimedOut = new Counter('confirm_timed_out');

// The orders phase. Answers are tagged rather than split across counters.
const orderSeats = new Counter('order_seats');
const confirmAnswered = new Counter('confirm_answered');
const cancelAnswered = new Counter('cancel_answered');
const raceAnswered = new Counter('race_answered');
const cancelLatency = new Trend('cancel_latency', true);

// How long after the sale opened each seat went. The sale's interesting number is how
// long inventory lasted, not latency: min is the first sale, max the sell-out.
const soldAfter = new Trend('sold_after_ms', true);

// Every seat pool a `buy`-shaped scenario draws from. The oversell threshold counts
// over all of them, so it is exactly SALE_SEATS on a baseline run and a chaos phase's
// sales cannot fail it spuriously.
function sellableSeats() {
  var total = SALE_SEATS;

  if (running('redis')) {
    total += REDIS_HALF * 2;
  }

  if (running('stall')) {
    total += STALL_SEATS;
  }

  return total;
}

function scenarios() {
  var all = {};

  // The baseline pair runs on every invocation. On a fault run it is the warm-up and
  // the same-day control.
  all.contention = {
    executor: 'constant-vus',
    vus: CONTENTION_VUS,
    duration: CONTENTION_SECONDS + 's',
    exec: 'contend',
    tags: { phase: 'contention' },
    gracefulStop: '10s',
  };

  all.flash_sale = {
    executor: 'constant-vus',
    vus: SALE_VUS,
    duration: SALE_SECONDS + 's',
    startTime: (CONTENTION_SECONDS + GAP_SECONDS) + 's',
    exec: 'buy',
    tags: { phase: 'sale' },
    gracefulStop: '10s',
  };

  // Everything below starts after the baseline pair has finished and settled.
  var after = CONTENTION_SECONDS + GAP_SECONDS + SALE_SECONDS + GAP_SECONDS;

  if (running('redis')) {
    all.redis_up_contend = {
      executor: 'constant-vus',
      vus: REDIS_VUS,
      duration: REDIS_SECONDS + 's',
      startTime: after + 's',
      exec: 'contend',
      tags: { phase: 'redis_up' },
      gracefulStop: '10s',
    };

    all.redis_up_sale = {
      executor: 'constant-vus',
      vus: REDIS_SALE_VUS,
      duration: REDIS_SECONDS + 's',
      startTime: after + 's',
      exec: 'buyRedisUp',
      tags: { phase: 'redis_up' },
      gracefulStop: '10s',
    };

    // The gap is where chaos.sh stops Redis, so clock skew cannot move the fault
    // into a window.
    var down = after + REDIS_SECONDS + GAP_SECONDS;

    all.redis_down_contend = {
      executor: 'constant-vus',
      vus: REDIS_VUS,
      duration: REDIS_SECONDS + 's',
      startTime: down + 's',
      exec: 'contend',
      tags: { phase: 'redis_down' },
      gracefulStop: '10s',
    };

    all.redis_down_sale = {
      executor: 'constant-vus',
      vus: REDIS_SALE_VUS,
      duration: REDIS_SECONDS + 's',
      startTime: down + 's',
      exec: 'buyRedisDown',
      tags: { phase: 'redis_down' },
      gracefulStop: '10s',
    };
  }

  if (running('payments')) {
    all.payments_up_checkout = {
      executor: 'constant-vus',
      vus: PAYMENTS_VUS,
      duration: PAYMENTS_SECONDS + 's',
      startTime: after + 's',
      exec: 'checkoutUp',
      tags: { phase: 'payments_up' },
      gracefulStop: '15s',
    };

    all.payments_down_checkout = {
      executor: 'constant-vus',
      vus: PAYMENTS_VUS,
      duration: PAYMENTS_SECONDS + 's',
      startTime: (after + PAYMENTS_SECONDS + GAP_SECONDS) + 's',
      exec: 'checkoutDown',
      tags: { phase: 'payments_down' },
      gracefulStop: '15s',
    };
  }

  if (running('stall')) {
    all.stall_sale = {
      executor: 'constant-arrival-rate',
      rate: STALL_RATE,
      timeUnit: '1s',
      duration: STALL_SECONDS + 's',
      startTime: after + 's',
      preAllocatedVUs: 60,
      maxVUs: 200,
      exec: 'buyStall',
      tags: { phase: 'stall' },
      gracefulStop: '10s',
    };
  }

  if (running('orders')) {
    all.orders_mixed = {
      executor: 'constant-vus',
      vus: ORDERS_VUS,
      duration: ORDERS_SECONDS + 's',
      startTime: after + 's',
      exec: 'orders',
      tags: { phase: 'orders' },
      gracefulStop: '15s',
    };
  }

  if (running('reconciler')) {
    all.reconciler_checkout = {
      executor: 'constant-vus',
      vus: RECONCILER_VUS,
      duration: RECONCILER_SECONDS + 's',
      startTime: after + 's',
      exec: 'checkoutReconciler',
      tags: { phase: 'reconciler' },
      gracefulStop: '15s',
    };
  }

  return all;
}

function thresholds() {
  var t = {
    // The invariant. Each 200 from a purchase is one seat leaving inventory, and each
    // iteration buys at most once with a client id nobody else uses, so this counts
    // distinct seats sold. More than exist is an oversell, and it fails the run.
    seats_sold: ['count<=' + sellableSeats()],

    // The same invariant for the flash sale's own pool. On a chaos run the overall bound
    // includes the fault phases' pools, whose unsold seats would otherwise hide an oversell here.
    'seats_sold{phase:sale}': ['count<=' + SALE_SEATS],

    // Anything the routes are not documented to return — a 500, a timeout, a 415 — is
    // a fault rather than a refusal.
    unexpected_responses: ['count==0'],

    // The rest are submetric declarations, not assertions: k6 only reports a tagged
    // submetric a threshold names, and >=0 is always true. They let the digest break
    // refusals down by reason and latency down by phase.
    'hold_latency{phase:contention}': ['p(99)>=0'],
    'hold_latency{phase:sale}': ['p(99)>=0'],
    'holds_refused{reason:already_held}': ['count>=0'],
    'holds_refused{reason:already_sold}': ['count>=0'],
    'holds_refused{reason:lost_race}': ['count>=0'],
    'holds_refused{reason:hold_cap_reached}': ['count>=0'],
    'holds_refused{reason:concurrent_request_in_flight}': ['count>=0'],
  };

  // Per-phase assertions, registered only for the phases this invocation runs: a
  // threshold over a metric with no samples reports a pass nobody earned or a failure
  // nobody caused.
  if (running('redis')) {
    t['hold_latency{phase:redis_up}'] = ['p(99)>=0'];
    t['hold_latency{phase:redis_down}'] = ['p(99)>=0'];
    t['purchase_latency{phase:redis_up}'] = ['p(99)>=0'];
    t['purchase_latency{phase:redis_down}'] = ['p(99)>=0'];
    t['holds_won{phase:redis_up}'] = ['count>=0'];

    // The fault must not turn a refusal into a fault.
    t['unexpected_responses{phase:redis_down}'] = ['count==0'];

    // A declaration, and the one that gives the assertion above a control. Without it
    // there is no telling whether 5xx came from the outage or from an overloaded
    // Postgres in the healthy window (019).
    t['unexpected_responses{phase:redis_up}'] = ['count>=0'];

    // Oversell, asserted separately per window because each has its own pool.
    t['seats_sold{phase:redis_up}'] = ['count<=' + REDIS_HALF];
    t['seats_sold{phase:redis_down}'] = ['count<=' + REDIS_HALF];

    // The hold path still works without the lock. A run where every hold failed would
    // pass everything above and demonstrate nothing.
    t['holds_won{phase:redis_down}'] = ['count>0'];
  }

  if (running('payments')) {
    t['confirm_latency{phase:payments_up}'] = ['p(99)>=0'];
    t['confirm_latency{phase:payments_down}'] = ['p(99)>=0'];
    t['unexpected_responses{phase:payments_down}'] = ['count==0'];
    t['confirm_refused{reason:payment_timed_out}'] = ['count>=0'];
    t['confirm_refused{reason:lost_race}'] = ['count>=0'];
    t['checkout_refused{reason:seats_unavailable}'] = ['count>=0'];

    // Declarations, not assertions: without them the control window reports zero and
    // the comparison is with nothing.
    t['orders_confirmed{phase:payments_up}'] = ['count>=0'];
    t['confirm_timed_out{phase:payments_up}'] = ['count>=0'];

    // A service that is down must present as a timeout and nothing else (018),
    // asserted under load rather than against a stubbed handler.
    t['confirm_timed_out{phase:payments_down}'] = ['count>0'];
    t['orders_confirmed{phase:payments_down}'] = ['count==0'];
  }

  if (running('stall')) {
    t['unexpected_responses{phase:stall}'] = ['count==0'];
    t['seats_sold{phase:stall}'] = ['count<=' + STALL_SEATS];
    t['purchase_latency{phase:stall}'] = ['p(99)>=0'];
    t['hold_latency{phase:stall}'] = ['p(99)>=0'];
  }

  if (running('orders')) {
    // The assertion k6 can make. The ones that matter — no order partly sold, none
    // sold and unpaid, none paid and unsold — are about rows, and chaos.sh reads them.
    t['unexpected_responses{phase:orders}'] = ['count==0'];
    t['orders_created{phase:orders}'] = ['count>0'];
    t['confirm_latency{phase:orders}'] = ['p(99)>=0'];
    t['cancel_latency{phase:orders}'] = ['p(99)>=0'];

    for (const answer of CONFIRM_ANSWERS) {
      t['confirm_answered{answer:' + answer + '}'] = ['count>=0'];

      for (const cancelAnswer of CANCEL_ANSWERS) {
        t['race_answered{pair:' + answer + '__' + cancelAnswer + '}'] = ['count>=0'];
      }
    }

    for (const answer of CANCEL_ANSWERS) {
      t['cancel_answered{answer:' + answer + '}'] = ['count>=0'];
    }
  }

  if (running('reconciler')) {
    t['unexpected_responses{phase:reconciler}'] = ['count==0'];
    t['confirm_latency{phase:reconciler}'] = ['p(99)>=0'];
    t['confirm_timed_out{phase:reconciler}'] = ['count>=0'];
    t['orders_confirmed{phase:reconciler}'] = ['count>=0'];
  }

  return t;
}

export const options = {
  summaryTrendStats: ['min', 'avg', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
  scenarios: scenarios(),
  thresholds: thresholds(),
};

// A client id is a GUID the API parses and nothing more, so Math.random rather than a
// jslib import: the harness reaches the network for the API under test only.
function uuid() {
  let s = '';
  for (let i = 0; i < 36; i++) {
    if (i === 8 || i === 13 || i === 18 || i === 23) { s += '-'; continue; }
    if (i === 14) { s += '4'; continue; }
    const r = Math.floor(Math.random() * 16);
    s += (i === 19 ? ((r & 0x3) | 0x8) : r).toString(16);
  }
  return s;
}

function pick(seats) {
  return seats[Math.floor(Math.random() * seats.length)];
}

function reasonOf(res) {
  try {
    const body = res.json();
    return (body && body.reason) || 'unknown';
  } catch (e) {
    return 'unparsed';
  }
}

// Setup's writes only: every caller is an operator route.
function postJson(path, body) {
  return http.post(BASE_URL + path, JSON.stringify(body), {
    headers: { 'Content-Type': 'application/json', 'X-Operator-Key': OPERATOR_KEY },
  });
}

function seatAction(eventId, seatId, clientId, action) {
  return http.post(
    BASE_URL + '/events/' + eventId + '/seats/' + seatId + '/' + action,
    null,
    { headers: { 'X-Client-Id': clientId }, tags: { name: action } });
}

// Builds the world the run measures: one venue, one event on sale now, one seat map.
// Pools are separate: the contention pool stays small to keep pressure on one row, the
// sale pool must survive its window, and each chaos phase gets its own so one fault's
// drained inventory is not the next one's starting condition.
export function setup() {
  // Give the counter a sample, so its threshold is evaluated even when nothing fails.
  unexpected.add(0);

  const pools = [
    { name: 'contended', count: CONTENDED_SEATS },
    { name: 'sale', count: SALE_SEATS },
    { name: 'redisUp', count: running('redis') ? REDIS_HALF : 0 },
    { name: 'redisDown', count: running('redis') ? REDIS_HALF : 0 },
    { name: 'checkout', count: running('payments') || running('reconciler') ? CHECKOUT_SEATS : 0 },
    { name: 'stall', count: running('stall') ? STALL_SEATS : 0 },
    { name: 'orders', count: running('orders') ? ORDERS_SEATS : 0 },
  ];

  let total = 0;
  for (const pool of pools) {
    total += pool.count;
  }

  const venue = postJson('/catalog/venues', {
    name: 'Encore load harness arena',
    address: 'One Compose Lane',
    capacity: total,
  });
  if (venue.status !== 201) {
    throw new Error('setup: creating the venue returned ' + venue.status + ' ' + venue.body);
  }

  const startsAt = new Date(Date.now() + 24 * 60 * 60 * 1000).toISOString();
  const show = postJson('/catalog/events', {
    venueId: venue.json('id'),
    name: 'Encore load harness show',
    startsAt: startsAt,
    onSaleAt: null,
    price: 49.5,
    currency: 'GBP',
  });
  if (show.status !== 201) {
    throw new Error('setup: creating the event returned ' + show.status + ' ' + show.body);
  }

  const eventId = show.json('id');
  const seats = postJson('/events/' + eventId + '/seats', { count: total });
  if (seats.status !== 201) {
    throw new Error('setup: creating the seat map returned ' + seats.status + ' ' + seats.body);
  }

  const seatIds = seats.json('seatIds');
  const data = { eventId: eventId };

  let offset = 0;
  for (const pool of pools) {
    data[pool.name] = seatIds.slice(offset, offset + pool.count);
    offset += pool.count;
  }

  // One unmeasured hold and release first. The first request pays for JIT, EF Core
  // model building and an empty pool — seconds, enough to be a whole run's maximum.
  // Through seatAction rather than hold(), so it lands in no metric.
  const warmupClient = uuid();
  seatAction(eventId, data.sale[0], warmupClient, 'hold');
  seatAction(eventId, data.sale[0], warmupClient, 'release');

  // The starting gun, and the last thing setup does. See CHAOS_MARKER above.
  if (CHAOS_MARKER) {
    const marker = postJson('/catalog/venues', {
      name: CHAOS_MARKER,
      address: 'One Compose Lane',
      capacity: 1,
    });
    if (marker.status !== 201) {
      throw new Error('setup: creating the chaos marker returned ' + marker.status + ' ' + marker.body);
    }
  }

  // Captured last, as close as possible to the scenarios starting; the sale offsets
  // itself against this.
  data.startedAt = Date.now();

  return data;
}

function hold(eventId, seatId, clientId) {
  const res = seatAction(eventId, seatId, clientId, 'hold');
  holdLatency.add(res.timings.duration);

  if (res.status === 200) {
    holdsWon.add(1);
    holdWinRate.add(true);
  } else if (res.status === 409) {
    holdsRefused.add(1, { reason: reasonOf(res) });
    holdWinRate.add(false);
  } else {
    unexpected.add(1, { route: 'hold', status: String(res.status) });
    holdWinRate.add(false);
  }

  return res;
}

// Hold, then hand it straight back. The release keeps the pool from draining, so every
// iteration meets a seat somebody else is fighting for.
export function contend(data) {
  const clientId = uuid();
  const seatId = pick(data.contended);

  if (hold(data.eventId, seatId, clientId).status !== 200) {
    return;
  }

  const res = seatAction(data.eventId, seatId, clientId, 'release');
  if (res.status === 409) {
    // A refusal, not a fault: lost_race is optimistic concurrency doing its job.
    releaseRefused.add(1, { reason: reasonOf(res) });
  } else if (res.status !== 200) {
    unexpected.add(1, { route: 'release', status: String(res.status) });
  }
}

// Hold, then buy. Nothing is retried: a refusal is the answer, and a retry would
// measure the harness's patience.
function purchase(data, seats, timed) {
  const clientId = uuid();
  const seatId = pick(seats);

  if (hold(data.eventId, seatId, clientId).status !== 200) {
    return;
  }

  const res = seatAction(data.eventId, seatId, clientId, 'purchase');
  purchaseLatency.add(res.timings.duration);

  if (res.status === 200) {
    seatsSold.add(1);
    if (timed) {
      // Subtract the scenario's startTime from time since setup ended, leaving time
      // since the sale opened.
      soldAfter.add(Date.now() - data.startedAt - (CONTENTION_SECONDS + GAP_SECONDS) * 1000);
    }
  } else if (res.status === 409) {
    saleRefused.add(1, { reason: reasonOf(res) });
  } else {
    unexpected.add(1, { route: 'purchase', status: String(res.status) });
  }
}

export function buy(data) {
  purchase(data, data.sale, true);
}

export function buyRedisUp(data) {
  purchase(data, data.redisUp, false);
}

export function buyRedisDown(data) {
  purchase(data, data.redisDown, false);
}

export function buyStall(data) {
  purchase(data, data.stall, false);
}

// The checkout path: create an order over held seats, then confirm it, the only route
// that reaches Payments. One seat per order, so no order ends partly.
//
// Nothing is retried: a confirm that timed out is exactly the state reconciliation is
// about (014), and a harness retry would resolve the ambiguity this run observes.
function checkout(data) {
  const clientId = uuid();
  const seatId = pick(data.checkout);

  const created = http.post(
    BASE_URL + '/orders',
    JSON.stringify({ eventId: data.eventId, seatIds: [seatId] }),
    {
      headers: { 'Content-Type': 'application/json', 'X-Client-Id': clientId },
      tags: { name: 'checkout' },
    });

  if (created.status === 201) {
    ordersCreated.add(1);
  } else if (created.status === 409 || created.status === 400) {
    // A seat somebody else holds, or a drained pool. Both are refusals.
    checkoutRefused.add(1, { reason: reasonOf(created) });
    return;
  } else {
    unexpected.add(1, { route: 'checkout', status: String(created.status) });
    return;
  }

  const orderId = created.json('id');

  const res = http.post(
    BASE_URL + '/orders/' + orderId + '/confirm',
    null,
    { headers: { 'X-Client-Id': clientId }, tags: { name: 'confirm' } });

  confirmLatency.add(res.timings.duration);

  if (res.status === 200) {
    const status = res.json('status');

    if (status === 'awaiting_capture') {
      // Authorised and sold, capture unanswered (010). Resolved by the next confirm,
      // so neither a success nor a fault.
      ordersAwaitingCapture.add(1);
    } else {
      ordersConfirmed.add(1);
    }
  } else if (res.status === 409) {
    const reason = reasonOf(res);
    confirmRefused.add(1, { reason: reason });

    if (reason === 'payment_timed_out') {
      confirmTimedOut.add(1);
    }
  } else {
    unexpected.add(1, { route: 'confirm', status: String(res.status) });
  }
}

export function checkoutUp(data) {
  checkout(data);
}

export function checkoutDown(data) {
  checkout(data);
}

export function checkoutReconciler(data) {
  checkout(data);
}

// -- The orders phase ---------------------------------------------------------

function pickDistinct(seats, count) {
  const chosen = [];

  while (chosen.length < count) {
    const seatId = pick(seats);
    if (chosen.indexOf(seatId) === -1) {
      chosen.push(seatId);
    }
  }

  return chosen;
}

function orderUrl(orderId, action) {
  return BASE_URL + '/orders/' + orderId + '/' + action;
}

function orderParams(clientId, action) {
  return { headers: { 'X-Client-Id': clientId }, tags: { name: action } };
}

// What a confirm said, as one word: the order's status on a 200, the reason on a 409,
// null for anything undocumented.
function confirmAnswer(res) {
  if (res.status === 200) {
    return res.json('status');
  }

  return res.status === 409 ? reasonOf(res) : null;
}

function cancelAnswer(res) {
  if (res.status === 200) {
    return 'cancelled';
  }

  return res.status === 409 ? reasonOf(res) : null;
}

function recordConfirm(res) {
  confirmLatency.add(res.timings.duration);
  const answer = confirmAnswer(res);

  if (answer === null) {
    unexpected.add(1, { route: 'confirm', status: String(res.status) });
  } else {
    confirmAnswered.add(1, { answer: answer });
  }

  return answer;
}

function recordCancel(res) {
  cancelLatency.add(res.timings.duration);
  const answer = cancelAnswer(res);

  if (answer === null) {
    unexpected.add(1, { route: 'cancel', status: String(res.status) });
  } else {
    cancelAnswered.add(1, { answer: answer });
  }

  return answer;
}

// One customer, one order of one to four seats, then one of three endings. Nothing is
// retried, as in checkout above; chaos.sh reads what actually happened from Postgres.
export async function orders(data) {
  const clientId = uuid();
  const seatIds = pickDistinct(data.orders, 1 + Math.floor(Math.random() * 4));

  const created = http.post(
    BASE_URL + '/orders',
    JSON.stringify({ eventId: data.eventId, seatIds: seatIds }),
    {
      headers: { 'Content-Type': 'application/json', 'X-Client-Id': clientId },
      tags: { name: 'checkout' },
    });

  // 409 only. The harness always sends one to four distinct seats and a client id, so a
  // 400 (no_seats, duplicate_seat, too_many_seats) could only mean a regression.
  if (created.status === 409) {
    checkoutRefused.add(1, { reason: reasonOf(created) });
    return;
  }

  if (created.status !== 201) {
    unexpected.add(1, { route: 'checkout', status: String(created.status) });
    return;
  }

  ordersCreated.add(1);
  orderSeats.add(seatIds.length);

  const orderId = created.json('id');
  const roll = Math.random();

  if (roll < RACE_SHARE) {
    // The confirm goes out without waiting for it; the cancel follows somewhere
    // inside the confirm's lifetime, then both answers are collected.
    const confirming = http.asyncRequest('POST', orderUrl(orderId, 'confirm'), null,
      orderParams(clientId, 'confirm'));

    sleep(Math.random() * RACE_DELAY_MAX_MS / 1000);

    const cancelled = recordCancel(
      http.post(orderUrl(orderId, 'cancel'), null, orderParams(clientId, 'cancel')));
    const confirmed = recordConfirm(await confirming);

    if (confirmed !== null && cancelled !== null) {
      raceAnswered.add(1, { pair: confirmed + '__' + cancelled });
    }
  } else if (roll < RACE_SHARE + CANCEL_SHARE) {
    recordCancel(http.post(orderUrl(orderId, 'cancel'), null, orderParams(clientId, 'cancel')));
  } else {
    recordConfirm(http.post(orderUrl(orderId, 'confirm'), null, orderParams(clientId, 'confirm')));
  }
}

function stat(metrics, name, key, digits) {
  const metric = metrics[name];
  if (!metric || !metric.values || metric.values[key] === undefined) {
    return 'n/a';
  }
  return metric.values[key].toFixed(digits === undefined ? 1 : digits);
}

function total(metrics, name) {
  const metric = metrics[name];
  return metric && metric.values && metric.values.count !== undefined ? metric.values.count : 0;
}

function line(label, value) {
  return '  ' + (label + '                          ').slice(0, 26) + value + '\n';
}

function latencyLine(metrics, label, name) {
  return line(label,
    'med ' + stat(metrics, name, 'med')
    + '  p95 ' + stat(metrics, name, 'p(95)')
    + '  p99 ' + stat(metrics, name, 'p(99)')
    + '  max ' + stat(metrics, name, 'max'));
}

// Whether a threshold passed, as k6 recorded it, so the digest's invariant matrix
// cannot disagree with the exit code.
function verdict(data, name) {
  const metric = data.metrics[name];

  if (!metric || !metric.thresholds) {
    return 'not asked';
  }

  const expressions = Object.keys(metric.thresholds);
  if (expressions.length === 0) {
    return 'not asked';
  }

  for (const expression of expressions) {
    const threshold = metric.thresholds[expression];
    const failed = threshold.ok === false || threshold.fails > 0;

    if (failed) {
      return 'FAIL   (' + name + ' ' + expression + ')';
    }
  }

  return 'pass';
}

function invariants(data) {
  let out = '';

  out += line('  no oversell, overall', verdict(data, 'seats_sold'));
  out += line('  no oversell, flash sale', verdict(data, 'seats_sold{phase:sale}'));
  out += line('  no unexpected responses', verdict(data, 'unexpected_responses'));

  if (running('redis')) {
    out += '\n  redis outage\n';
    out += line('  no oversell, lock up', verdict(data, 'seats_sold{phase:redis_up}'));
    out += line('  no oversell, lock gone', verdict(data, 'seats_sold{phase:redis_down}'));
    out += line('  no faults, lock gone', verdict(data, 'unexpected_responses{phase:redis_down}'));
    out += line('  holds still won', verdict(data, 'holds_won{phase:redis_down}'));
  }

  if (running('payments')) {
    out += '\n  payments outage\n';
    out += line('  no faults, service gone', verdict(data, 'unexpected_responses{phase:payments_down}'));
    out += line('  outage reads as timeout', verdict(data, 'confirm_timed_out{phase:payments_down}'));
    out += line('  nothing confirmed', verdict(data, 'orders_confirmed{phase:payments_down}'));
  }

  if (running('stall')) {
    out += '\n  dispatcher stall\n';
    out += line('  no oversell', verdict(data, 'seats_sold{phase:stall}'));
    out += line('  no faults while stalled', verdict(data, 'unexpected_responses{phase:stall}'));
  }

  if (running('orders')) {
    out += '\n  orders\n';
    out += line('  no faults', verdict(data, 'unexpected_responses{phase:orders}'));
    out += line('  orders were placed', verdict(data, 'orders_created{phase:orders}'));
  }

  if (running('reconciler')) {
    out += '\n  two reconcilers\n';
    out += line('  no faults', verdict(data, 'unexpected_responses{phase:reconciler}'));
  }

  return out;
}

// Replaces k6's default summary, which would need textSummary from jslib over the
// network. The numbers that matter are few enough to print directly.
export function handleSummary(data) {
  const m = data.metrics;
  const sold = total(m, 'seats_sold');

  let out = '\nEncore flash-sale harness — run "' + RUN_LABEL + '"\n\n';
  out += line('base url', BASE_URL);
  out += line('chaos phases', PHASES.length === 0 ? 'none (baseline)' : PHASES.join(', '));
  out += line('contention', CONTENTION_VUS + ' vus, ' + CONTENDED_SEATS + ' seats, ' + CONTENTION_SECONDS + 's');
  out += line('sale', SALE_VUS + ' vus, ' + SALE_SEATS + ' seats, ' + SALE_SECONDS + 's');
  out += '\n';
  out += latencyLine(m, 'hold ms, contention', 'hold_latency{phase:contention}');
  out += latencyLine(m, 'hold ms, sale', 'hold_latency{phase:sale}');
  out += latencyLine(m, 'purchase ms', 'purchase_latency');
  out += '\n';
  out += line('holds won', String(total(m, 'holds_won')));
  out += line('holds refused', String(total(m, 'holds_refused')));
  out += line('win rate', stat(m, 'hold_win_rate', 'rate', 3));
  out += '\n';
  out += line('already held', String(total(m, 'holds_refused{reason:already_held}')));
  out += line('already sold', String(total(m, 'holds_refused{reason:already_sold}')));
  out += line('lost race', String(total(m, 'holds_refused{reason:lost_race}')));
  out += line('hold cap reached', String(total(m, 'holds_refused{reason:hold_cap_reached}')));
  out += line('lock contended', String(total(m, 'holds_refused{reason:concurrent_request_in_flight}')));
  out += '\n';
  out += line('seats sold', sold + ' of ' + sellableSeats() + ' sellable');
  if (BASELINE) {
    out += line('sold out after',
      'first ' + stat(m, 'sold_after_ms', 'min') + ' ms, last ' + stat(m, 'sold_after_ms', 'max') + ' ms');
  }
  out += line('sale refused', String(total(m, 'sale_refused')));
  out += line('release refused', String(total(m, 'release_refused')));
  out += line('unexpected', String(total(m, 'unexpected_responses')));
  out += line('oversold', sold > sellableSeats() ? 'YES — the run failed' : 'no');

  if (running('redis')) {
    out += '\nRedis outage — same shape either side, one variable\n\n';
    out += latencyLine(m, 'hold ms, lock up', 'hold_latency{phase:redis_up}');
    out += latencyLine(m, 'hold ms, lock gone', 'hold_latency{phase:redis_down}');
    out += latencyLine(m, 'buy ms, lock up', 'purchase_latency{phase:redis_up}');
    out += latencyLine(m, 'buy ms, lock gone', 'purchase_latency{phase:redis_down}');
    out += line('holds won, lock up', String(total(m, 'holds_won{phase:redis_up}')));
    out += line('holds won, lock gone', String(total(m, 'holds_won{phase:redis_down}')));
    out += line('sold, lock up', total(m, 'seats_sold{phase:redis_up}') + ' of ' + REDIS_HALF);
    out += line('sold, lock gone', total(m, 'seats_sold{phase:redis_down}') + ' of ' + REDIS_HALF);
    out += line('faults, lock gone', String(total(m, 'unexpected_responses{phase:redis_down}')));
  }

  if (running('payments')) {
    out += '\nPayments outage\n\n';
    out += latencyLine(m, 'confirm ms, up', 'confirm_latency{phase:payments_up}');
    out += latencyLine(m, 'confirm ms, down', 'confirm_latency{phase:payments_down}');
    out += line('orders created', String(total(m, 'orders_created')));
    out += line('confirmed, up', String(total(m, 'orders_confirmed{phase:payments_up}')));
    out += line('confirmed, down', String(total(m, 'orders_confirmed{phase:payments_down}')));
    out += line('awaiting capture', String(total(m, 'orders_awaiting_capture')));
    out += line('timed out, up', String(total(m, 'confirm_timed_out{phase:payments_up}')));
    out += line('timed out, down', String(total(m, 'confirm_timed_out{phase:payments_down}')));
    out += line('checkout refused', String(total(m, 'checkout_refused')));
    out += line('faults, down', String(total(m, 'unexpected_responses{phase:payments_down}')));
  }

  if (running('stall')) {
    out += '\nDispatcher stall — request path only; delivery is read from Postgres\n\n';
    out += line('arrival rate', STALL_RATE + '/s for ' + STALL_SECONDS + 's');
    out += latencyLine(m, 'hold ms', 'hold_latency{phase:stall}');
    out += latencyLine(m, 'purchase ms', 'purchase_latency{phase:stall}');
    out += line('seats sold', total(m, 'seats_sold{phase:stall}') + ' of ' + STALL_SEATS);
    out += line('faults', String(total(m, 'unexpected_responses{phase:stall}')));
  }

  if (running('orders')) {
    const created = total(m, 'orders_created{phase:orders}');
    out += '\nOrders — multi-seat, and confirm racing cancel; the invariants are in Postgres\n\n';
    out += line('orders created', String(created));
    out += line('seats per order', created > 0 ? (total(m, 'order_seats') / created).toFixed(2) : 'n/a');
    out += line('checkout refused', String(total(m, 'checkout_refused')));
    out += latencyLine(m, 'confirm ms', 'confirm_latency{phase:orders}');
    out += latencyLine(m, 'cancel ms', 'cancel_latency{phase:orders}');

    out += '\n  confirm answered\n';
    for (const answer of CONFIRM_ANSWERS) {
      const count = total(m, 'confirm_answered{answer:' + answer + '}');
      if (count > 0) {
        out += line('    ' + answer, String(count));
      }
    }

    out += '\n  cancel answered\n';
    for (const answer of CANCEL_ANSWERS) {
      const count = total(m, 'cancel_answered{answer:' + answer + '}');
      if (count > 0) {
        out += line('    ' + answer, String(count));
      }
    }

    // Both halves of each race, paired. Confirmed + cancelled should never appear:
    // one of the two saves must lose on the order row's xmin.
    out += '\n  raced, confirm + cancel\n';
    for (const answer of CONFIRM_ANSWERS) {
      for (const cancelled of CANCEL_ANSWERS) {
        const count = total(m, 'race_answered{pair:' + answer + '__' + cancelled + '}');
        if (count > 0) {
          // Wider than line() allows: the longest pair is 35 characters.
          out += '      ' + (answer + ' + ' + cancelled + '                                        ').slice(0, 40)
            + count + '\n';
        }
      }
    }

    out += line('faults', String(total(m, 'unexpected_responses{phase:orders}')));
  }

  if (running('reconciler')) {
    out += '\nTwo reconcilers — the settlement evidence is in Postgres\n\n';
    out += latencyLine(m, 'confirm ms', 'confirm_latency{phase:reconciler}');
    out += line('orders created', String(total(m, 'orders_created')));
    out += line('confirmed', String(total(m, 'orders_confirmed{phase:reconciler}')));
    out += line('timed out', String(total(m, 'confirm_timed_out{phase:reconciler}')));
    out += line('faults', String(total(m, 'unexpected_responses{phase:reconciler}')));
  }

  out += '\nInvariants, as k6 recorded them\n\n';
  out += invariants(data);
  out += '\n';

  const summary = { stdout: out };
  if (RESULTS_DIR) {
    const stamp = new Date().toISOString().replace(/[:.]/g, '-');
    summary[RESULTS_DIR + '/summary-' + RUN_LABEL + '-' + stamp + '.json'] = JSON.stringify(data, null, 2);
    summary[RESULTS_DIR + '/digest-' + RUN_LABEL + '-' + stamp + '.txt'] = out;
  }
  return summary;
}
