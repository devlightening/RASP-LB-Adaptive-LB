# RASP-LB — Adım Adım Uygulama Planı

> Bu belge `docs/RASP_LB_PROJECT_VISION.md`'deki hedefleri, somut dosya/kod değişikliklerine indirger. Claude bu plana göre kodu yazacak, her adımda ne yaptığını burada değil sohbette açıklayacak — bu dosya sadece referans/rehber niteliğindedir.

## Context

`docs/RASP_LB_PROJECT_VISION.md` projenin amacını, mevcut kanıtlanmış durumunu ve 7 aşamalı (A-G) bir yol haritasını tanımlıyor. Bu plan, vizyon belgesinin Bölüm 10'undaki (Aşama A-G) soyut hedeflerini somut dosya/kod değişikliklerine indirger. Her aşamada üç şey var: **(1) mevcut kod ne yapıyor**, **(2) ne eklenecek/değişecek**, **(3) nasıl doğrulanır**. Sıra önemli — vizyon belgesinin ilkesi "önce ölçüm, sonra değişiklik" ve "aynı anda tek soruna odaklan".

## Mevcut Kod Haritası (önce bunu anla)

| Dosya | Görevi |
|---|---|
| `Gateway/RaspLb.Gateway/Program.cs` | YARP pipeline kurulumu: 3 policy'yi DI'a kaydeder, konfigürasyondaki policy'yi çalıştırır. Ölçüm middleware'i 2xx/4xx/5xx ile `IForwarderErrorFeature` sonuçlarını sınıflandırır; sonra passive health middleware'i çalışır. `/debug/metrics` ayrıntılı sonuç sayaçlarını yayımlar. |
| `Gateway/RaspLb.Gateway/appsettings.json` | Aktif policy: `"LoadBalancingPolicy": "RaspV2"`. Her backend için `RaspCapacity` metadata'sı (`backend1=2`, `backend2=8`, `backend3=8`) — bu değerler `docker-compose.yml`'daki `MAX_CONCURRENCY` ile birebir eşleşiyor. |
| `Gateway/RaspLb.Gateway/LoadBalancing/DestinationMetrics.cs` | Tek backend için latency/error EWMA, kullanıcıya görünen hata ve destination kaynaklı hata sayaçları. 2xx, 4xx, 5xx, forwarder hatası, timeout, iptal ve beklenmeyen exception ayrılır. Tutarlı debug okuması için tek kilit altında snapshot üretir. |
| `Gateway/RaspLb.Gateway/LoadBalancing/DestinationMetricsStore.cs` | `ConcurrentDictionary<string, DestinationMetrics>`, anahtar = `DestinationId`. `Get`, `Record`, `GetAll`. |
| `Gateway/RaspLb.Gateway/LoadBalancing/RaspV0/V1/V2LoadBalancingPolicy.cs` | `PickDestination`: Faz 1 ısınma (5 örnek altı → dönüşümlü seçim) → Faz 2 keşif (%10 rastgele) → Faz 3 exploitation (skor en düşük olan kazanır). v0: sadece latency EWMA. v1: `latency × (1 + 4×errorRate)`. v2: v1 + `load_factor = 1 + inFlight/capacity`, `predicted = latency × load_factor`, skor `predicted × (1+4×errorRate)`. |
| `backend/RaspLb.Backend/Controllers/WorkController.cs` | `SemaphoreSlim CapacityGate(MaxConcurrency)` — kapasiteyi aşan istekler burada kuyrukta bekler (`queueWatch`). Sonra `Task.Delay(BASE_LATENCY_MS)` ile gecikme simülasyonu, `ERROR_RATE` olasılığıyla 503 döner. Yanıt gövdesinde `queueDelayMs`, `activeRequestsAtStart`, `elapsedMs` var. |
| `Benchmark/RaspLb.Benchmark/Program.cs` | Varsayılan 50 warm-up isteğini sonuçlardan ayırır; ardından 300 istek/concurrency=20 ile ölçer. Target, istek, warm-up ve concurrency ortam değişkenleriyle değiştirilebilir. p50/p95/p99, backend dağılımı, queue ve status code konsola yazılır; ham veriyi yapılandırılmış dosyaya yazma hâlâ yok. |
| `docker-compose.yml` | 3 backend heterojen: `40ms/2 slot`, `70ms/8 slot`, `120ms/8 slot`; `ERROR_RATE=0` üçünde de (hata farkındalığı şu an test edilmiyor). |

