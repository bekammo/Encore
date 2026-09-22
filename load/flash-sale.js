// Encore's flash-sale load harness, and since DECISIONS 064 its chaos harness too.
//
// The project's central claim is that seat contention is handled correctly under
// flash-sale load. Until the harness existed that claim was proven by
// ConcurrentHoldTests: fifty tasks in one process, against a Testcontainers
// Postgres, asserting exactly one winner. That is a correctness proof and it stays
// the important one. It is not a measurement — it says nothing about what the hold
// path costs at the ninety-ninth percentile, or where the Redis lock stops helping,
// or whether xmin rejections turn into a retry storm when two hundred clients want
// the same row.
//
// This file answers those questions and asserts the invariant while it does it.
// The threshold on seats_sold is the load-test equivalent of the unit suite's
// oversell assertion: if the API ever sells more seats than exist, k6 exits
// non-zero and the run fails. Everything else here is measurement.
//
// Two baseline scenarios, run one after the other rather than together, because
// they answer different questions and overlapping them would make neither number
// attributable:
//
//   contention — many clients, a handful of seats, hold then immediately release.
//                The pool never drains, so contention stays at full pressure for
//                the whole window. This is the hot-path number.
//   flash_sale — many clients, a real seat map, hold then purchase. Inventory
//                drains the way it would on sale day, and the refusal mix shifts
//                from already_held to already_sold as it does.
//
// No sleep between iterations anywhere in those two. A flash sale is not a Poisson
// arrival process with think time; it is everyone pressing the button at once.
//
// == The chaos scenarios (064) ==
//
// CHAOS_PHASES selects them, and an empty value — the default — registers exactly
// the two scenarios above and nothing else. That is deliberate: a baseline run
// after this change must be comparable with the three configurations in 056, and
// a rig that quietly grew extra scenarios would have invalidated its own history.
//
//   redis       four scenarios in two windows, identical in every respect except
//               that Redis is stopped for the second. The A/B is the point: a
//               single window with an outage in the middle measures a mixture.
//   payments    two windows of real checkouts, with payments-api stopped for the
//               second. What is under test is a behaviour, not a latency.
//   stall       a paced, known rate of sales while the outbox's consumer is
//               blocked. Delivery latency is read out of notifications afterwards
//               rather than measured here, because this side cannot see it.
//   reconciler  checkouts against a gateway that loses answers, run while two
//               reconcilers sweep one table. 061's debt, deliberately incurred.
//
// This script does not inject anything. It cannot: k6 has no access to the Docker
// daemon and should not. load/chaos.sh owns the timeline and does the injecting,
// and the windows below are separated by gaps so that a second or two of skew
// between the two clocks cannot land a fault in the wrong window.
import http from 'k6/http';
import { Counter, Rate, Trend } from 'k6/metrics';

const BASE_URL = __ENV.ENCORE_BASE_URL || 'http://api:8080';

// Where handleSummary writes the machine-readable summary. Set it to an empty
// string to run without a mounted volume and get the digest on stdout only.
const RESULTS_DIR = __ENV.ENCORE_RESULTS_DIR === undefined ? '/results' : __ENV.ENCORE_RESULTS_DIR;

// Names this run in the summary filename and in the digest. Several runs land in
// one directory during a chaos session and "which one was this" is not a question
// a timestamp answers well.
const RUN_LABEL = __ENV.RUN_LABEL || 'baseline';

const CONTENDED_SEATS = Number(__ENV.CONTENDED_SEATS || 5);
const CONTENTION_VUS = Number(__ENV.CONTENTION_VUS || 50);
const CONTENTION_SECONDS = Number(__ENV.CONTENTION_SECONDS || 60);

const SALE_SEATS = Number(__ENV.SALE_SEATS || 500);
const SALE_VUS = Number(__ENV.SALE_VUS || 100);
const SALE_SECONDS = Number(__ENV.SALE_SECONDS || 60);

// Long enough for the first scenario's in-flight requests to land before the
// second one starts competing for the same connection pool.
const GAP_SECONDS = 5;

// Which faults this invocation is shaped for. One fault per invocation is the
// rule: a run that carried all four would take a fault's aftermath into the next
// fault's measurement, and the aftermath is half of what each one is about.
const PHASES = (__ENV.CHAOS_PHASES || '')
  .split(',')
  .map(function (name) { return name.trim(); })
  .filter(function (name) { return name.length > 0; });

