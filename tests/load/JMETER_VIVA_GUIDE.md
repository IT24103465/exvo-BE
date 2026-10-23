# Apache JMeter load-testing runbook and viva guide

- Owner: IT24103465
- Module: SE3112 — Advanced Software Engineering
- Individual area: Load and Performance Testing

This guide explains how to run, demonstrate, interpret, and defend the Exvo
load-testing implementation. The source of truth is
`tests/load/exvo-load-test.jmx`; npm commands and GitHub Actions both execute
that same test plan.

## 1. What was implemented

The test sends concurrent, read-only user journeys through the Exvo API Gateway.
This measures the gateway and downstream Catalog and Booking services. Auth login
can also be included with a dedicated test account.

One complete default journey contains:

1. `GET /api/catalog/events`
2. `GET /api/catalog/categories`
3. `GET /api/catalog/events/{eventId}`
4. `GET /api/booking/events/{eventId}/availability`
5. Optional `POST /api/auth/login`
6. Random think time between 0.5 and 1.5 seconds

Before load begins, a JMeter Setup Thread Group calls `/health`, requests the
event list, and extracts the first event ID. This makes event and Booking requests
use real application data without hard-coding an ID.

The suite provides:

- Smoke, demo, expected-load, and stress profiles.
- Gradual thread ramp-up rather than an instant traffic wall.
- HTTP 200 response assertions on every measured endpoint.
- Automatic event discovery with an optional `EVENT_ID` override.
- Optional login traffic without storing credentials in source control.
- JTL raw results, JMeter logs, native HTML dashboards, and JSON summaries.
- Automated error-rate, global p95, and endpoint-specific p95 gates.
- GitHub Actions validation, manual execution, weekly execution, and artifacts.

## 2. Test architecture

```text
Developer or GitHub Actions
        |
        v
run-jmeter.mjs ---- selects profile and target
        |
        v
exvo-load-test.jmx
        |
        +-- Setup Thread Group
        |     +-- GET /health
        |     +-- GET /api/catalog/events
        |     +-- JSON Extractor: first event ID
        |     +-- Groovy: publish ID as a global JMeter property
        |
        +-- Main Thread Group
              +-- Catalog events
              +-- Catalog categories
              +-- Event details
              +-- Booking availability
              +-- Optional Auth login
              +-- Random user think time
        |
        v
results.jtl + jmeter.log + HTML dashboard
        |
        v
assert-jmeter-results.mjs
        |
        +-- Calculate global and endpoint p95
        +-- Calculate errors and throughput
        +-- Enforce performance gates
        +-- Write summary.json
        +-- Exit 0 for PASS or 1 for FAIL
```

### Why there is a Node runner around JMeter

JMeter is responsible for producing traffic and recording samples. However,
JMeter can finish with exit code zero even when HTTP samples or response
assertions fail. A zero JMeter process exit therefore does not prove the system
met its performance objectives.

`run-jmeter.mjs` standardizes the profile, URL parsing, output directories,
Java checks, and JMeter command. After the run, `assert-jmeter-results.mjs`
parses the JTL and converts the performance objectives into a real automation
gate. This is what makes the implementation suitable for CI/CD.

## 3. Prerequisites

Use the following versions for the most predictable demonstration:

- Java 17 JDK.
- Apache JMeter 5.6.3.
- Node.js and npm.
- A running local backend or deployed non-production gateway.

If the demonstration machine defaults to Java 25, point `JAVA_HOME` to Java 17:
the Groovy engine bundled with JMeter 5.6.3 is not compatible with Java 25. The
runner deliberately accepts Java 17–21 and rejects unsupported versions before
testing.

### PowerShell environment setup

Adjust these paths to match the installed directories:

```powershell
$env:JAVA_HOME = 'C:\Program Files\Eclipse Adoptium\jdk-17'
$env:JMETER_HOME = 'C:\tools\apache-jmeter-5.6.3'
$env:Path = "$env:JAVA_HOME\bin;$env:JMETER_HOME\bin;$env:Path"
```

These assignments affect only the current PowerShell window, which is useful for
a controlled demonstration. Verify the tools:

```powershell
java -version
jmeter --version
node --version
npm.cmd --version
```

