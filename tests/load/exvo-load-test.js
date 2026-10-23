import http from 'k6/http';
import { check, fail, group, sleep } from 'k6';
import { Rate, Trend } from 'k6/metrics';

const baseUrl = (__ENV.BASE_URL || 'http://localhost:5000').replace(/\/$/, '');
const profileName = (__ENV.PROFILE || 'smoke').toLowerCase();
const configuredEventId = Number.parseInt(__ENV.EVENT_ID || '', 10);
const authEnabled = Boolean(__ENV.LOAD_TEST_EMAIL && __ENV.LOAD_TEST_PASSWORD);
const resultsDirectory = (__ENV.RESULTS_DIR || 'tests/load/results').replace(/\/$/, '');

const profiles = {
  smoke: {
    stages: [
      { duration: '10s', target: 1 },
      { duration: '20s', target: 1 },
      { duration: '5s', target: 0 },
    ],
    latency: 'p(95)<1200',
    endpointLatency: 'p(95)<1000',
    errorRate: 'rate<0.02',
  },
  demo: {
    stages: [
      { duration: '10s', target: 5 },
      { duration: '30s', target: 10 },
      { duration: '10s', target: 0 },
    ],
    latency: 'p(95)<1000',
    endpointLatency: 'p(95)<800',
    errorRate: 'rate<0.01',
  },
  load: {
    stages: [
      { duration: '30s', target: 10 },
      { duration: '1m', target: 10 },
      { duration: '30s', target: 30 },
      { duration: '2m', target: 30 },
      { duration: '30s', target: 0 },
    ],
    latency: 'p(95)<1000',
    endpointLatency: 'p(95)<800',
    errorRate: 'rate<0.01',
  },
  stress: {
    stages: [
      { duration: '30s', target: 20 },
      { duration: '1m', target: 40 },
      { duration: '1m', target: 70 },
      { duration: '1m', target: 100 },
      { duration: '30s', target: 0 },
    ],
    latency: 'p(95)<1800',
    endpointLatency: 'p(95)<1500',
    errorRate: 'rate<0.03',
  },
};

if (!profiles[profileName]) {
  throw new Error(`Unknown PROFILE '${profileName}'. Use smoke, demo, load, or stress.`);
}

const profile = profiles[profileName];
const endpointErrors = new Rate('endpoint_errors');
const catalogListDuration = new Trend('catalog_list_duration', true);
const eventDetailDuration = new Trend('event_detail_duration', true);
const bookingAvailabilityDuration = new Trend('booking_availability_duration', true);
const loginDuration = new Trend('login_duration', true);

export const options = {
  scenarios: {
    gateway_traffic: {
      executor: 'ramping-vus',
      startVUs: 0,
      gracefulRampDown: '10s',
      stages: profile.stages,
      tags: { profile: profileName },
    },
  },
  thresholds: {
    checks: ['rate>0.99'],
    http_req_failed: [profile.errorRate],
    http_req_duration: [profile.latency],
    endpoint_errors: [profile.errorRate],
    catalog_list_duration: [profile.endpointLatency],
    event_detail_duration: [profile.endpointLatency],
    booking_availability_duration: ['p(95)<1200'],
    login_duration: ['p(95)<1500'],
  },
  userAgent: 'exvo-k6-load-tests/1.0',
  discardResponseBodies: false,
  summaryTrendStats: ['avg', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

function get(path, name) {
  return http.get(`${baseUrl}${path}`, {
    tags: { endpoint: name, name },
    timeout: '10s',
  });
}

function record(response, trend, checkName) {
  trend.add(response.timings.duration);
  const succeeded = check(response, {
    [checkName]: (result) => result.status === 200,
  });
  endpointErrors.add(!succeeded);
  return succeeded;
}

export function setup() {
  const health = get('/health', 'GET /health');
  if (!check(health, { 'gateway is reachable before load starts': (r) => r.status === 200 })) {
    fail(`Gateway preflight failed: ${baseUrl}/health returned HTTP ${health.status}`);
  }

  if (Number.isInteger(configuredEventId) && configuredEventId > 0) {
    return { eventId: configuredEventId };
  }

  const events = get('/api/catalog/events', 'SETUP GET /api/catalog/events');
  if (events.status !== 200) {
    console.warn(`Event discovery returned HTTP ${events.status}; event and booking requests will be skipped.`);
    return { eventId: null };
  }

  try {
    const body = events.json();
    return { eventId: Array.isArray(body) && body.length > 0 ? body[0].id : null };
  } catch (_) {
    console.warn('Event discovery did not return a JSON array; event and booking requests will be skipped.');
    return { eventId: null };
  }
}

export default function (data) {
  group('public catalog browsing', () => {
    const events = get('/api/catalog/events', 'GET /api/catalog/events');
    record(events, catalogListDuration, 'catalog events returns 200');

    const categories = get('/api/catalog/categories', 'GET /api/catalog/categories');
    const categoriesSucceeded = check(categories, {
      'catalog categories returns 200': (response) => response.status === 200,
    });
    endpointErrors.add(!categoriesSucceeded);
  });

  if (data.eventId) {
    group('event and booking reads', () => {
      const detail = get(`/api/catalog/events/${data.eventId}`, 'GET /api/catalog/events/:id');
      record(detail, eventDetailDuration, 'event detail returns 200');

      const availability = get(
        `/api/booking/events/${data.eventId}/availability`,
        'GET /api/booking/events/:id/availability',
      );
      record(availability, bookingAvailabilityDuration, 'booking availability returns 200');
    });
  }

  if (authEnabled) {
    group('authentication', () => {
      const response = http.post(
        `${baseUrl}/api/auth/login`,
        JSON.stringify({
          email: __ENV.LOAD_TEST_EMAIL,
          password: __ENV.LOAD_TEST_PASSWORD,
        }),
        {
          headers: { 'Content-Type': 'application/json' },
          tags: { endpoint: 'POST /api/auth/login', name: 'POST /api/auth/login' },
          timeout: '10s',
        },
      );
      record(response, loginDuration, 'login returns 200');
    });
  }

  sleep(0.5 + Math.random());
}

export function handleSummary(data) {
  const duration = data.metrics.http_req_duration?.values || {};
  const failures = data.metrics.http_req_failed?.values || {};
  const checks = data.metrics.checks?.values || {};
  const requests = data.metrics.http_reqs?.values || {};
  const text = [
    '',
    `Exvo k6 ${profileName} profile`,
    `Requests: ${requests.count || 0} (${(requests.rate || 0).toFixed(2)}/s)`,
    `HTTP p95: ${(duration['p(95)'] || 0).toFixed(2)} ms`,
    `HTTP failures: ${((failures.rate || 0) * 100).toFixed(2)}%`,
    `Checks passed: ${((checks.rate || 0) * 100).toFixed(2)}%`,
    '',
  ].join('\n');

  return {
    stdout: text,
    [`${resultsDirectory}/summary.json`]: JSON.stringify(data, null, 2),
  };
}