**Kritik gözlem (vision §8, hipotez #1):** Gateway'in ölçtüğü latency (`Program.cs`'teki stopwatch) muhtemelen backend'in kuyruk süresini (`queueDelayMs`) zaten içeriyor — çünkü stopwatch `await next()` boyunca proxy+backend'in tüm zamanını sarıyor. v2'nin ayrıca `load_factor = 1 + inFlight/capacity` ile çarpması, kuyruğu **iki kez** cezalandırıyor olabilir. Bu, Aşama A'nın merkezi araştırma sorusu.

---

## Aşama 0 — Ortamı ayağa kaldır, temiz bir baseline al

**Nerede:** repo kökü, PowerShell.

**Yapılacaklar:**
1. `docker compose up --build -d`
2. Sağlık kontrolü: `Invoke-RestMethod http://localhost:5101/health`, `...5102/health`, `...5103/health`
3. Gateway'i yeniden başlat (singleton EWMA state'ini sıfırlamak için) — `docker compose restart gateway`
4. Benchmark'ı host üzerinde çalıştır: `dotnet run --project Benchmark/RaspLb.Benchmark/RaspLb.Benchmark.csproj -c Release`. Runner varsayılan 50 warm-up isteğini ölçülen sonuçlardan hariç tutar.
5. Alternatif ayarlar gerekiyorsa `BENCHMARK_WARMUP_REQUESTS`, `BENCHMARK_REQUESTS`, `BENCHMARK_CONCURRENCY`, `BENCHMARK_GATEWAY_URL` ortam değişkenlerini kullan.
6. Sonuçları kaydet (şu an sadece konsol çıktısı var — kopyala/yapıştır ile bir `.txt`/`.md` dosyasına kaydet, örn. `benchmarks/manual/run-01.txt`, çünkü runner henüz dosyaya yazmıyor).
7. `Invoke-RestMethod http://localhost:5200/debug/metrics` ile son EWMA durumunu da kaydet.

**Neden önce bu:** Vizyon belgesi hiçbir geçmiş benchmark sayısının kanıtı olmadığını söylüyor. İlk somut görevin (vision §13) gerektirdiği "mevcut v2 davranışını doğrula" adımı burada başlıyor.

---

## Aşama A — v2 regresyonunu yeniden üret ve teşhis et (öncelik #1)

**Durum (2026-09-18): Tamamlandı.** Üç policy beşer kez koşuldu. Varsayılan heterojen kapasite senaryosunda v2 regresyonu yeniden üretilemedi; v2 belirgin biçimde daha iyi çıktı. Ham veriler ve analiz `benchmarks/manual/` altındadır. Farklı yük senaryoları ayrı araştırma olarak açık kalır.

Vizyon belgesi açık: **yeni özellik ekleme, önce mevcut regresyonu anla.**

**Nerede:** `Gateway/RaspLb.Gateway/LoadBalancing/RaspV0/V1/V2LoadBalancingPolicy.cs`, `Program.cs` (appsettings üzerinden policy değiştirilebilir hale getirme).

**Yapılacaklar:**
1. **A/B karşılaştırması yapılabilir hale getir:** `appsettings.json`'da `LoadBalancingPolicy` alanını değiştirerek (`RaspV0`, `RaspV1`, `RaspV2`) aynı senaryoda 3 ayrı benchmark koşusu al. Şu an bunu manuel yapabilirsin (dosyayı değiştir → `docker compose restart gateway` → benchmark çalıştır), otomasyon gerekmiyor bu aşamada.
2. **Tanı logu ekle:** `RaspV2LoadBalancingPolicy.CalculateScore()` içine (satır ~122), her seçimde şu bileşenleri log'la: `destination.ConcurrentRequestCount`, `capacity`, `loadFactor`, `metrics.EwmaLatencyMs`, `predictedCompletion`, `metrics.EwmaErrorRate`, final `score`. `BuildMetricsLog()` zaten bunun büyük kısmını üretiyor (satır 173-210) — sadece `_logger.LogInformation` seviyesini gateway loglarında görünür kıl ve/veya bu veriyi `/debug/metrics`'e ek alan olarak ekle (`DestinationMetricsStore`'a `ConcurrentRequestCount` snapshot'ı da eklenebilir).
3. **Backend tarafında kuyruk-latency ayrımını gör:** `WorkController.cs`'nin döndürdüğü `queueDelayMs` ile `elapsedMs`'i, gateway'in ölçtüğü toplam latency ile karşılaştır. Benchmark zaten `queueDelayMs`'i topluyor (`Program.cs` satır 58-61, 207-284 QUEUE METRICS bölümü) — bu veriyi RaspV0/V1/V2 koşularında karşılaştır.
4. **Hipotezi test et (çifte ceza):** v2'nin `predicted = latency_EWMA × loadFactor` formülünde, eğer `latency_EWMA` zaten kuyruk süresini içeriyorsa, deneysel olarak `loadFactor` çarpanını geçici olarak `1.0` yaparak (yani sadece v1 davranışına dön) sonucu karşılaştır. Bu, kod değiştirmeden `RaspV2`→`RaspV1` policy switch'i ile zaten yapılabilir — asıl iş `RaspV2` içinde `loadFactor`'ı devre dışı bırakıp yine de v2 kod yolunu (log'ları, metadata okumasını) çalıştırarak ayrıştırmak.
5. **Sonuçları belgeye işle:** `docs/RASP_LB_PROJECT_VISION.md` Bölüm 7 ve 8'i, artık kanıtlanmış sayılarla güncelle (belge zaten bunu bekliyor: *"Geçmiş sonuçlar elde edildiğinde... bu belgenin 7. bölümüne işle"*).