If PowerShell blocks `npm.ps1`, use `npm.cmd` instead of `npm`.

### Validate the test plan without generating traffic

```powershell
npm.cmd run load:validate
```

This loads the JMX through the real JMeter engine with zero setup and workload
threads. It detects malformed JMX and missing JMeter components without calling
the backend.

## 4. Starting the backend

For a local test, follow the root README to configure `.env.local` and start
Docker Compose:

```powershell
docker compose --env-file .env.local up --build -d
docker compose --env-file .env.local ps
```

Verify the gateway and database-backed Catalog route:

```powershell
Invoke-RestMethod http://localhost:5000/health
Invoke-RestMethod http://localhost:5000/api/catalog/events
Invoke-RestMethod http://localhost:5000/api/catalog/categories
```

The event list must contain at least one valid upcoming event. Otherwise, set an
existing event ID explicitly. Booking availability for that event must return
HTTP 200.

```powershell
$env:EVENT_ID = '1'
```

If using a deployed environment, do not start local Compose. Set `BASE_URL` to
the non-production gateway URL.

## 5. Running every profile

Set the system under test:

```powershell
$env:BASE_URL = 'http://localhost:5000'
```

### Smoke profile

```powershell
npm.cmd run load:smoke
```

- 1 concurrent thread.
- 5-second ramp-up.
- 35-second duration.
- Maximum 2% errors.
- Global p95 must be at most 1200 ms.
- Purpose: confirm connectivity, data, assertions, reports, and gates.

Run this first on presentation day.

### Demo profile

```powershell
npm.cmd run load:demo
```

- 10 concurrent threads.
- 10-second ramp-up.
- 50-second duration.
- Maximum 1% errors.
- Global p95 must be at most 1000 ms.
- Purpose: short live demonstration within the assessment time limit.

### Expected-load profile

```powershell
npm.cmd run load:test
```

- 30 concurrent threads.
- 60-second ramp-up.
- 270-second duration.
- Maximum 1% errors.
- Global p95 must be at most 1000 ms.
- Purpose: evaluate sustained expected concurrency.

### Stress profile

```powershell
npm.cmd run load:stress
```

- 100 concurrent threads.
- 180-second ramp-up.
- 240-second duration.
- Maximum 3% errors.
- Global p95 must be at most 1800 ms.
- Purpose: observe degradation as concurrency approaches a stressful level.

Never run the stress profile against production or a shared environment without
explicit authorization.

## 6. Optional authenticated login traffic

Use a dedicated non-production account:

```powershell
$env:LOAD_TEST_EMAIL = 'load-test@example.com'
$env:LOAD_TEST_PASSWORD = '<test-password>'
npm.cmd run load:demo
```

Both variables must be present. The runner passes only an `authEnabled` Boolean
to JMeter. The JMX reads credentials from the process environment and constructs
JSON using a Groovy preprocessor. Passwords are not committed or embedded in the
JMX. If either variable is absent, the Auth controller is skipped.

After the demonstration, clear session variables:

```powershell
Remove-Item Env:LOAD_TEST_EMAIL -ErrorAction SilentlyContinue
Remove-Item Env:LOAD_TEST_PASSWORD -ErrorAction SilentlyContinue
```

## 7. Opening and explaining the JMeter GUI

```powershell
npm.cmd run load:gui
```

The important components in the tree are:

### Test Plan

The root container. Functional mode is disabled because saving all response data
would consume memory and distort a load test.

### Setup Thread Group

Runs before the main group. It performs gateway preflight and data discovery once,
instead of making every simulated user discover the event.

### HTTP Request Defaults

Centralizes protocol, host, port, connection timeout, response timeout, and the
HttpClient4 implementation. This avoids repeating environment configuration on
every HTTP sampler.

### Header Manager

Applies `Accept: application/json`. The login sampler adds
`Content-Type: application/json`.

### JSON Extractor

Extracts `$[0].id` from the Catalog event array. The extracted JMeter variable is
thread-local, so a Groovy postprocessor copies it into JMeter `props`, which are
global and available to the later Main Thread Group.

### Main Thread Group

Represents concurrent users. Thread count, ramp-up, and duration are supplied as
`-J` properties by the runner. The loop repeats until the scheduled duration ends.

