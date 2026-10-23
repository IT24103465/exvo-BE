import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const testDirectory = dirname(fileURLToPath(import.meta.url));
const gate = join(testDirectory, 'assert-jmeter-results.mjs');

const azureSmokeSamples = {
  'GET /api/catalog/events': [1258, 1400, 2500, 3106],
  'GET /api/catalog/categories': [900, 1000, 1100, 1271],
  'GET /api/catalog/events/:id': [1400, 1500, 1700, 1879],
  'GET /api/booking/events/:id/availability': [1800, 2000, 2200, 2319],
};

function runGate(profile, failedSample = null) {
  const directory = mkdtempSync(join(tmpdir(), 'exvo-jmeter-gate-'));
  const resultsPath = join(directory, 'results.jtl');
  const summaryPath = join(directory, 'summary.json');
  const rows = ['timeStamp,elapsed,label,success'];
  let timestamp = 1_790_000_000_000;
  let sampleIndex = 0;

  // Setup warm-up latency remains visible in JTL/HTML but must not influence
  // the measured workload gate.
  rows.push(`${timestamp},8160,SETUP GET /api/booking/events/:id/availability,true`);
  timestamp += 8170;

  for (const [label, durations] of Object.entries(azureSmokeSamples)) {
    for (const elapsed of durations) {
      const success = sampleIndex !== failedSample;
      rows.push(`${timestamp},${elapsed},${label},${success}`);
      timestamp += elapsed + 10;
      sampleIndex += 1;
    }
  }

  writeFileSync(resultsPath, `${rows.join('\n')}\n`, 'utf8');
  const result = spawnSync(process.execPath, [gate, resultsPath, profile, summaryPath], {
    encoding: 'utf8',
  });
  const summary = JSON.parse(readFileSync(summaryPath, 'utf8'));
  rmSync(directory, { recursive: true, force: true });
  return { result, summary };
}

test('Azure-like baseline passes the deployment-oriented smoke gates', () => {
  const { result, summary } = runGate('smoke');

  assert.equal(result.status, 0, result.stderr);
  assert.equal(summary.passed, true);
  assert.equal(summary.requests, 16);
  assert.equal(summary.p95Milliseconds, 3106);
  assert.equal(summary.limits.globalP95Milliseconds, 5000);
  assert.equal(summary.endpoints['GET /api/catalog/categories'].thresholdMilliseconds, 3000);
});

test('the same latency still fails the strict expected-load gates', () => {
  const { result, summary } = runGate('load');

  assert.equal(result.status, 1);
  assert.equal(summary.passed, false);
  assert.ok(summary.violations.some((violation) => violation.startsWith('Global p95')));
  assert.equal(summary.endpoints['GET /api/catalog/events'].thresholdMilliseconds, 800);
});

test('smoke continues to fail when the error-rate objective is breached', () => {
  const { result, summary } = runGate('smoke', 0);

  assert.equal(result.status, 1);
  assert.equal(summary.passed, false);
  assert.equal(summary.errorRatePercent, 6.25);
  assert.ok(summary.violations.some((violation) => violation.startsWith('Error rate')));
});
