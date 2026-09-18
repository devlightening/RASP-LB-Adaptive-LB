# Aşama A — v0/v1/v2 karşılaştırması (tek koşu, 2026-09-18)

Senaryo: 300 istek, concurrency=20, backend1=40ms/cap2, backend2=70ms/cap8, backend3=120ms/cap8, ERROR_RATE=0 (hepsinde). Her policy için gateway+backend'ler yeniden build/recreate edildi, tek bir readiness-probe isteği dışında ısınma yapılmadı. Tek tekrar — vizyon belgesinin de uyardığı gibi tek koşu varyans içerir, kesin hüküm için ≥5 tekrar gerekir.

| Metrik | RaspV0 (run-02) | RaspV1 (run-03) | RaspV2 (run-01) |
|---|---:|---:|---:|
| P50 latency | 161.22 ms | 156.19 ms | **101.05 ms** |
| P95 latency | 401.26 ms | 390.93 ms | **248.72 ms** |
| P99 latency | 571.97 ms | 436.40 ms | **329.05 ms** |
| Throughput | 101.43 req/s | 105.72 req/s | **166.39 req/s** |
| Queue rate | 76.33% | 72.67% | **60.33%** |
| Ortalama kuyruk | 84.58 ms | 83.13 ms | **21.00 ms** |
| Backend-1 EWMA (son) | 307.22 ms | 218.97 ms | **76.77 ms** |

## Sonuç (bu tek koşuya göre)

Bu senaryoda **kullanıcının aktardığı "v2 regresyonu" gözlenmedi** — tam tersi, RaspV2 her metrikte RaspV0 ve RaspV1'den belirgin biçimde daha iyi:
- p95 latency ~%38 daha düşük (401ms → 249ms)
- throughput ~%60 daha yüksek (101 → 166 req/s)
- kuyruk oranı ve süresi çok daha düşük

**Neden:** v0 ve v1 kapasiteyi hiç bilmiyor. Backend-1'in düşük yapılandırılmış gecikmesi (40ms) onları başlangıçta ona yönlendirmeye itiyor, ama kapasitesi (2 slot) hemen doluyor ve kuyruk oluşuyor — EWMA bunu ancak gecikmeli olarak (istekler tamamlandıkça) öğreniyor. v2 ise `RaspCapacity` metadata'sını ve anlık `ConcurrentRequestCount`'u kullanarak bu tıkanmayı **önceden** görüyor ve isteği kapasiteli backend'lere yönlendiriyor — bu senaryoda tam olarak tasarlandığı gibi çalışıyor.

## Bu ne anlama geliyor — vizyon belgesindeki hipotezle ilişki

`docs/RASP_LB_PROJECT_VISION.md` §8'deki "çifte ceza" hipotezi (gateway latency EWMA'sının zaten kuyruğu içerdiği, v2'nin bunu ikinci kez cezalandırdığı) **bu senaryoda regresyona yol açmıyor** — v2 hâlâ kazanıyor, çünkü çifte ceza olsa bile kapasite farkındalığının getirdiği kazanç bunu fazlasıyla kapatıyor.

Bu, kullanıcının aktardığı regresyonun:
1. **Farklı bir senaryoda** ortaya çıkıyor olabilir (örn. homojen kapasiteli backend'ler, ya da hata oranı > 0, ya da çok daha yüksek concurrency/sürekli aşırı yük),
2. **Tek bir "kötü" koşunun gürültüsü** olabilir (tek koşuda %20-30 varyans normal olabilir — henüz birden fazla tekrar almadık),
3. Ya da kullanıcının aktardığı geçmiş konuşmada farklı kod/config durumuydu (vizyon belgesi bunu zaten "kanıtsız" olarak işaretlemişti).

## Sıradaki adım önerisi

- **Tekrar sayısını artır** (aynı senaryoyu 5 kez daha koş, varyansı gör) — regresyon gerçekten yoksa bunu güvenle raporlayabiliriz.
- **Farklı senaryolar dene:** homojen kapasite (üç backend de cap=8), yüksek concurrency (örn. 50-100) ile sürekli aşırı yük, `ERROR_RATE>0` ile hata farkındalığını test et.
- Bunlardan hiçbiri regresyonu göstermezse, vizyon belgesinin Bölüm 7-8'ini "regresyon bu ortamda yeniden üretilemedi" notuyla güncelle ve Aşama B'ye (ölçüm/güvenilirlik temeli) geç.