function running(phase) {
  return PHASES.indexOf(phase) !== -1;
}

const BASELINE = PHASES.length === 0;

// -- Redis outage ------------------------------------------------------------
// Two windows, one variable. Both carry the same two scenarios at the same VUs
// against pools of the same size, so the only difference between them is whether
// the lock exists — which is what makes the p99 delta attributable to it.
const REDIS_VUS = Number(__ENV.REDIS_VUS || 200);
const REDIS_SALE_VUS = Number(__ENV.REDIS_SALE_VUS || 50);
const REDIS_SECONDS = Number(__ENV.REDIS_SECONDS || 30);
// Split in half, one half per window, so neither window inherits the other's
// drained pool.
const REDIS_SEATS = Number(__ENV.REDIS_SEATS || 1000);
const REDIS_HALF = Math.floor(REDIS_SEATS / 2);

// -- Payments outage ---------------------------------------------------------
const PAYMENTS_VUS = Number(__ENV.PAYMENTS_VUS || 8);
const PAYMENTS_SECONDS = Number(__ENV.PAYMENTS_SECONDS || 20);
const CHECKOUT_SEATS = Number(__ENV.CHECKOUT_SEATS || 3000);

// -- Dispatcher stall --------------------------------------------------------
// A paced arrival rate rather than a mob, and this is the one place in the file
// where that is right. The question is what a backlog does to delivery latency,
// which needs a known and sustained event rate for the whole window; a flash-sale
// mob drains any affordable seat map in seconds and then produces no events at
// all, which would measure a stall against silence.
const STALL_RATE = Number(__ENV.STALL_RATE || 100);
const STALL_SECONDS = Number(__ENV.STALL_SECONDS || 60);
const STALL_SEATS = Number(__ENV.STALL_SEATS || 6500);

// -- Double reconciler -------------------------------------------------------
const RECONCILER_VUS = Number(__ENV.RECONCILER_VUS || 8);
const RECONCILER_SECONDS = Number(__ENV.RECONCILER_SECONDS || 45);

// When set, setup() creates a venue with this name as its very last act. It is
// the starting gun: chaos.sh polls Postgres for the row and starts its clock the
// moment it appears, so the injection timeline is pinned to the end of setup
// rather than to the start of a container. Without it the harness would be
// guessing how long seat-map creation took on the day.
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

// The checkout path, which the baseline scenarios never touch: it is the only one
// that reaches Orders and therefore the only one that reaches Payments.
const ordersCreated = new Counter('orders_created');
const ordersConfirmed = new Counter('orders_confirmed');
const ordersAwaitingCapture = new Counter('orders_awaiting_capture');
const checkoutRefused = new Counter('checkout_refused');
const confirmRefused = new Counter('confirm_refused');
const confirmTimedOut = new Counter('confirm_timed_out');

// How long after the sale opened each seat went. The interesting number from the
// sale scenario is not its latency — it is how long the inventory lasted, because
// at a few thousand attempts a second any finite seat map drains in seconds and
// everything after that is the system refusing politely. min is the first sale and
// max is the sell-out.
const soldAfter = new Trend('sold_after_ms', true);

