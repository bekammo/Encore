// Encore's flash-sale load harness.
//
// The project's central claim is that seat contention is handled correctly under
// flash-sale load. Until now that claim was proven by ConcurrentHoldTests: fifty
// tasks in one process, against a Testcontainers Postgres, asserting exactly one
// winner. That is a correctness proof and it stays the important one. It is not a
// measurement — it says nothing about what the hold path costs at the ninety-ninth
// percentile, or where the Redis lock stops helping, or whether xmin rejections
// turn into a retry storm when two hundred clients want the same row.
//
// This file answers those questions and asserts the invariant while it does it.
// The threshold on seats_sold is the load-test equivalent of the unit suite's
// oversell assertion: if the API ever sells more seats than exist, k6 exits
// non-zero and the run fails. Everything else here is measurement.
//
// Two scenarios, run one after the other rather than together, because they
// answer different questions and overlapping them would make neither number
// attributable:
//
//   contention — many clients, a handful of seats, hold then immediately release.
//                The pool never drains, so contention stays at full pressure for
//                the whole window. This is the hot-path number.
//   flash_sale — many clients, a real seat map, hold then purchase. Inventory
//                drains the way it would on sale day, and the refusal mix shifts
//                from already_held to already_sold as it does.
//
// No sleep between iterations anywhere. A flash sale is not a Poisson arrival
// process with think time; it is everyone pressing the button at once.
import http from 'k6/http';
import { Counter, Rate, Trend } from 'k6/metrics';

const BASE_URL = __ENV.ENCORE_BASE_URL || 'http://api:8080';

// Where handleSummary writes the machine-readable summary. Set it to an empty
// string to run without a mounted volume and get the digest on stdout only.
const RESULTS_DIR = __ENV.ENCORE_RESULTS_DIR === undefined ? '/results' : __ENV.ENCORE_RESULTS_DIR;

const CONTENDED_SEATS = Number(__ENV.CONTENDED_SEATS || 5);
const CONTENTION_VUS = Number(__ENV.CONTENTION_VUS || 50);
const CONTENTION_SECONDS = Number(__ENV.CONTENTION_SECONDS || 60);

const SALE_SEATS = Number(__ENV.SALE_SEATS || 500);
const SALE_VUS = Number(__ENV.SALE_VUS || 100);
const SALE_SECONDS = Number(__ENV.SALE_SECONDS || 60);

// Long enough for the first scenario's in-flight requests to land before the
// second one starts competing for the same connection pool.
const GAP_SECONDS = 5;

const holdLatency = new Trend('hold_latency', true);
const purchaseLatency = new Trend('purchase_latency', true);
const holdsWon = new Counter('holds_won');
const holdsRefused = new Counter('holds_refused');
const holdWinRate = new Rate('hold_win_rate');
const seatsSold = new Counter('seats_sold');
const saleRefused = new Counter('sale_refused');
const releaseRefused = new Counter('release_refused');
const unexpected = new Counter('unexpected_responses');

// How long after the sale opened each seat went. The interesting number from the
// sale scenario is not its latency — it is how long the inventory lasted, because
// at a few thousand attempts a second any finite seat map drains in seconds and
// everything after that is the system refusing politely. min is the first sale and
// max is the sell-out.
const soldAfter = new Trend('sold_after_ms', true);

