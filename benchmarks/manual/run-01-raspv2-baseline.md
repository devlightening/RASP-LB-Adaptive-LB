# Run 01 — RaspV2 baseline

- Tarih: 2026-09-18
- Commit: 21775f4 (HEAD, docs eklendi ama kod değişmedi)
- Policy: RaspV2 (appsettings.json — değiştirilmedi, varsayılan)
- Ortam: docker compose up --build -d, gateway restart edildi (temiz EWMA state), 1 readiness-probe isteği sonrası benchmark koşuldu
- Benchmark parametreleri: 300 istek, concurrency=20, timeout=10s (Benchmark/RaspLb.Benchmark/Program.cs varsayılanları)
- Backend ayarları (docker-compose.yml): backend1=40ms/cap2, backend2=70ms/cap8, backend3=120ms/cap8, ERROR_RATE=0 (üçünde de)

## Sonuçlar

```
Total Requests : 300
Successful     : 300
Failed         : 0
Success Rate   : 100.00%

Average Latency: 113.82 ms
P50 Latency    : 101.05 ms
P95 Latency    : 248.72 ms
P99 Latency    : 329.05 ms

Total Duration : 1.80 sec
Throughput     : 166.39 req/sec

BACKEND DISTRIBUTION
Backend-1    Total:   68 | Success:   68 | Failed:    0
Backend-2    Total:  155 | Success:  155 | Failed:    0
Backend-3    Total:   77 | Success:   77 | Failed:    0

QUEUE METRICS
Queued Requests : 181
Queue Rate      : 60.33%
Average Queue   : 21.00 ms
P50 Queue       : 9.00 ms
P95 Queue       : 70.00 ms
P99 Queue       : 167.00 ms
Max Queue       : 180.00 ms

QUEUE BY BACKEND
Backend-1    AvgQueue:   49.94 ms | P95Queue:  167.00 ms | MaxQueue:  180.00 ms | Queued:   61/  68
Backend-2    AvgQueue:   16.62 ms | P95Queue:   53.00 ms | MaxQueue:   64.00 ms | Queued:  113/ 155
Backend-3    AvgQueue:    4.27 ms | P95Queue:   33.00 ms | MaxQueue:  115.00 ms | Queued:    7/  77

LOAD BY BACKEND
Backend-1    Capacity:   2 | AvgActive:   1.96 | MaxActive:   2
Backend-2    Capacity:   8 | AvgActive:   7.32 | MaxActive:   8
Backend-3    Capacity:   8 | AvgActive:   5.56 | MaxActive:   8

STATUS CODES
OK                  : 300
```

## /debug/metrics (koşu sonrası anlık durum)

```json
[
  {"destination":"backend3","samples":77,"ewmaLatencyMs":121.64,"failures":0,"failureRate":0},
  {"destination":"backend1","samples":69,"ewmaLatencyMs":76.77,"failures":0,"failureRate":0},
  {"destination":"backend2","samples":155,"ewmaLatencyMs":92.79,"failures":0,"failureRate":0}
]
```

## İlk gözlem (henüz kanıtlanmış kök neden değil, sadece ham veri)

- Backend-1'in **yapılandırılmış** gecikmesi 40ms, ama **gözlenen EWMA'sı 76.77ms** — neredeyse 2 katı. Kapasitesi en düşük (2 slot) olduğu için isteklerin %90'ı (61/68) kuyrukta bekliyor; ortalama kuyruk süresi 49.94ms. Yani gateway'in EWMA'sı, backend'in `Task.Delay` süresini DEĞİL, kuyruk+delay toplamını ölçüyor gibi görünüyor.
- Backend-3'ün yapılandırılmış gecikmesi 120ms, EWMA'sı 121.64ms — neredeyse birebir örtüşüyor, çünkü kuyruklanma çok az (7/77).
- Bu, Aşama A'nın "çifte ceza" hipotezini destekleyen ilk veri: eğer latency EWMA zaten kuyruk süresini içeriyorsa, RaspV2'nin ayrıca `loadFactor = 1 + inFlight/capacity` ile çarpması aynı kuyruklanma etkisini iki kez saymış olabilir. Bunu doğrulamak için sıradaki adım RaspV0/V1 ile aynı senaryoyu koşup karşılaştırmak.
