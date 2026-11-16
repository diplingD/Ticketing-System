// Throughput test: a constant stream of purchases (open model) for a fixed time.
// Unlike purchase.js (one burst of N users), new requests keep arriving at RATE per second
// no matter how fast the server answers, so p95 is computed from thousands of requests.
//
// Run: docker compose run --rm k6 run /scripts/throughput.js
// Options: -e RATE=200 -e DURATION=30s -e EVENT_ID=2 -e BASE_URL=http://api:8080

import http from 'k6/http';
import { check } from 'k6';
import { Counter } from 'k6/metrics';

const BASE_URL = __ENV.BASE_URL || 'http://api:8080';
const EVENT_ID = __ENV.EVENT_ID || '2';          // event 2 has 100,000 tickets
const RATE = Number(__ENV.RATE || 200);           // purchases per second
const DURATION = __ENV.DURATION || '30s';

const purchased = new Counter('purchases_ok');
const soldOut = new Counter('purchases_sold_out');
const conflicts = new Counter('purchases_conflict');

// 409 ("Sold out" or "Concurrent update") is an expected business outcome, not a failure.
http.setResponseCallback(http.expectedStatuses(200, 409));

export const options = {
  scenarios: {
    steady_load: {
      executor: 'constant-arrival-rate', // starts RATE iterations per timeUnit, independent of response time
      rate: RATE,
      timeUnit: '1s',
      duration: DURATION,
      preAllocatedVUs: RATE,             // enough VUs for ~1 s latency
      maxVUs: RATE * 5,                  // if the server slows down, k6 adds VUs to keep the rate;
                                         // when even these run out, k6 reports dropped_iterations
    },
  },
};

// Runs once, before the load starts: full stock, no orders.
export function setup() {
  const res = http.post(`${BASE_URL}/events/${EVENT_ID}/reset`, null, {
    responseCallback: http.expectedStatuses(204),
  });
  if (res.status !== 204) {
    throw new Error(`Reset failed with status ${res.status}`);
  }
}

// One iteration = one purchase by a new user.
export default function () {
  const res = http.post(
    `${BASE_URL}/events/${EVENT_ID}/purchase`,
    JSON.stringify({ userId: `user-${__VU}-${__ITER}` }),
    { headers: { 'Content-Type': 'application/json' } },
  );

  check(res, { 'status is 200 or 409': (r) => r.status === 200 || r.status === 409 });

  if (res.status === 200) purchased.add(1);
  else if (res.status === 409 && res.body.includes('Sold out')) soldOut.add(1);
  else if (res.status === 409) conflicts.add(1);
}

// With 100,000 tickets nothing gets oversold, so we check the invariant directly:
// total - available must equal sold. Any difference is a lost update.
export function teardown() {
  const ev = http.get(`${BASE_URL}/events/${EVENT_ID}`).json();
  const lostUpdates = ev.soldTickets - (ev.totalTickets - ev.availableTickets);

  console.log(
    `RESULT total=${ev.totalTickets} available=${ev.availableTickets} ` +
    `sold=${ev.soldTickets} lost_updates=${lostUpdates}`,
  );
}
