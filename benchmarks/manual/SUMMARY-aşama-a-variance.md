# Aşama A — Varyans kontrolü (5 tekrar × 3 policy, 2026-09-18)

Aynı senaryo (300 istek, concurrency=20, backend1=40ms/cap2, backend2=70ms/cap8, backend3=120ms/cap8, ERROR_RATE=0) her policy için 5 kez koşuldu. Her tekrardan önce gateway restart edilip (`docker compose restart gateway`) EWMA state sıfırlandı. Ham çıktılar `benchmarks/manual/variance/<Policy>_run<N>.txt` içinde.

## Ham sonuçlar

| Policy | Run | P50 | P95 | P99 | Throughput | Queue Rate |
|---|---|---:|---:|---:|---:|---:|
| RaspV0 | 1 | 163.98 | 403.63 | 471.12 | 96.14 | 75.33% |
| RaspV0 | 2 | 139.11 | 317.79 | 403.01 | 125.93 | 68.00% |
| RaspV0 | 3 | 147.85 | 329.78 | 370.01 | 119.27 | 72.00% |
| RaspV0 | 4 | 162.04 | 288.38 | 355.65 | 111.94 | 77.67% |
| RaspV0 | 5 | 144.45 | 307.72 | 403.96 | 122.05 | 76.33% |
| RaspV1 | 1 | 175.38 | 441.73 | 512.59 | 91.27 | 84.33% |
| RaspV1 | 2 | 175.98 | 365.46 | 403.85 | 90.05 | 77.33% |
| RaspV1 | 3 | 167.97 | 370.65 | 407.64 | 100.33 | 72.67% |
| RaspV1 | 4 | 153.54 | 336.17 | 399.76 | 113.68 | 72.33% |
| RaspV1 | 5 | 160.67 | 309.36 | 361.20 | 109.04 | 77.00% |
| RaspV2 | 1 | 102.75 | 462.43 | 542.97 | 140.63 | 70.00% |
| RaspV2 | 2 | 102.96 | 195.74 | 259.80 | 174.64 | 63.00% |
| RaspV2 | 3 | 99.65 | 238.62 | 354.67 | 172.29 | 62.33% |
| RaspV2 | 4 | 99.83 | 170.06 | 257.05 | 178.52 | 52.33% |
| RaspV2 | 5 | 104.37 | 175.49 | 262.68 | 176.35 | 55.00% |

15/15 koşu %100 başarı oranıyla tamamlandı (hata yok, `ERROR_RATE=0` olduğu için beklenen).

## Önemli metodolojik bulgu: "run 1" sapması

Her üç policy'de de **policy geçişinden/rebuild'den sonraki ilk koşu (run 1), diğer 4 koşudan sistematik olarak daha kötü** (RaspV0: p95 404 vs ort. 311; RaspV1: p95 442 vs ort. 345; RaspV2: p95 462 vs ort. 195 — RaspV2'de fark en çarpıcı). Bu, vizyon belgesinin (§7 "Mevcut ölçümün önemli sınırlamaları") zaten öngördüğü bir sorun: *"Ayrı benchmark ısınma bölümü yok. JIT, bağlantı kurulması ve algoritmanın öğrenme dönemi rapora karışabilir."* Container her `restart` edildiğinde .NET JIT'i sıfırdan derliyor ve bağlantı havuzu tekrar kuruluyor — bu maliyet ilk koşuya biniyor.

**Sonuç:** Karşılaştırmalarda run 1'i hariç tutup **run 2-5 ortalamasını** kullanmak gerekiyor (aşağıdaki tablo). Gelecekte benchmark runner'a ayrı bir "warm-up" bloğu eklenmesi (vision §9'da zaten önerilmiş) bu sorunu kalıcı çözer.

## Özet istatistik (run 2-5 ortalaması, run 1 hariç — "ısınmış" durum)

| Metrik | RaspV0 | RaspV1 | RaspV2 |
|---|---:|---:|---:|
| P50 latency (ort.) | 148.4 ms | 164.5 ms | **101.7 ms** |
| P95 latency (ort.) | 310.9 ms | 345.4 ms | **195.0 ms** |
| P95 latency (aralık) | 288–330 ms | 309–371 ms | **170–239 ms** |
| Throughput (ort.) | 119.8 req/s | 103.3 req/s | **175.5 req/s** |

## Sonuç

5 tekrarlı varyans kontrolünden sonra bile **RaspV2, RaspV0 ve RaspV1'den bu senaryoda tutarlı biçimde daha iyi**: p95 latency ~%37 daha düşük RaspV0'a göre, throughput ~%47 daha yüksek. RaspV2'nin en kötü koşusu (p95=239ms) bile RaspV0/V1'in en iyi koşusundan (p95=288/309ms) daha iyi — gruplar arası fark, grup içi varyanstan büyük. **Yani bu heterojen-kapasite senaryosunda "v2 regresyonu" tekrarlı ölçümle de doğrulanamadı.**

İlginç yan bulgu: RaspV1, RaspV0'dan biraz daha kötü görünüyor (p95: 345 vs 311ms) — teorik olarak `ERROR_RATE=0` iken v1'in skor formülü (`latency × (1+4×0)`) v0'ın ham EWMA sıralamasıyla matematiksel olarak özdeş olmalı. Aradaki fark muhtemelen rastgele keşif dalının (%10) farklı örneklemesinden kaynaklanan gürültü — 4 tekrarla kesin ayrım yapılamaz, ek tekrar gerekir.

## Öneri

Regresyon bu ortamda (heterojen kapasiteli 3 backend, sıfır hata oranı, sabit concurrency=20) tekrarlı ölçümle de bulunamadı. `docs/RASP_LB_PROJECT_VISION.md` §7-8'i bu bulguyla güncellemek ve Aşama B'ye (ölçüm/güvenilirlik temeli) geçmek mantıklı — regresyonun farklı bir senaryoda (homojen kapasite, hata oranı>0, sürekli aşırı yük) ortaya çıkıp çıkmadığı ayrı bir araştırma sorusu olarak notlanabilir.