### Simple Controller

Groups the two public browsing requests for readability. It does not create an
extra transaction sample, so throughput calculations contain only real HTTP
requests.

### If Controllers

- Event controller executes only when discovery/configuration produced an event.
- Auth controller executes only when both login environment variables exist.

The external gate still fails if event-detail or Booking samples are missing,
because those endpoints are required scope rather than optional coverage.

### Response Assertions

Require HTTP status 200. An assertion failure changes the JTL `success` field to
false, allowing the error-rate gate to detect functionally incorrect responses.

### Think time

A Flow Control Action pauses each simulated user for a random 500–1500 ms after
each journey. Real people do not send journeys in a zero-delay loop. Think time
reduces unrealistic request intensity and makes the workload more human-like.

### Important GUI rule

Use the GUI to inspect, edit, and explain the plan. Use CLI mode for measured load.
GUI rendering and listeners consume CPU and memory, which can make the load
generator—not the backend—the bottleneck.

## 8. Results and reports

Every active run creates:

```text
tests/load/results/<UTC timestamp>-<profile>/
  results.jtl
  jmeter.log
  summary.json
  report/
    index.html
    content/
    sbadmin2-1.0.7/
```

Open the newest HTML dashboard from PowerShell:

```powershell
$run = Get-ChildItem tests/load/results -Directory |
  Sort-Object Name |
  Select-Object -Last 1

Invoke-Item (Join-Path $run.FullName 'report\index.html')
```

Inspect the machine-readable decision:

```powershell
Get-Content -Raw (Join-Path $run.FullName 'summary.json')
```

### Dashboard sections to present

1. **Test and Report information** — duration and report metadata.
2. **APDEX** — high-level satisfaction indicator.
3. **Requests Summary** — success versus failure distribution.
4. **Statistics** — count, error percentage, throughput, average, median, and
   percentiles per sampler.
5. **Response Times Over Time** — whether latency increases as the run proceeds.
6. **Active Threads Over Time** — proof that concurrency ramped as designed.
7. **Transactions/Hits per Second** — achieved throughput.
8. **Response Time Percentiles** — tail latency distribution.
9. **Errors and Top Errors** — failure type and affected sampler.

The HTML dashboard can include the two setup requests. `summary.json` excludes
sampler labels beginning with `SETUP ` so the automation decision represents only
the main workload.

## 9. Performance gates

The runner evaluates these endpoint p95 limits for every profile:

| Endpoint | Maximum p95 |
| --- | ---: |
| Catalog events | 800 ms |
| Catalog categories | 800 ms |
| Event details | 800 ms |
| Booking availability | 1200 ms |
| Optional login | 1500 ms |

The gate also verifies:

- Profile-specific global p95.
- Profile-specific error percentage.
- At least one sample exists for every required endpoint.
- JMeter did not log an engine, Groovy, or uncaught execution error.

A test passes only when every gate passes. A missing endpoint is a failure because
reporting excellent latency for only half the required scope would be misleading.

## 10. Understanding the core metrics

### Concurrency or threads

A JMeter thread represents one concurrent simulated user executing the journey.
Ten threads do not mean ten requests per second. Each user sends multiple requests,
waits for responses, and pauses for think time.

### Ramp-up

Ramp-up is how long JMeter takes to start all threads. Approximate thread-start
interval is:

```text
ramp-up seconds / number of threads
```

For the load profile, 30 threads over 60 seconds starts approximately one new
thread every two seconds. Gradual ramp-up reveals the concurrency level where
latency or errors begin rising and avoids an unrealistic instant spike.

### Throughput

Throughput is completed requests per unit of time. This suite reports requests
per second. In a closed workload like a standard JMeter Thread Group, throughput
depends on concurrency, backend response time, number of requests per journey,
and think time.

An approximate relationship is:

```text
throughput ≈ concurrent users × requests per journey
             / (journey response time + think time)
```

If response time rises while concurrency is fixed, throughput may flatten or fall.

### Average response time

The arithmetic mean. Easy to understand but sensitive to extreme values and able
to hide the experience of slower users.

### Median or p50

Half the samples completed at or below this time. It represents the typical
request but not the slow tail.