**Doğrulama:** Aynı senaryoda (aynı docker-compose ayarları, aynı benchmark parametreleri) v0/v1/v2 çalıştırıldığında, hangi metrikte (p95, goodput, queue) v2'nin gerçekten kötüleştiği rakamla gösterilmeli. Regresyon yeniden üretilemiyorsa bunu da yaz — "bulunamadı" da bir sonuçtur.

---

## Aşama B — Ölçüm ve güvenilirlik temeli

**Durum (2026-09-18): Tamamlandı.** Başarı ve hata sınıfları ayrıldı, YARP'ın normal proxy hata yolu `IForwarderErrorFeature` üzerinden okunuyor, debug endpoint'i tutarlı snapshot yayımlıyor ve benchmark'a açık warm-up eklendi. Performans kontrolünde anlamlı regresyon görülmedi (`benchmarks/manual/SUMMARY-asama-b-metrics.md`). Başarı, 404, 503, istemci iptali, connection-refused ve YARP timeout entegrasyonla doğrulandı.

**Nerede:** `Program.cs` (gateway ölçüm middleware'i), `WorkController.cs`, `DestinationMetricsStore.cs`.

**Yapılacaklar:**
1. **Başarı ve hata sözleşmesi:** Tamamlandı. 2xx kullanıcı başarısı; 4xx, 5xx, forwarder hatası, timeout, iptal ve beklenmeyen exception ayrı sayaçlardır. İstemci 4xx/iptali destination error EWMA'sını yükseltmez.
2. **Proxy hata yolu:** Tamamlandı. Beklenen YARP hataları exception varsayımıyla değil `IForwarderErrorFeature` üzerinden okunur; pipeline'dan gerçekten kaçan exception ayrıca kaydedilip yeniden fırlatılır.
3. **Destination kimliği benzersizliğini doğrula:** `DestinationMetricsStore` şu an sadece `DestinationId` ile anahtarlanıyor (`Get(DestinationState destination) => _metrics.GetOrAdd(destination.DestinationId, ...)`). Tek cluster'da (`work-cluster`) bu sorun değil; birden fazla cluster eklenirse `ClusterId+DestinationId` birleşimine geçmek gerekir — şimdilik not olarak bırakılabilir, aksiyon gerekmiyor.

**Doğrulama:** Başarı, 404, 503, istemci timeout/iptali, connection-refused ve YARP `RequestTimedOut` yolları doğrulandı.

---

## Aşama C — Retry (sınırlı, deadline-farkında kurtarma)

**Durum (2026-09-18): İlk dar kapsamlı sürüm uygulandı ve davranış olarak doğrulandı.** GET/HEAD için yanıt başlamadan oluşan belirli forwarder hatalarında farklı destination'a en fazla bir retry yapılıyor. Denenen destination'lar dışlanıyor; zaman ve token-bucket bütçeleri var. Connection-refused, unsafe POST, zaman bütçesi ve sistem bütçesi kontrollü test edildi. Ayrıntılar `benchmarks/manual/SUMMARY-asama-c-retry.md` dosyasında. Uygulama 503 retry'ı ve kesin deadline henüz kapsamda değil.

**Nerede:** YARP forwarder/request yaşam döngüsü incelendikten sonra belirlenecek ayrı resilience katmanı. `ILoadBalancingPolicy` içine retry eklenmeyecek. Basitçe `next()` çağrısını tekrar etmek farklı destination seçimini, request body replay'ini veya başlamış yanıt güvenliğini garanti etmediği için sıradan bir middleware olduğu varsayılmayacak.

**Yapılacaklar:**
1. **Tamamlandı:** `TraceIdentifier` ile her retry loglanıyor; `/debug/retries` mantıksal istek ve attempt sayılarını ayırıyor.
2. **Kısmen tamamlandı:** Connection/forwarder hataları ve YARP timeout enum'u kapsandı; 500/503 uygulama yanıtları tekrar edilmiyor.
3. **Kısmen tamamlandı:** Azami attempt, farklı destination ve yeni attempt başlatma zaman bütçesi var. İlk attempt'i zorla kesen kesin uçtan uca deadline Aşama D'ye kaldı.
4. **Tamamlandı:** Sistem geneli token bucket bütçesi ve debug sayaçları eklendi.

**Doğrulama:** Bir backend'in `ERROR_RATE`'ini geçici olarak `0.3` yap (`docker-compose.yml`), retry açık/kapalı iken başarı oranı ve ek trafik miktarını karşılaştır.

---

## Aşama D — SLO ve deadline farkındalığı

**Nerede:** Yeni middleware/servis, `DestinationMetricsStore`'a goodput sayaçları eklenebilir.

**Yapılacaklar:**
1. İstek başına bir deadline tanımla (örn. header ile veya sabit `500ms`).
2. "Goodput" say: SLO içinde başarıyla tamamlanan istek sayısı — ayrı bir sayaç.
3. `/debug/metrics`'e goodput ve deadline-aşan istek sayısını ekle.

**Doğrulama:** Yük artırıldığında (benchmark concurrency'yi 20'den 50'ye çıkar), goodput eğrisinin ham throughput'tan ne zaman ayrıştığını gözle.

---

## Aşama E — Brownout

**Nerede:** `WorkController.cs` (backend'e "azaltılmış mod" ekle), gateway tarafında bir sinyal/karar mekanizması.

**Yapılacaklar:**
1. `WorkController`'a zorunlu/opsiyonel iş ayrımı ekle (örn. sahte bir "zenginleştirme" adımı ekleyip yük yüksekken atla).
2. Hysteresis: modun sık açılıp kapanmasını önlemek için minimum bekleme süresi.

**Doğrulama:** Tam ve azaltılmış modun maliyet farkını ve kullanım oranını raporla.

---

## Aşama F — Load shedding / admission control

**Nerede:** `WorkController.cs`'deki `SemaphoreSlim` zaten bir kapasite kapısı — bunun önüne sınırlı bir bekleme kuyruğu ve erken red (429/503 + `Retry-After`) ekle.

**Yapılacaklar:**
1. `CapacityGate.WaitAsync` çağrısına `timeout` ekle; süre dolarsa `429`/`503` dön.
2. Reddedilen istek sayısını ayrı say (başarı oranından gizleme).

**Doğrulama:** Sürekli aşırı yük altında (concurrency >> toplam kapasite) kuyruk/bellek büyümesinin sınırlı kaldığını göster.

---

## Aşama G — Birleşik dayanıklılık deneyleri

Retry + SLO + brownout + shedding birlikte açıkken/kapalıyken (feature flag ile) her birinin katkısını ayrı ayrı ölç (ablation). Yeni kod gerekmiyor — Aşama C-F'de eklenen mekanizmaları config'den aç/kapa yapılabilir hale getirmek yeterli.

---

## Opsiyonel — NGINX taşıması

Vizyon belgesi bunu son aşama olarak koyuyor (Bölüm 11). Şimdilik aksiyon gerekmiyor; A-G tamamlanmadan başlanmaması öneriliyor.

---

## Genel çalışma disiplini (vizyon §4, §13'ten)

- Her aşamada **tek bir değişkeni** değiştir (algoritma formülü, retry, benchmark yöntemini aynı commit'te karıştırma).
- Her değişiklikten önce/sonra aynı senaryoda benchmark al, sonuçları `benchmarks/results/<run-id>/` altında sakla (manifest + ham CSV/JSON — vizyon §9'daki önerilen düzen).
- İyileşmeyen metrikleri de yaz — "olumsuz sonuç da teslimattır" (vizyon ilke #10).
- Rastgeleliğe dayalı testlerden kaçın (Random.Shared için seed enjekte etme opsiyonu düşünülebilir ama bu B/C aşamalarına ek iş, şimdilik not).

---

## Çalışma şekli (bu proje için)

Kodu Claude yazacak. Her adımda ne değiştirdiğini (hangi dosya, hangi satır, neden) sohbette açıklayacak — bu belge dosyasına implementasyon detayı yazılmayacak, sadece plan/rehber olarak kalacak. Her aşama bitince benchmark ile doğrulama yapılacak ve sonuç birlikte değerlendirilecek.
