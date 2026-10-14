# RASP-LB — Adaptive Load Balancer on YARP

<p>
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white">
  <img alt="YARP" src="https://img.shields.io/badge/YARP-2.3-0078D4">
  <img alt="Docker" src="https://img.shields.io/badge/Docker-Compose-2496ED?logo=docker&logoColor=white">
  <img alt="Status" src="https://img.shields.io/badge/status-research%20prototype-orange">
</p>

An experimental load balancer built on **YARP** that asks one question:

> Given the current load and reliability of each backend, which one should receive the next request?

Round-robin sends the same number of requests everywhere. Real backends differ in latency, concurrent capacity, and error rate, and a backend that is fast when idle can become the worst choice once its slots are full. RASP-LB explores routing policies that react to this, and measures whether they actually help.

> **Honest scope:** this is a measured research prototype, not a production load balancer. Results below come from a local Docker setup with simulated backends. The repository documents what was verified in code and what is still a hypothesis.

## How it works

```text
 .NET benchmark ──►  YARP gateway  ──►  backend-1  (40 ms,  2 slots)
 localhost:5200      load-balancing     backend-2  (70 ms,  8 slots)
                     policy + metrics   backend-3  (120 ms, 8 slots)
                     + bounded retry
        completed response ──► per-destination EWMA ──► next routing decision
```

| Policy | Idea |
|---|---|
| **RaspV0** | Warm-up round-robin, then pick the lowest latency EWMA, with ~10 % random exploration |
| **RaspV1** | V0 plus an error-rate penalty: `latency × (1 + 4 × errorRate)` |
| **RaspV2** | V1 plus load awareness: `predicted = latency × (1 + inFlight / capacity)` so a nearly full backend is avoided *before* a queue builds |

The gateway also ships a **bounded retry layer** that sits in front of load balancing:

- only `GET`/`HEAD`, and only forwarder failures that happen before a response starts
- at most 2 attempts, the failed destination is excluded from the retry
- a time budget (default 5 s) and a global token bucket (default 20 tokens/s) cap retry-driven extra load
- unsafe methods such as `POST` are never retried

`/debug/metrics` and `/debug/retries` expose the EWMA values, result classes (2xx, 4xx, 5xx, forwarder error, timeout, cancellation) and retry counters.

## Results

Scenario: 300 requests, concurrency 20, three heterogeneous backends, `ERROR_RATE=0`. Five runs per policy; the first run after each restart is excluded because of JIT and connection warm-up (this effect is documented in the benchmark notes). Mean of runs 2–5:

| Metric | RaspV0 | RaspV1 | RaspV2 |
|---|---:|---:|---:|
| P50 latency | 148.4 ms | 164.5 ms | **101.7 ms** |
| P95 latency | 310.9 ms | 345.4 ms | **195.0 ms** |
| Throughput | 119.8 req/s | 103.3 req/s | **175.5 req/s** |

In this scenario RaspV2's worst run (p95 239 ms) is still better than the best run of V0 and V1, so the difference is larger than the run-to-run variance. **This does not generalize**: it is one heterogeneous-capacity scenario with no injected errors. Homogeneous capacity, higher concurrency and `ERROR_RATE > 0` are listed as open questions.

The retry layer was validated separately with a closed destination: 30 `GET` requests returned 30×`200` with retry enabled, `POST` was never retried, and a 1 s time budget correctly suppressed retries. Raw outputs are in [`benchmarks/manual`](benchmarks/manual).

## Run it

Requires Docker and the .NET 10 SDK.

```bash
docker compose up --build        # gateway on :5200, backends on :5101-5103
dotnet run --project Benchmark/RaspLb.Benchmark
```

The active policy is set in `Gateway/RaspLb.Gateway/appsettings.json` (`LoadBalancingPolicy`). The benchmark target, request count, warm-up and concurrency are environment variables.

## Repository layout

```text
Gateway/RaspLb.Gateway/      YARP gateway: policies, metrics store, retry middleware
backend/RaspLb.Backend/      simulated backend: configurable latency, capacity gate, error rate
Benchmark/RaspLb.Benchmark/  load generator with warm-up phase, p50/p95/p99 report
benchmarks/manual/           raw runs and written summaries (stages A, B, C)
docs/                        project vision and step-by-step implementation plan
```

## Known limits

- There is no automated test project yet; evidence is the recorded benchmark output.
- Backends are simulated (`Task.Delay` and a semaphore), so results show routing behavior, not real service performance.
- Gateway latency includes queue time, so V2 may penalize queueing twice. The data does not show a regression here, but the question is kept open in [`docs/RASP_LB_PROJECT_VISION.md`](docs/RASP_LB_PROJECT_VISION.md).
- Planned: SLO-aware selection, brownout and load shedding, comparison against NGINX.

## Author

**Halil İbrahim Yıldırım** — .NET backend developer · [GitHub](https://github.com/devlightening) · [LinkedIn](https://www.linkedin.com/in/0001myprofile)