### p90, p95, and p99

For p95, 95% of requests completed at or below the reported time and 5% were
slower. Percentiles are preferred for service objectives because they expose tail
latency hidden by an average.

### Error rate

```text
failed main-workload samples / all main-workload samples
```

Failures include connection/time-out errors, non-200 responses caught by response
assertions, and other unsuccessful JMeter samples.

### Elapsed time, latency, and connect time

- **Elapsed**: total time from starting the request until the response completes.
- **Latency**: time until the first response bytes are received.
- **Connect time**: time required to establish the network connection.

High connect time suggests network/TLS/connection pressure. Low connect time but
high latency can indicate server queueing or application/database processing.

### APDEX

APDEX converts response times into a satisfaction score between 0 and 1:

- Satisfied request: full weight 1.
- Tolerating request: half weight 0.5.
- Frustrated request: weight 0.

The workflow configures 800 ms as satisfied and 1500 ms as tolerated for the
dashboard. APDEX is a summary, not a replacement for per-endpoint percentiles and
error analysis.

## 11. Load-testing theory

### Load testing

Checks whether the system meets performance expectations at anticipated traffic.
The 30-thread profile is this suite's main load test.

### Stress testing

Pushes beyond normal expectations to locate degradation, saturation, or failure
behavior. A proper stress investigation increases pressure until a breaking point
is observed; this suite provides a bounded 100-thread stress profile as a safe
starting point.

### Spike testing

Applies a sudden, sharp increase and often a sharp decrease in traffic. This suite
does not implement a dedicated spike profile because its profiles use gradual
ramp-up.

### Soak or endurance testing

Maintains load for hours to detect memory leaks, connection leaks, resource
exhaustion, and gradual degradation. The current suite is intentionally short for
CI and live demonstration; it is not an endurance test.

### Volume testing

Evaluates behavior with a large amount of stored data. Thread count alone does not
perform volume testing. That requires a deliberately large database dataset.

### Closed versus open workload models

The standard JMeter Thread Group is a closed model: a fixed population repeatedly
completes journeys, and each user waits for responses before continuing. An open
model controls arrival rate independently of response time. Closed modeling is
appropriate for interactive users, but it can reduce generated arrival rate when
the backend slows. State this limitation confidently if asked.

### Performance testing versus functional testing

Functional tests ask whether behavior is correct for specific cases. Performance
tests ask whether correct behavior remains timely and reliable under concurrency.
This plan includes response assertions because performance without correctness is
meaningless, but it is not a replacement for detailed unit or integration tests.

## 12. CI/CD automation

Workflow: `.github/workflows/backend-load-tests.yml`

### Pull requests and pushes

Changes to load-test assets trigger the validation job. It installs Java 17,
downloads Apache JMeter 5.6.3, verifies its SHA-512 checksum, and loads the JMX
with zero traffic. This is fast and prevents accidental load against an environment
for every commit.

### Manual execution

From GitHub Actions, choose **Backend Load Testing** → **Run workflow**, then select
the profile and optionally provide a base URL/event ID.

If `base_url` is blank, the workflow uses repository variable:

```text
LOAD_TEST_BASE_URL
```

Optional repository secrets:

```text
LOAD_TEST_EMAIL
LOAD_TEST_PASSWORD
```

### Scheduled execution

The workflow schedules the normal `load` profile weekly at Tuesday 00:00 in
Asia/Colombo (Monday 18:30 UTC). Scheduled execution requires
`LOAD_TEST_BASE_URL` to be configured.

### Why active load does not run on every pull request

- It could overload a shared environment.
- Results would be noisy when multiple branches run concurrently.
- The environment may not exist for forked pull requests.
- Performance runs consume more time and infrastructure than syntax validation.
- An explicit/scheduled target makes authorization and interpretation clearer.

## 13. Two-minute individual demonstration script

### 0:00–0:20 — Introduce scope

> My individual contribution is automated load and performance testing using
> Apache JMeter. The workload enters through the API Gateway and exercises Catalog
> browsing, event details, and Booking availability, with optional Auth login.

### 0:20–0:40 — Show the plan

Open the GUI/tree and say:

> The setup thread checks gateway health and discovers a real event ID. The main
> thread group represents concurrent users. Every HTTP sampler has a 200 response
> assertion, and each journey ends with randomized think time.

### 0:40–1:00 — Run automation

```powershell
$env:BASE_URL = 'http://localhost:5000'
npm.cmd run load:demo
```

Say:

> This profile ramps to ten concurrent threads over ten seconds and runs for fifty
> seconds. CLI mode prevents the JMeter GUI from consuming load-generator resources.

### 1:00–1:35 — Explain results

Open `report/index.html` and show active threads, statistics, p95, throughput, and
errors:

> I focus on p95 rather than only the average because it exposes slow-tail users.
> Response assertions ensure a fast error cannot be counted as success. The HTML
> report provides analysis, while the JSON gate makes the same objectives enforceable
> in CI.

### 1:35–1:55 — Show automation depth

Open `summary.json` or the workflow:

> The run fails when the global or endpoint p95 exceeds its limit, the error rate
> breaches the profile objective, a required endpoint receives no samples, or the
> JMeter engine logs a script error. GitHub Actions also uploads the complete report.

### 1:55–2:00 — Finish

> This gives the team repeatable load execution, objective pass/fail criteria, and
> evidence for identifying which microservice endpoint degrades under concurrency.

## 14. Likely viva questions and strong answers

### Why did you choose JMeter?

JMeter supports HTTP load generation, configurable thread groups, assertions,
data extraction, CLI automation, and a native HTML dashboard. Its GUI helps explain
the plan during the viva, while CLI mode integrates with CI. The tradeoff is that
JMX is XML and less readable in code review than a JavaScript-based tool.

### Why not execute the test in the GUI?

The GUI is intended for authoring and debugging. Rendering listeners consumes CPU
and memory and can limit the load generator, contaminating measurements. I execute
with `-n` CLI mode and analyze the post-run HTML dashboard.

### What does one thread represent?

One concurrent simulated user repeatedly executing the journey. It is not one
request and not one request per second.

### What is ramp-up?

The period over which JMeter starts all threads. It prevents an instant wall of
traffic and lets us observe how behavior changes as concurrency increases.

### Why use p95 rather than only average?

An average can look acceptable while a meaningful minority experiences very slow
responses. p95 establishes a bound for 95% of requests and exposes tail latency.

### What is the difference between latency and throughput?

Latency is the time taken by an individual request. Throughput is the number of
completed requests per unit time. A system can have high throughput but poor tail
latency, so both must be evaluated.

### Why are assertions included in a load test?

Without assertions, fast HTTP 500 responses could look like excellent performance.
Assertions confirm the system remains functionally correct under concurrency and
mark invalid samples as failures.

### How does event discovery work?

The Setup Thread Group requests the event array. A JSON Extractor reads `$[0].id`.
Because JMeter variables are local to a thread, a Groovy postprocessor copies the
value into global JMeter properties. The main group uses that property in event and
Booking paths.

### What happens if there are no events?

Event/Booking controllers do not execute, but the external gate fails because
those required endpoint labels have zero samples. This prevents a partial workload
from being reported as a valid pass. I can seed data or set `EVENT_ID` explicitly.

### How are thresholds automated?

After JMeter writes the JTL, the result-gate script parses successful/failed
samples, calculates global and per-label nearest-rank p95 values, compares them
against profile limits, writes `summary.json`, and exits nonzero on any violation.

### Why is a separate gate necessary?

JMeter may exit zero even when samplers or response assertions fail. CI requires a
process-level pass/fail signal, so the external gate converts JTL evidence into a
reliable build status.

### How is p95 calculated?

The script sorts response times and chooses the nearest-rank item at
`ceil(0.95 × sample count)`. That value means 95% of observed samples are at or
below it.

### How is the error rate calculated?

Unsuccessful main-workload JTL samples divided by all main-workload samples. Setup
samples are excluded from the automation decision.

### Why are different thresholds used for stress?

Expected-load objectives should be strict. Stress deliberately approaches abnormal
pressure, so modest degradation is expected. The stress thresholds remain bounded
so severe failure is still detected.

### Is 100 threads the breaking point?

No. It is a bounded stress profile, not a proven breaking point. Finding the true
limit requires controlled step increases, backend monitoring, repeated runs, and
stopping criteria.

