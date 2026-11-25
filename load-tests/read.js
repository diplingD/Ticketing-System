// Read test: a constant stream of GET /events/{id} (open model) for a fixed time.
// Reads take no row locks, so unlike purchases there is no hot row
//
// Run: docker compose run --rm k6 run /scripts/read.js
// Options: -e RATE=200 -e DURATION=30s -e EVENT_ID=1 -e BASE_URL=http://api:8080

import http from 'k6/http';
import { check } from 'k6';

const BASE_URL = __ENV.BASE_URL || 'http://api:8080';
const EVENT_ID = __ENV.EVENT_ID || '1';   // event 1 has at most 100 orders, so the COUNT stays cheap
const RATE = Number(__ENV.RATE || 200);   // reads per second
const DURATION = __ENV.DURATION || '30s';

export const options = {
  scenarios: {
    steady_reads: {
      executor: 'constant-arrival-rate', // one of the k6's default executors: starts RATE iterations per second
      rate: RATE,
      timeUnit: '1s',
      duration: DURATION,
      preAllocatedVUs: RATE,             // starting number of VUs
      maxVUs: RATE * 5,                  // when these run out, k6 reports dropped_iterations
    },
  },
};

// This function is executed by one independent VU
export default function () {
  const res = http.get(`${BASE_URL}/events/${EVENT_ID}`);
  check(res, { 'status is 200': (r) => r.status === 200 });
}