export const options = {
  summaryTrendStats: ['min', 'avg', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],

  scenarios: {
    contention: {
      executor: 'constant-vus',
      vus: CONTENTION_VUS,
      duration: CONTENTION_SECONDS + 's',
      exec: 'contend',
      tags: { phase: 'contention' },
      gracefulStop: '10s',
    },
    flash_sale: {
      executor: 'constant-vus',
      vus: SALE_VUS,
      duration: SALE_SECONDS + 's',
      startTime: (CONTENTION_SECONDS + GAP_SECONDS) + 's',
      exec: 'buy',
      tags: { phase: 'sale' },
      gracefulStop: '10s',
    },
  },

  thresholds: {
    // The invariant. Every 200 from a purchase is one seat leaving inventory,
    // and each iteration buys at most once with a client id nobody else uses, so
    // this counter is a count of distinct seats sold. More of them than there are
    // seats means the system oversold, which is the one outcome that must fail
    // the run rather than appear in a report.
    seats_sold: ['count<=' + SALE_SEATS],

    // Anything outside the statuses these routes are documented to return — a
    // 500, a timeout, a 415 — is a fault rather than a refusal.
    unexpected_responses: ['count==0'],

    // The rest of these are submetric declarations, not assertions: k6 only puts
    // a tagged submetric in the summary if a threshold names it, and >=0 is
    // always true. This is what makes the digest below able to break refusals
    // down by reason and latency down by phase, which is the whole point of
    // running two scenarios.
    'hold_latency{phase:contention}': ['p(99)>=0'],
    'hold_latency{phase:sale}': ['p(99)>=0'],
    'holds_refused{reason:already_held}': ['count>=0'],
    'holds_refused{reason:already_sold}': ['count>=0'],
    'holds_refused{reason:lost_race}': ['count>=0'],
    'holds_refused{reason:hold_cap_reached}': ['count>=0'],
    'holds_refused{reason:concurrent_request_in_flight}': ['count>=0'],
  },
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
// stay small to keep pressure on one row, and the sale pool has to be large
// enough to survive the window without every request becoming already_sold.
export function setup() {
  // Give the counter a sample, so its threshold is evaluated against real data
  // even on a run where nothing goes wrong.
  unexpected.add(0);

  const venue = postJson('/catalog/venues', {
    name: 'Encore load harness arena',
    address: 'One Compose Lane',
    capacity: CONTENDED_SEATS + SALE_SEATS,
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
  const seats = postJson('/events/' + eventId + '/seats', {
    count: CONTENDED_SEATS + SALE_SEATS,
  });
  if (seats.status !== 201) {
    throw new Error('setup: creating the seat map returned ' + seats.status + ' ' + seats.body);
  }

  const seatIds = seats.json('seatIds');
  const saleSeats = seatIds.slice(CONTENDED_SEATS);

  // One hold and release before anybody is measured. The first request through
  // this path pays for JIT, four EF Core models being built and an empty Npgsql
  // pool, and it costs seconds — enough that it was the reported maximum of an
  // entire run whose p99 was 41ms. Warming it here rather than subtracting it
  // later keeps the maximum a number about the system rather than about startup.
  // Called through seatAction rather than hold(), so it lands in no metric.
  const warmupClient = uuid();
  seatAction(eventId, saleSeats[0], warmupClient, 'hold');
  seatAction(eventId, saleSeats[0], warmupClient, 'release');

  return {
    eventId: eventId,
    // Captured last, so it is as close as possible to the moment k6 starts the
    // scenarios: the sale offsets itself against this.
    startedAt: Date.now(),
    contendedSeats: seatIds.slice(0, CONTENDED_SEATS),
    saleSeats: saleSeats,
  };
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
  const seatId = pick(data.contendedSeats);

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
export function buy(data) {
  const clientId = uuid();
  const seatId = pick(data.saleSeats);

  if (hold(data.eventId, seatId, clientId).status !== 200) {
    return;
  }

  const res = seatAction(data.eventId, seatId, clientId, 'purchase');
  purchaseLatency.add(res.timings.duration);

  if (res.status === 200) {
    seatsSold.add(1);
    // k6 starts this scenario startTime after the run begins, and data.startedAt
    // was taken as setup ended, so subtracting both leaves time since the sale
    // opened rather than time since the process started.
    soldAfter.add(Date.now() - data.startedAt - (CONTENTION_SECONDS + GAP_SECONDS) * 1000);
  } else if (res.status === 409) {
    saleRefused.add(1, { reason: reasonOf(res) });
  } else {
    unexpected.add(1, { route: 'purchase', status: String(res.status) });
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
  return '  ' + (label + '                      ').slice(0, 22) + value + '\n';
}

// k6's default summary is replaced when handleSummary is defined, and keeping it
// would mean importing textSummary from jslib over the network. The numbers that
// matter here are few enough to print directly, and a digest that names them is
// more use than the default wall of output anyway.
export function handleSummary(data) {
  const m = data.metrics;
  const sold = total(m, 'seats_sold');

  let out = '\nEncore flash-sale baseline\n\n';
  out += line('contention', CONTENTION_VUS + ' vus, ' + CONTENDED_SEATS + ' seats, ' + CONTENTION_SECONDS + 's');
  out += line('sale', SALE_VUS + ' vus, ' + SALE_SEATS + ' seats, ' + SALE_SECONDS + 's');
  out += '\n';
  out += line('hold ms, contention',
    'med ' + stat(m, 'hold_latency{phase:contention}', 'med')
    + '  p95 ' + stat(m, 'hold_latency{phase:contention}', 'p(95)')
    + '  p99 ' + stat(m, 'hold_latency{phase:contention}', 'p(99)')
    + '  max ' + stat(m, 'hold_latency{phase:contention}', 'max'));
  out += line('hold ms, sale',
    'med ' + stat(m, 'hold_latency{phase:sale}', 'med')
    + '  p95 ' + stat(m, 'hold_latency{phase:sale}', 'p(95)')
    + '  p99 ' + stat(m, 'hold_latency{phase:sale}', 'p(99)')
    + '  max ' + stat(m, 'hold_latency{phase:sale}', 'max'));
  out += line('purchase ms',
    'med ' + stat(m, 'purchase_latency', 'med')
    + '  p95 ' + stat(m, 'purchase_latency', 'p(95)')
    + '  p99 ' + stat(m, 'purchase_latency', 'p(99)')
    + '  max ' + stat(m, 'purchase_latency', 'max'));
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
  out += line('seats sold', sold + ' of ' + SALE_SEATS);
  out += line('sold out after',
    'first ' + stat(m, 'sold_after_ms', 'min') + ' ms, last ' + stat(m, 'sold_after_ms', 'max') + ' ms');
  out += line('sale refused', String(total(m, 'sale_refused')));
  out += line('release refused', String(total(m, 'release_refused')));
  out += line('unexpected', String(total(m, 'unexpected_responses')));
  out += line('oversold', sold > SALE_SEATS ? 'YES — the run failed' : 'no');
  out += '\n';

  const summary = { stdout: out };
  if (RESULTS_DIR) {
    const stamp = new Date().toISOString().replace(/[:.]/g, '-');
    summary[RESULTS_DIR + '/summary-' + stamp + '.json'] = JSON.stringify(data, null, 2);
  }
  return summary;
}
