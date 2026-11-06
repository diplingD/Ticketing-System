// Flash sale: USERS people try to buy a ticket at the same moment, each exactly once.
// Goal: prove overselling of the naive purchase (sold tickets > total tickets).
//
// Run: docker compose run --rm k6 run /scripts/purchase.js
// Options: -e USERS=1000 -e EVENT_ID=1 -e BASE_URL=http://api:8080

import http from 'k6/http';
import { check } from 'k6';
import { Counter } from 'k6/metrics';

const BASE_URL = __ENV.BASE_URL || 'http://api:8080';
const EVENT_ID = __ENV.EVENT_ID || '1';
const USERS = Number(__ENV.USERS || 500);

const purchased = new Counter('purchases_ok');
const soldOut = new Counter('purchases_sold_out');
const conflicts = new Counter('purchases_conflict'); // optimistic locking: lost the race, ticket may still be free

// 409 ("Sold out" or "Concurrent update") is an expected business outcome, not a failure.
// With this, http_req_failed counts only real failures (5xx, timeouts, 404, 400...).
http.setResponseCallback(http.expectedStatuses(200, 409));

export const options = {
  scenarios: {
    flash_sale: {
      executor: 'per-vu-iterations', // every VU runs the default function exactly `iterations` times
      vus: USERS,                    // all VUs are initialized first, then start together
      iterations: 1,
      maxDuration: '1m',
    },
  },
};

// Runs once, before the load starts: every test begins with 100 free tickets and no orders.
export function setup() {
  const res = http.post(`${BASE_URL}/events/${EVENT_ID}/reset`, null, {
    responseCallback: http.expectedStatuses(204),
  });
  if (res.status !== 204) {
    throw new Error(`Reset failed with status ${res.status}`);
  }
}

// One iteration = one user clicks "Buy".
export default function () {
  const res = http.post(
    `${BASE_URL}/events/${EVENT_ID}/purchase`,
    JSON.stringify({ userId: `user-${__VU}` }),
    { headers: { 'Content-Type': 'application/json' } },
  );

  check(res, { 'status is 200 or 409': (r) => r.status === 200 || r.status === 409 });

  if (res.status === 200) purchased.add(1);
  else if (res.status === 409 && res.body.includes('Sold out')) soldOut.add(1);
  else if (res.status === 409) conflicts.add(1);
}

// Runs once, after the load: compare orders in the database with the number of tickets.
export function teardown() {
  const ev = http.get(`${BASE_URL}/events/${EVENT_ID}`).json();
  const oversold = Math.max(ev.soldTickets - ev.totalTickets, 0);

  console.log(
    `RESULT total=${ev.totalTickets} available=${ev.availableTickets} ` +
    `sold=${ev.soldTickets} oversold=${oversold}`,
  );
}
