# Run 03 — RaspV1

- Tarih: 2026-09-18
- Policy: RaspV1 (appsettings.json değiştirildi, gateway+backend'ler yeniden build edildi ve recreate edildi)
- Diğer her şey Run 01/02 ile aynı: 300 istek, concurrency=20, backend1=40ms/cap2, backend2=70ms/cap8, backend3=120ms/cap8, ERROR_RATE=0

## Sonuçlar

```
Total Requests : 300
Successful     : 300
Failed         : 0
Success Rate   : 100.00%

Average Latency: 180.28 ms
P50 Latency    : 156.19 ms
P95 Latency    : 390.93 ms
P99 Latency    : 436.40 ms

Total Duration : 2.84 sec
Throughput     : 105.72 req/sec

BACKEND DISTRIBUTION
Backend-1    Total:   77 | Success:   77 | Failed:    0
Backend-2    Total:  153 | Success:  153 | Failed:    0
Backend-3    Total:   70 | Success:   70 | Failed:    0

QUEUE METRICS
Queued Requests : 218
Queue Rate      : 72.67%
Average Queue   : 83.13 ms
P50 Queue       : 64.00 ms
P95 Queue       : 307.00 ms
P99 Queue       : 359.00 ms
Max Queue       : 359.00 ms

QUEUE BY BACKEND
Backend-1    AvgQueue:  182.75 ms | P95Queue:  359.00 ms | MaxQueue:  359.00 ms | Queued:   66/  77
Backend-2    AvgQueue:   45.82 ms | P95Queue:  106.00 ms | MaxQueue:  125.00 ms | Queued:  109/ 153
Backend-3    AvgQueue:   55.11 ms | P95Queue:  161.00 ms | MaxQueue:  196.00 ms | Queued:   43/  70

LOAD BY BACKEND
Backend-1    Capacity:   2 | AvgActive:   1.90 | MaxActive:   2
Backend-2    Capacity:   8 | AvgActive:   6.78 | MaxActive:   8
Backend-3    Capacity:   8 | AvgActive:   6.13 | MaxActive:   8

STATUS CODES
OK                  : 300
```

## /debug/metrics (koşu sonrası)

```json
[
  {"destination":"backend3","samples":70,"ewmaLatencyMs":166.57,"failures":0,"failureRate":0},
  {"destination":"backend1","samples":78,"ewmaLatencyMs":218.97,"failures":0,"failureRate":0},
  {"destination":"backend2","samples":153,"ewmaLatencyMs":167.84,"failures":0,"failureRate":0}
]
```

## Gözlem

`ERROR_RATE=0` olduğu için v1'in hata cezası hiç devreye girmiyor — beklendiği gibi v1 ≈ v0 (p95: 391ms vs 401ms, throughput: 106 vs 101 req/s, aradaki fark gürültü seviyesinde). v1'in v0'dan farkı yalnızca hata-farkındalığı; bu senaryoda hata olmadığı için ayrışma göstermiyor.