// Every seat pool a `buy`-shaped scenario can draw from. The oversell threshold is
// a count over all of them, so it stays exactly SALE_SEATS on a baseline run and
// cannot fail spuriously just because a chaos phase also sells seats.
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

  // The baseline pair runs on every invocation, chaos or not. On a fault run they
  // are the warm-up and the control: the same two numbers under the same
  // parameters, measured on the same stack minutes before anything was broken.
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

    // The gap is where chaos.sh stops Redis. Injecting between windows rather
    // than inside one is what makes the experiment insensitive to clock skew.
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
    // The invariant. Every 200 from a purchase is one seat leaving inventory,
    // and each iteration buys at most once with a client id nobody else uses, so
    // this counter is a count of distinct seats sold. More of them than there are
    // seats means the system oversold, which is the one outcome that must fail
    // the run rather than appear in a report.
    seats_sold: ['count<=' + sellableSeats()],

    // Anything outside the statuses these routes are documented to return — a
    // 500, a timeout, a 415 — is a fault rather than a refusal.
    unexpected_responses: ['count==0'],

    // The rest of these are submetric declarations, not assertions: k6 only puts
    // a tagged submetric in the summary if a threshold names it, and >=0 is
    // always true. This is what makes the digest below able to break refusals
    // down by reason and latency down by phase, which is the whole point of
    // running more than one scenario.
    'hold_latency{phase:contention}': ['p(99)>=0'],
    'hold_latency{phase:sale}': ['p(99)>=0'],
    'holds_refused{reason:already_held}': ['count>=0'],
    'holds_refused{reason:already_sold}': ['count>=0'],
    'holds_refused{reason:lost_race}': ['count>=0'],
    'holds_refused{reason:hold_cap_reached}': ['count>=0'],
    'holds_refused{reason:concurrent_request_in_flight}': ['count>=0'],
  };

  // Per-phase assertions. Registered only for the phases this invocation runs,
  // because a threshold over a metric with no samples is either vacuously true —
  // which reports a pass nobody earned — or vacuously false, which reports a
  // failure nobody caused. Both are worse than not asking.
  if (running('redis')) {
    t['hold_latency{phase:redis_up}'] = ['p(99)>=0'];
    t['hold_latency{phase:redis_down}'] = ['p(99)>=0'];
    t['purchase_latency{phase:redis_up}'] = ['p(99)>=0'];
    t['purchase_latency{phase:redis_down}'] = ['p(99)>=0'];
    t['holds_won{phase:redis_up}'] = ['count>=0'];

    // The fault must not turn a refusal into a fault.
    t['unexpected_responses{phase:redis_down}'] = ['count==0'];

    // A declaration, and the one that makes the assertion above mean anything.
    // The first session recorded 111 5xx across the run with only 4 inside the
    // outage window, and without this submetric there was no way to tell whether
    // the other 107 came from the outage or from 250 VUs being more than this
    // machine's Postgres will take. An A/B whose control window is unmeasured is
    // not an A/B. DECISIONS 064.
    t['unexpected_responses{phase:redis_up}'] = ['count>=0'];

    // Oversell, asserted separately per window because each has its own pool.
    t['seats_sold{phase:redis_up}'] = ['count<=' + REDIS_HALF];
    t['seats_sold{phase:redis_down}'] = ['count<=' + REDIS_HALF];

    // The hold path still does work without the lock. A run where every hold
    // failed would satisfy every assertion above and demonstrate nothing, so
    // this is the one threshold here that asserts something happened.
    t['holds_won{phase:redis_down}'] = ['count>0'];
  }

  if (running('payments')) {
    t['confirm_latency{phase:payments_up}'] = ['p(99)>=0'];
    t['confirm_latency{phase:payments_down}'] = ['p(99)>=0'];
    t['unexpected_responses{phase:payments_down}'] = ['count==0'];
    t['confirm_refused{reason:payment_timed_out}'] = ['count>=0'];
    t['confirm_refused{reason:lost_race}'] = ['count>=0'];
    t['checkout_refused{reason:seats_unavailable}'] = ['count>=0'];

    // Declarations, not assertions. The digest prints the healthy window beside
    // the broken one, and k6 only computes a tagged submetric when a threshold
    // names it — so without these two the control window reports zero and the
    // comparison silently becomes a comparison with nothing. The first smoke run
    // of this rig did exactly that.
    t['orders_confirmed{phase:payments_up}'] = ['count>=0'];
    t['confirm_timed_out{phase:payments_up}'] = ['count>=0'];

    // A service that is down must present as a timeout and as nothing else.
    // This is 061's claim — an unreadable answer is a timeout — asserted under
    // load rather than in a unit test with a stubbed handler.
    t['confirm_timed_out{phase:payments_down}'] = ['count>0'];
    t['orders_confirmed{phase:payments_down}'] = ['count==0'];
  }

  if (running('stall')) {
    t['unexpected_responses{phase:stall}'] = ['count==0'];
    t['seats_sold{phase:stall}'] = ['count<=' + STALL_SEATS];
    t['purchase_latency{phase:stall}'] = ['p(99)>=0'];
    t['hold_latency{phase:stall}'] = ['p(99)>=0'];
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

// A client id is a claimed identity the API parses as a GUID and nothing more, so
// this is four lines of Math.random rather than a jslib import. The harness
// reaches the network for the API under test and for nothing else.
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

function postJson(path, body) {
  return http.post(BASE_URL + path, JSON.stringify(body), {
    headers: { 'Content-Type': 'application/json' },
  });
}

function seatAction(eventId, seatId, clientId, action) {
  return http.post(
    BASE_URL + '/events/' + eventId + '/seats/' + seatId + '/' + action,
    null,
    { headers: { 'X-Client-Id': clientId }, tags: { name: action } });
}

// Builds the world the run measures: one venue, one event on sale immediately,
// one seat map. Seats are split rather than shared — the contention pool has to
// stay small to keep pressure on one row, the sale pool has to be large enough to
// survive its window, and each chaos phase needs a pool of its own so that one
// fault's drained inventory is not the next fault's starting condition.
export function setup() {
  // Give the counter a sample, so its threshold is evaluated against real data
  // even on a run where nothing goes wrong.
  unexpected.add(0);

  const pools = [
    { name: 'contended', count: CONTENDED_SEATS },
    { name: 'sale', count: SALE_SEATS },
    { name: 'redisUp', count: running('redis') ? REDIS_HALF : 0 },
    { name: 'redisDown', count: running('redis') ? REDIS_HALF : 0 },
    { name: 'checkout', count: running('payments') || running('reconciler') ? CHECKOUT_SEATS : 0 },
    { name: 'stall', count: running('stall') ? STALL_SEATS : 0 },
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

  // One hold and release before anybody is measured. The first request through
  // this path pays for JIT, four EF Core models being built and an empty Npgsql
  // pool, and it costs seconds — enough that it was the reported maximum of an
  // entire run whose p99 was 41ms. Warming it here rather than subtracting it
  // later keeps the maximum a number about the system rather than about startup.
  // Called through seatAction rather than hold(), so it lands in no metric.
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

  // Captured last, so it is as close as possible to the moment k6 starts the
  // scenarios: the sale offsets itself against this.
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

// Hold, then hand it straight back. The release keeps the pool from draining, so
// every iteration of every VU meets a seat somebody else is fighting for.
export function contend(data) {
  const clientId = uuid();
  const seatId = pick(data.contended);

  if (hold(data.eventId, seatId, clientId).status !== 200) {
    return;
  }

  const res = seatAction(data.eventId, seatId, clientId, 'release');
  if (res.status === 409) {
    // Legitimate under contention rather than a fault: lost_race means the row
    // moved under us, which is the optimistic-concurrency path doing its job.
    releaseRefused.add(1, { reason: reasonOf(res) });
  } else if (res.status !== 200) {
    unexpected.add(1, { route: 'release', status: String(res.status) });
  }
}

// Hold, then buy. Nothing is retried: a refusal is the answer, and retrying it
// would measure the harness's patience rather than the system's behaviour.
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
      // k6 starts this scenario startTime after the run begins, and data.startedAt
      // was taken as setup ended, so subtracting both leaves time since the sale
      // opened rather than time since the process started.
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

// The checkout path: create an order over held seats, then confirm it, which is
// the only route in this system that reaches Payments. One seat per order, so an
// order's fate is never a partial one and the counters mean what they say.
//
// Nothing is retried here either, and that matters more than it does on the seat
// path: a confirm that timed out is exactly the state 031 and 057 are about, and
// retrying it in the harness would resolve the ambiguity this run exists to
// observe.
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
      // 027: authorised and sold, but the capture went unanswered. Non-terminal
      // and resolved by the next confirm, so it is neither a success nor a fault.
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

// Whether a threshold passed, as k6 recorded it rather than as this file
// recomputes it. Reading it back out is what lets the digest print an invariant
// matrix that cannot disagree with the exit code.
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

  if (running('reconciler')) {
    out += '\n  two reconcilers\n';
    out += line('  no faults', verdict(data, 'unexpected_responses{phase:reconciler}'));
  }

  return out;
}

// k6's default summary is replaced when handleSummary is defined, and keeping it
// would mean importing textSummary from jslib over the network. The numbers that
// matter here are few enough to print directly, and a digest that names them is
// more use than the default wall of output anyway.
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
