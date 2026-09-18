# Run 02 — RaspV0

- Tarih: 2026-09-18
- Policy: RaspV0 (appsettings.json değiştirildi, gateway+backend'ler yeniden build edildi ve recreate edildi — temiz state)
- Diğer her şey Run 01 ile aynı: 300 istek, concurrency=20, backend1=40ms/cap2, backend2=70ms/cap8, backend3=120ms/cap8, ERROR_RATE=0

## Sonuçlar

```
Total Requests : 300
Successful     : 300
Failed         : 0
Success Rate   : 100.00%

Average Latency: 188.65 ms
P50 Latency    : 161.22 ms
P95 Latency    : 401.26 ms
P99 Latency    : 571.97 ms

Total Duration : 2.96 sec
Throughput     : 101.43 req/sec

BACKEND DISTRIBUTION
Backend-1    Total:   60 | Success:   60 | Failed:    0
Backend-2    Total:  172 | Success:  172 | Failed:    0
Backend-3    Total:   68 | Success:   68 | Failed:    0

QUEUE METRICS
Queued Requests : 229
Queue Rate      : 76.33%
Average Queue   : 84.58 ms
P50 Queue       : 78.00 ms
P95 Queue       : 263.00 ms
P99 Queue       : 358.00 ms
Max Queue       : 358.00 ms

QUEUE BY BACKEND
Backend-1    AvgQueue:  171.47 ms | P95Queue:  358.00 ms | MaxQueue:  358.00 ms | Queued:   53/  60
Backend-2    AvgQueue:   59.20 ms | P95Queue:  120.00 ms | MaxQueue:  125.00 ms | Queued:  135/ 172
Backend-3    AvgQueue:   72.12 ms | P95Queue:  234.00 ms | MaxQueue:  234.00 ms | Queued:   41/  68

LOAD BY BACKEND
Backend-1    Capacity:   2 | AvgActive:   1.63 | MaxActive:   2
Backend-2    Capacity:   8 | AvgActive:   7.23 | MaxActive:   8
Backend-3    Capacity:   8 | AvgActive:   5.62 | MaxActive:   8

STATUS CODES
OK                  : 300
```

## /debug/metrics (koşu sonrası)

```json
[
  {"destination":"backend3","samples":68,"ewmaLatencyMs":290.58,"failures":0,"failureRate":0},
  {"destination":"backend1","samples":61,"ewmaLatencyMs":307.22,"failures":0,"failureRate":0},
  {"destination":"backend2","samples":172,"ewmaLatencyMs":130.98,"failures":0,"failureRate":0}
]
```

## Gözlem

RaspV0 kapasiteyi hiç bilmiyor — sadece geçmiş EWMA latency'ye göre seçiyor. Backend-1'in başlangıç gecikmesi düşük göründüğü için önce ona yükleniyor, kapasitesi (2 slot) hemen doluyor, kuyruk EWMA'yı şişiriyor (307ms'e kadar), sonra policy fark edip başka backend'e kayıyor — ama bu reaksiyon gecikmeli. Backend-1 ve Backend-3'ün EWMA'sı (~290-307ms) Backend-2'nin (~131ms) neredeyse 2-2.5 katı; bu **RaspV2'de görülmeyen bir dengesizlik**.

**Run 01 (RaspV2) ile karşılaştırma:** RaspV0'da p95=401ms, RaspV2'de p95=249ms. RaspV0'da throughput=101 req/s, RaspV2'de 166 req/s. **Yani bu senaryoda RaspV2, RaspV0'dan belirgin biçimde daha iyi performans gösteriyor** — kullanıcının aktardığı "v2 regresyonu" bu koşuda gözlenmedi. Kesin sonuç için RaspV1 ile de karşılaştırma yapılacak ve tekrar sayısı artırılacak (tek koşu varyansı yüksek olabilir).
