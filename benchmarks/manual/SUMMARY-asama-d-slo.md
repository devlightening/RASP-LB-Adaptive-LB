# Aşama D — SLO / deadline / goodput doğrulaması (2026-09-18)

## Eklenen bileşenler

- `Gateway/RaspLb.Gateway/Slo/SloOptions.cs` — `Enabled` (varsayılan true), `DeadlineMs` (varsayılan 500).
- `Gateway/RaspLb.Gateway/Slo/SloMetrics.cs` — thread-safe sayaçlar: `TotalRequests`, `Successes`, `DeadlineExceeded`, `Goodput`, `GoodputRate`.
- `Gateway/RaspLb.Gateway/Slo/SloMiddleware.cs` — proxy pipeline'ının **en dışında** çalışır (Aşama C'deki `RaspRetryMiddleware`'den önce), böylece ölçtüğü süre mantıksal isteğin retry denemeleri dahil uçtan uca süresidir, tek bir backend denemesinin değil.
- `Program.cs`: DI kaydı + `AddOptions<SloOptions>().ValidateOnStart()` + `/debug/slo` endpoint'i.
- `appsettings.json`: `"Slo": { "Enabled": true, "DeadlineMs": 500 }`.

**Goodput tanımı:** bir mantıksal istek hem 2xx döndürdü HEM DE deadline (500ms) içinde tamamlandıysa "goodput" sayılır. Sadece 2xx olması yetmez.

## Doğrulama 1 — sağlıklı yük (concurrency=20)

300 ölçülen + 50 warm-up istek, tüm istekler p95=146ms (500ms sınırının çok altında):

```
totalRequests=350, successes=350, deadlineExceeded=0, goodput=350, goodputRate=1.0
```

Beklenen: sağlıklı durumda goodput == throughput. Doğrulandı.

## Doğrulama 2 — aşırı yük (concurrency=50, plan §D doğrulama adımı)

Aynı senaryo, concurrency 20'den 50'ye çıkarıldı (`BENCHMARK_CONCURRENCY=50`). Kapasite kapıları (backend1 cap=2, backend2/3 cap=8) yetersiz kalıp kuyruklanma arttı (max kuyruk 312ms, ortalama ~117ms):

```
totalRequests=350, successes=350, deadlineExceeded=13, goodput=337, goodputRate=0.963
```

**Bu, Aşama D'nin amacını doğruluyor:** Benchmark'ın kendi "Success Rate" metriği hâlâ %100 gösteriyor (300/300 "OK") — çünkü backend hiçbir zaman hata döndürmedi, sadece geç cevap verdi. Ama goodput bunu yakaladı: 13 istek (~%3.7) kullanıcıya zamanında ulaşmadı. Yani **goodput, ham throughput/success-rate'in gizlediği bir kalite bozulmasını ortaya çıkarıyor** — tam olarak vizyon belgesinin §3'te tanımladığı metrik.

## Sınırlamalar / sonraki adımlar

- Deadline şu an sabit (appsettings, tüm istekler için aynı); istek bazlı/header ile deadline (vizyon §D'nin bahsettiği) henüz yok.
- Deadline aşımı istemciye iletilmiyor — istek normal şekilde tamamlanıyor, sadece geriye dönük ölçülüyor. Gerçek "deadline-aware" davranış (deadline aşılınca isteği erken kesmek/reddetmek) Aşama F'nin (load shedding) kapsamına daha yakın; bu ilk sürüm yalnızca gözlemsel (observability).
- Retry'ın SLO üzerindeki etkisi (retry denemesi toplam süreyi uzatıp deadline'ı aşırabilir) ayrı ölçülmedi — ileride retry açık/kapalı karşılaştırmasında goodput'a bakmak faydalı olur.
