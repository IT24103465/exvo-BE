# Exvo load and performance testing

Owner: IT24103465  
Tool: Apache JMeter 5.6.3

The JMeter test plan sends read-only traffic through the API gateway and measures
the Gateway, Catalog, Booking, and optionally Auth services. A one-user setup
thread first checks gateway health and discovers a valid event. The main thread
group then browses events/categories, reads that event, checks Booking availability,
and optionally logs in. It never creates users, bookings, holds, or check-ins.

## Profiles

| Profile | Threads | Ramp-up | Total duration | Intended use |
| --- | ---: | ---: | ---: | --- |
| `smoke` | 1 | 5 seconds | 35 seconds | Connectivity and plan verification |
| `demo` | 10 | 10 seconds | 50 seconds | Fast live assessment demonstration |
| `load` | 30 | 60 seconds | 270 seconds | Expected sustained concurrency |
| `stress` | 100 | 180 seconds | 240 seconds | Find degradation in non-production |

Each thread repeats the same user journey and pauses for a randomized 0.5–1.5
seconds between journeys. HTTP response assertions require status 200. The
post-run performance gate fails automation if the error rate or p95 limits are
breached:

| Profile | Maximum errors | Global p95 |
| --- | ---: | ---: |
| `smoke` | 2% | 1200 ms |
| `demo` / `load` | 1% | 1000 ms |
| `stress` | 3% | 1800 ms |

Catalog/event p95 must also remain below 800 ms, Booking availability below
1200 ms, and optional login below 1500 ms.

## Install and run

Install Java 17 (Java 17–21 is supported by this plan) and Apache JMeter 5.6.3,
then add JMeter's `bin` directory to `PATH` or set `JMETER_HOME`. Java 25 is not
compatible with JMeter 5.6.3's bundled Groovy engine. Start the backend and run
from the repository root:

```powershell
$env:BASE_URL = 'http://localhost:5000'
npm run load:demo
```

Other commands are `npm run load:smoke`, `npm run load:test`, and
`npm run load:stress`. Every run creates a timestamped directory under
`tests/load/results/` containing:

- `results.jtl`: raw sample-level results.
- `jmeter.log`: engine diagnostics.
- `summary.json`: automated threshold decision and endpoint statistics.
- `report/index.html`: interactive native JMeter dashboard.

Use a dedicated non-production account to include AuthService login performance:

```powershell
$env:LOAD_TEST_EMAIL = 'load-test@example.com'
$env:LOAD_TEST_PASSWORD = '<non-production-password>'
npm run load:demo
```

Set `$env:EVENT_ID = '1'` to force an existing event instead of automatic
discovery. Do not run `stress` against production or a shared environment without
explicit team approval.

## GUI demonstration

Open the plan with:

```powershell
npm run load:gui
```

Use the tree to explain the setup thread, HTTP defaults, assertions, controllers,
and main thread group. Edit and inspect plans in the GUI, but execute measured load
with an npm command/CLI mode—the GUI consumes CPU and memory and can distort load
results. After execution, open `report/index.html` and demonstrate:

1. Dashboard summary and APDEX.
2. Per-request statistics and p95/p99 response times.
3. Active Threads Over Time, Hits per Second, and Transactions per Second.
4. Error table and response-code graph.
5. `summary.json` showing the automated PASS/FAIL decision.

## Automation

The `Backend Load Testing` workflow downloads and SHA-512 verifies Apache JMeter,
loads the JMX without sending traffic on relevant pushes/pull requests, supports
manual test execution, and schedules a weekly `load` profile. Active runs produce
JTL/HTML/JSON artifacts even when a performance gate fails.

Configure repository variable `LOAD_TEST_BASE_URL` with a deployed non-production
gateway. Optional secrets `LOAD_TEST_EMAIL` and `LOAD_TEST_PASSWORD` enable login
traffic. If either is absent, the login controller is skipped.

## Two-minute demo and VIVA notes

Run `npm run load:demo`, explain that ten threads represent ten concurrent users,
and open the generated HTML report. Threads are not requests per second: actual
throughput depends on response times, the four HTTP operations, and think time.

- Load testing validates expected traffic; stress testing searches for saturation.
- p95 means 95% of requests completed at or below that time and exposes slow-tail
  behavior that an average can hide.
- Assertions prove correctness under concurrency; a fast HTTP 500 is still a fail.
- Ramp-up introduces users progressively and avoids an unrealistic instant wall.
- APDEX summarizes satisfied/tolerating/frustrated response experiences, while raw
  percentiles and errors provide the evidence behind that score.
- GUI listeners are useful for debugging small runs but expensive during real load;
  CLI execution plus the post-run HTML dashboard produces more trustworthy results.
- Read-only endpoints make repeated CI/demo runs safer and deterministic.