### Why add think time?

Interactive users pause between journeys. Without think time, each thread loops as
fast as the backend responds, producing a less realistic workload and coupling
traffic intensity entirely to response speed.

### How do you identify the bottleneck?

First compare per-endpoint p95 and error curves as threads increase. Then correlate
the slow endpoint with server-side CPU, memory, thread-pool, database connection,
query, and downstream-service telemetry. JMeter identifies symptoms; it does not
alone prove the internal root cause.

### Why load through the gateway instead of calling services directly?

The gateway is the actual frontend entry point, so this includes routing and
proxy overhead and exercises realistic service paths. Direct service tests could
be added later to isolate gateway overhead from service processing.

### Why are write operations excluded?

Read-only tests are repeatable and safer for demos/CI. Booking writes require
careful test-data creation and cleanup and can corrupt inventory if repeatedly
executed. The limitation is that this suite does not measure write contention,
transaction locks, Kafka publishing, or complete booking workflows.

### Is the local validation result a real backend benchmark?

No. The controlled validation server proves the JMX, runner, extraction, reports,
and gates work. Only a run against the actual Exvo environment provides meaningful
backend performance evidence.

### How are credentials protected?

Credentials come from environment variables or GitHub secrets. They are not stored
in the repository. The JMX constructs the login body at runtime, and the standard
JTL configuration does not save request bodies.

### What causes unreliable performance results?

Cold starts, shared-environment traffic, changing data, network variation, debug
logging, insufficient load-generator resources, GUI execution, and too few samples.
Reliable conclusions require a controlled environment, warm-up strategy, repeated
runs, consistent datasets, and server telemetry.

### Can one passing run prove scalability?

No. It proves only that this workload met these objectives in that environment at
that time. Scalability requires multiple load levels, repeatability, resource
correlation, and evidence that capacity grows acceptably.

## 15. Honest limitations and future improvements

State these openly if asked; identifying limitations demonstrates mastery.

1. The workload is read-heavy and does not measure booking/check-in write contention.
2. A single event is reused, which can favor application/database caches.
3. The standard Thread Group is a closed user model, not a constant arrival-rate model.
4. Tests run from one load generator; very high loads may require distributed JMeter.
5. No dedicated spike or hours-long soak profile is implemented.
6. JMeter results show client-visible symptoms but require backend telemetry for
   root-cause diagnosis.
7. Fixed thresholds are initial service objectives and should be refined using a
   validated baseline and stakeholder expectations.
8. Scheduled tests against shared infrastructure may be affected by unrelated load.

Strong future extensions would include:

- A seeded dataset containing many events and randomized event selection.
- Authenticated booking creation with isolated inventory and cleanup.
- A check-in workflow with unique tickets.
- Prometheus/Grafana or Azure Monitor correlation for CPU, memory, DB pools, and
  service-level traces.
- Baseline comparison against the previous successful run.
- Dedicated spike and soak profiles.
- Distributed load generators for higher traffic.

## 16. Troubleshooting

### Java version rejected

Symptom:

```text
This JMeter plan requires Java 17-21
```

Fix `JAVA_HOME` in the current terminal and verify `java -version` reports 17.

### JMeter cannot be started

Set `JMETER_HOME` to the extracted JMeter directory, not its `bin` directory:

```powershell
$env:JMETER_HOME = 'C:\tools\apache-jmeter-5.6.3'
```

### Gateway preflight fails

Check `BASE_URL`, containers, gateway port, and `/health`:

```powershell
Invoke-RestMethod "$env:BASE_URL/health"
docker compose --env-file .env.local ps
docker compose --env-file .env.local logs --tail 100 apigateway
```

### Required endpoint has no samples

Catalog discovery found no usable event. Inspect the events response or set a valid
ID:

```powershell
Invoke-RestMethod "$env:BASE_URL/api/catalog/events"
$env:EVENT_ID = '1'
```

### Booking availability returns non-200

Verify the selected event exists in Catalog and that BookingService can reach its
database and CatalogService. Inspect Booking logs.

### HTML report was not generated

Inspect the timestamped `jmeter.log`. The report directory must be new/empty; the
runner solves this by creating a unique UTC timestamp directory for every run.

