# Aşama E — Brownout doğrulaması (2026-09-18)

## Eklenen bileşenler

- `backend/RaspLb.Backend/Brownout/BrownoutState.cs` — hysteresis'li mod durumu (Full/Reduced), thread-safe (`lock`), backend başına DI singleton olarak kayıtlı.
- `WorkController.cs`: her istekte **zorunlu çekirdek iş** (`Task.Delay(latencyMs)`, hiç atlanmaz) sonrası, backend'in kendi doluluk oranına (`activeRequestsAtStart / MaxConcurrency`) bakarak **opsiyonel "zenginleştirme" işini** (`Task.Delay(EnrichmentLatencyMs)`, varsayılan 30ms) atlayıp atlamayacağına karar veriyor. Yanıt gövdesinde `mode` (`Full`/`Reduced`) ve `enrichmentLatencyMs` görünüyor.
- `Program.cs` (backend): `BROWNOUT_UTILIZATION_THRESHOLD` (varsayılan 0.8) ve `BROWNOUT_MIN_DWELL_MS` (varsayılan 2000) env değişkenleri, `/debug/brownout` endpoint'i.

**Tasarım kararı:** Vizyon belgesi brownout'u gateway tarafında bir sinyal/karar mekanizması olarak öneriyordu; ben bunu backend'in **kendi** doluluk bilgisine dayanan, bağımsız bir karar olarak uyguladım (aşamalı geliştirme ilkesi — önce en basit çalışan hali, gateway-sinyalli versiyon sonraki bir iyileştirme olarak bırakıldı).

## Hysteresis formülü

- Trip (Full→Reduced): `utilization >= 0.8` VE son mod değişiminden beri `>= 2000ms` geçmiş.
- Recovery (Reduced→Full): `utilization < 0.6` (recovery band = threshold - 0.2) VE son mod değişiminden beri `>= 2000ms` geçmiş.

## Kontrollü doğrulama (backend1, cap=2)

1. **Tek istek (utilization=0.5):** `mode=Full`, `enrichmentLatencyMs=30`, `elapsedMs=73` — beklenen.
2. **2 eşzamanlı istek (2. istek utilization=1.0):** 2. istek `mode=Reduced`, `enrichmentLatencyMs=0`, `elapsedMs=41` (sadece çekirdek gecikme); `activations=1`. Diğeri hâlâ `Full`.
3. **Aynı burst'ten 0.1sn sonra, tek düşük-utilization istek:** hâlâ `mode=Reduced` (`enrichmentLatencyMs=0`) — **dwell süresi dolmadığı için mod değişmedi**, anlık düşük yükte bile hemen Full'a dönmedi. Hysteresis çalışıyor.
4. **2.2sn daha bekleyip tek istek:** `mode=Full` (`enrichmentLatencyMs=30`) — dwell süresi dolduğunda ve utilization düşükken doğru şekilde geri döndü.

Tüm adımlar `activations` sayacıyla tutarlı (tek bir gerçek aktivasyon, salınım yok).

## Sınırlamalar / sonraki adımlar

- Şu an backend'ler birbirinden bağımsız karar veriyor (her biri kendi doluluğuna bakıyor); gateway'in genel sistem yükünü görerek merkezi bir sinyal vermesi ayrı bir iyileştirme.
- Dashboard'a henüz `/debug/brownout` entegre edilmedi (backend-taraflı endpoint, gateway'in topladığı metriklerin dışında) — istenirse eklenebilir.
- "Zenginleştirme" tamamen sentetik (`Task.Delay`); gerçek bir opsiyonel özelliğin (ör. öneri motoru çağrısı) yerini tutuyor.
