import { readFileSync, writeFileSync } from 'node:fs';

const [resultsPath, profile = 'smoke', summaryPath] = process.argv.slice(2);
if (!resultsPath || !summaryPath) {
  console.error('Usage: node assert-jmeter-results.mjs <results.jtl> <profile> <summary.json>');
  process.exit(2);
}

const strictEndpointLimits = {
  'GET /api/catalog/events': 800,
  'GET /api/catalog/categories': 800,
  'GET /api/catalog/events/:id': 800,
  'GET /api/booking/events/:id/availability': 1200,
  'POST /api/auth/login': 1500,
};

// Smoke verifies deployment health with very few samples and may include Azure
// Container Apps cold-start/network latency. Higher-concurrency profiles retain
// the stricter service objectives below.
const smokeEndpointLimits = {
  'GET /api/catalog/events': 5000,
  'GET /api/catalog/categories': 3000,
  'GET /api/catalog/events/:id': 4000,
  'GET /api/booking/events/:id/availability': 4000,
  'POST /api/auth/login': 5000,
};

const limits = {
  smoke: { errorRate: 0.02, globalP95: 5000, endpoints: smokeEndpointLimits },
  demo: { errorRate: 0.01, globalP95: 1000, endpoints: strictEndpointLimits },
  load: { errorRate: 0.01, globalP95: 1000, endpoints: strictEndpointLimits },
  stress: { errorRate: 0.03, globalP95: 1800, endpoints: strictEndpointLimits },
};
if (!limits[profile]) throw new Error(`Unknown profile '${profile}'.`);

function parseCsvLine(line) {
  const values = [];
  let value = '';
  let quoted = false;
  for (let index = 0; index < line.length; index += 1) {
    const character = line[index];
    if (character === '"') {
      if (quoted && line[index + 1] === '"') {
        value += '"';
        index += 1;
      } else {
        quoted = !quoted;
      }
    } else if (character === ',' && !quoted) {
      values.push(value);
      value = '';
    } else {
      value += character;
    }
  }
  values.push(value);
  return values;
}

function percentile(values, percentage) {
  if (values.length === 0) return null;
  const sorted = [...values].sort((left, right) => left - right);
  return sorted[Math.max(0, Math.ceil((percentage / 100) * sorted.length) - 1)];
}

const lines = readFileSync(resultsPath, 'utf8').replace(/^\uFEFF/, '').trim().split(/\r?\n/);
if (lines.length < 2) throw new Error('JMeter produced no sample results.');

const headers = parseCsvLine(lines[0]);
const indexes = Object.fromEntries(headers.map((header, index) => [header, index]));
for (const required of ['timeStamp', 'elapsed', 'label', 'success']) {
  if (indexes[required] === undefined) throw new Error(`JTL is missing required '${required}' column.`);
}

const samples = lines.slice(1).map(parseCsvLine).map((columns) => ({
  timestamp: Number(columns[indexes.timeStamp]),
  elapsed: Number(columns[indexes.elapsed]),
  label: columns[indexes.label],
  success: columns[indexes.success].toLowerCase() === 'true',
})).filter((sample) => !sample.label.startsWith('SETUP '));

if (samples.length === 0) throw new Error('JMeter produced no main workload samples.');

const endpointLimits = limits[profile].endpoints;
const requiredEndpoints = new Set([
  'GET /api/catalog/events',
  'GET /api/catalog/categories',
  'GET /api/catalog/events/:id',
  'GET /api/booking/events/:id/availability',
]);

const endpointStatistics = {};
for (const [label, threshold] of Object.entries(endpointLimits)) {
  const endpointSamples = samples.filter((sample) => sample.label === label);
  if (endpointSamples.length === 0) {
    if (requiredEndpoints.has(label)) endpointStatistics[label] = { samples: 0, thresholdMilliseconds: threshold };
    continue;
  }
  endpointStatistics[label] = {
    samples: endpointSamples.length,
    errors: endpointSamples.filter((sample) => !sample.success).length,
    p95Milliseconds: percentile(endpointSamples.map((sample) => sample.elapsed), 95),
    thresholdMilliseconds: threshold,
  };
}

const errors = samples.filter((sample) => !sample.success).length;
const errorRate = errors / samples.length;
const globalP95 = percentile(samples.map((sample) => sample.elapsed), 95);
const firstTimestamp = Math.min(...samples.map((sample) => sample.timestamp));
const finalTimestamp = Math.max(...samples.map((sample) => sample.timestamp + sample.elapsed));
const elapsedSeconds = Math.max((finalTimestamp - firstTimestamp) / 1000, 0.001);
const violations = [];

if (errorRate > limits[profile].errorRate) {
  violations.push(`Error rate ${(errorRate * 100).toFixed(2)}% exceeds ${(limits[profile].errorRate * 100).toFixed(2)}%.`);
}
if (globalP95 > limits[profile].globalP95) {
  violations.push(`Global p95 ${globalP95} ms exceeds ${limits[profile].globalP95} ms.`);
}
for (const endpoint of requiredEndpoints) {
  if (!endpointStatistics[endpoint] || endpointStatistics[endpoint].samples === 0) {
    violations.push(`Required endpoint '${endpoint}' had no samples; provide an existing EVENT_ID or seed catalog data.`);
  }
}
for (const [label, statistics] of Object.entries(endpointStatistics)) {
  if (statistics.p95Milliseconds > statistics.thresholdMilliseconds) {
    violations.push(`${label} p95 ${statistics.p95Milliseconds} ms exceeds ${statistics.thresholdMilliseconds} ms.`);
  }
}

const summary = {
  profile,
  passed: violations.length === 0,
  requests: samples.length,
  errors,
  errorRatePercent: Number((errorRate * 100).toFixed(2)),
  p95Milliseconds: globalP95,
  throughputPerSecond: Number((samples.length / elapsedSeconds).toFixed(2)),
  limits: {
    errorRatePercent: limits[profile].errorRate * 100,
    globalP95Milliseconds: limits[profile].globalP95,
  },
  endpoints: endpointStatistics,
  violations,
};

writeFileSync(summaryPath, `${JSON.stringify(summary, null, 2)}\n`, 'utf8');

console.log('\nExvo JMeter performance gate');
console.log(`Profile: ${profile}`);
console.log(`Requests: ${summary.requests} (${summary.throughputPerSecond}/s)`);
console.log(`Global p95: ${summary.p95Milliseconds} ms`);
console.log(`Errors: ${summary.errors} (${summary.errorRatePercent}%)`);
console.log(`Result: ${summary.passed ? 'PASS' : 'FAIL'}`);
for (const violation of violations) console.error(`- ${violation}`);

process.exit(summary.passed ? 0 : 1);