### Performance gate fails although JMeter says `... end of run`

This is expected behavior when the engine ran successfully but the application
failed an SLO/assertion. Read `summary.json` → `violations` and inspect the affected
endpoint in the HTML report.

### Results vary between runs

Repeat the same profile after warm-up, confirm the dataset/environment is unchanged,
check for competing traffic, and correlate server resources. Do not select only
the best run.

## 17. Presentation-day checklist

- [ ] Checkout `test/load-testing-jmeter`.
- [ ] Confirm Java 17 with `java -version`.
- [ ] Confirm JMeter 5.6.3 with `jmeter --version`.
- [ ] Run `npm.cmd run load:validate`.
- [ ] Start/check the backend or verify the deployed URL.
- [ ] Confirm at least one upcoming event exists.
- [ ] Run `npm.cmd run load:smoke`.
- [ ] Open the smoke HTML report before the assessment as a fallback.
- [ ] Open the JMX GUI tree for explanation.
- [ ] Keep the terminal at the repository root.
- [ ] Set `BASE_URL` and optional `EVENT_ID` in advance.
- [ ] Use `npm.cmd run load:demo` for the live run.
- [ ] Show p95, throughput, errors, active threads, and `summary.json`.
- [ ] Never expose passwords while sharing the screen.
- [ ] Be ready to explain limitations rather than claiming unrealistic coverage.

## 18. Thirty-second revision sheet

- Tool: Apache JMeter 5.6.3, Java 17, CLI execution.
- Model: closed workload using concurrent threads.
- Required path: Gateway → Catalog + Booking; Auth optional.
- Setup: health check + JSON event-ID discovery.
- Correctness: HTTP 200 assertions.
- Realism: gradual ramp-up + randomized think time.
- Evidence: JTL + native HTML dashboard + JSON summary.
- Primary metric: p95 tail response time.
- Other metrics: error rate, throughput, active threads, APDEX.
- Automation insight: JMeter exit zero is insufficient; external JTL gate controls CI.
- Demo: 10 threads, 10-second ramp, 50-second duration.
- Main limitation: read-only, single-event, single-generator closed model.

## 19. How this earns marks against the assessment rubric

| Rubric area | Evidence to show | Point to explain |
| --- | --- | --- |
| Test Automation & Pipeline | npm commands and `.github/workflows/backend-load-tests.yml` | The same version-controlled JMX is validated and executed locally and in CI; reports are uploaded as artifacts. |
| Code Quality & Standards | `tests/load/` structure, named samplers, environment configuration, and no committed credentials | Responsibilities are separated between workload generation, orchestration, and result gating. |
| Implementation Depth | Four profiles, setup discovery, optional auth, assertions, percentile gates, and reporting | This is a data-aware microservice journey, not one hard-coded request or one fixed thread count. |
| Live Demo Reliability | `load:validate`, `load:smoke`, `load:demo`, unique result directories, and preflight checks | There is a short presentation profile and a prepared fallback report, while failures remain visible. |
| Walkthrough & Clarity | HTML dashboard plus `summary.json` | Explain threads, ramp-up, p95, throughput, errors, endpoint gates, and the final pass/fail result. |
| VIVA & Concept Mastery | Sections 10–15 of this guide | Defend the closed workload model, percentile-based objectives, limitations, and next improvements without overclaiming. |

Do not merely list features. For each feature, connect the implementation to its
testing purpose. For example: “The JSON Extractor removes a hard-coded event ID,
so the Booking request follows data returned by Catalog and is less brittle.”

## 20. Official JMeter references

- [Getting Started](https://jmeter.apache.org/usermanual/get-started.html) — CLI
  execution, test-plan running, and general setup.
- [Component Reference](https://jmeter.apache.org/usermanual/component_reference.html)
  — Thread Groups, HTTP samplers, assertions, timers, and extractors.
- [Dashboard Report](https://jmeter.apache.org/usermanual/generating-dashboard.html)
  — HTML report generation, graphs, statistics, and APDEX configuration.
- [Best Practices](https://jmeter.apache.org/usermanual/best-practices.html) —
  non-GUI execution and guidance for realistic, efficient load tests.
