# Exvo load and performance testing

Owner: IT24103465  
Tool: Grafana k6

This suite sends read-only traffic through the API gateway and measures the
Gateway, Catalog, Booking, and optionally Auth services. It discovers a valid
event before the run, so event-detail and booking-availability traffic uses real
application data. It never creates users, bookings, seat holds, or check-ins.

## Profiles

| Profile | Shape | Intended use |
| --- | --- | --- |
| `smoke` | 1 virtual user, 35 seconds | Connectivity and script verification |
| `demo` | Ramp to 10 users, 50 seconds | Fast, reliable live assessment demo |
| `load` | Ramp to 30 users, 4.5 minutes | Expected sustained concurrency |
| `stress` | Ramp to 100 users, 4 minutes | Find degradation/breaking behavior in non-production |

The test fails when its performance contract is breached. The normal load goal
is under 1% failed requests/checks and p95 latency under 1 second globally, with
tighter 800 ms p95 goals for core catalog/event reads. Booking availability has
a 1.2 second p95 goal; optional login has a 1.5 second p95 goal. Stress limits are
intentionally more tolerant because its purpose is to locate saturation.

## Run locally

Install k6, start the backend, and run from the repository root:

```powershell
$env:BASE_URL = 'http://localhost:5000'
npm run load:demo
```

Other commands are `npm run load:smoke`, `npm run load:test`, and
`npm run load:stress`. To include AuthService login performance, use a dedicated
non-production account:

```powershell
$env:LOAD_TEST_EMAIL = 'load-test@example.com'
$env:LOAD_TEST_PASSWORD = '<non-production-password>'
npm run load:demo
```

To force a known catalog event instead of automatic discovery:

```powershell
$env:EVENT_ID = '1'
npm run load:demo
```

Docker is an alternative when k6 is not installed:

```powershell
docker run --rm -i `
  -e BASE_URL=http://host.docker.internal:5000 `
  -e PROFILE=demo `
  -e RESULTS_DIR=/work/tests/load/results `
  -e K6_WEB_DASHBOARD=true `
  -e K6_WEB_DASHBOARD_EXPORT=/work/tests/load/results/report.html `
  -v "${PWD}:/work" -w /work grafana/k6:0.54.0 `
  run tests/load/exvo-load-test.js
```

Do not run `stress` against production or a shared environment without explicit
team approval. Load generation consumes real application and database capacity.

## Automation and reports

The `Backend Load Testing` GitHub Actions workflow validates the script on
relevant pushes and pull requests. It runs an actual test when manually
dispatched, and runs the `load` profile weekly against the URL stored in the
repository variable `LOAD_TEST_BASE_URL`.

Configure these GitHub settings:

- Variable `LOAD_TEST_BASE_URL`: deployed non-production gateway URL.
- Secrets `LOAD_TEST_EMAIL` and `LOAD_TEST_PASSWORD`: optional test-account
  credentials. If either is absent, login traffic is skipped.

Each active workflow run uploads `summary.json` and an interactive `report.html`
artifact, even when a threshold fails. A failed workflow therefore preserves the
evidence needed to diagnose the regression.

Read the output in this order during the demo:

1. `http_reqs` and request rate show the traffic volume/throughput.
2. `http_req_duration` p95 shows the experience of 95% of requests.
3. `http_req_failed` and `endpoint_errors` show transport/status failures.
4. `checks` confirms functional correctness under concurrency.
5. Per-endpoint trends identify whether Catalog, Booking, or Auth is the bottleneck.

## Two-minute demo and VIVA notes

Run `npm run load:demo`, point out the staged ramp from 5 to 10 concurrent virtual
users, then explain the threshold result and p95. A virtual user repeatedly follows
the scripted browsing journey; it is not the same as requests per second. Throughput
depends on response time and the randomized think time.

- Load testing validates expected traffic; stress testing increases traffic until
  service quality degrades.
- p95 is more useful than an average because an average can hide slow outliers.
- Thresholds turn performance objectives into automated pass/fail release signals.
- Checks validate response correctness; a fast HTTP 500 is still a failed test.
- Ramp-up avoids an unrealistic instant traffic wall and reveals the concurrency
  level at which latency or errors begin increasing.
- The test uses read-only endpoints so repeated CI/demo runs are deterministic and
  do not corrupt booking inventory.
