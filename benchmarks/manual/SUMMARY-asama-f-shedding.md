# Aşama F — Load shedding + brownout sinyali (2026-10-03)

## Ne değişti

- **Backend `Admission/AdmissionGate.cs` (yeni):** Controller'daki çıplak `SemaphoreSlim` yerine geçti. Bekleyen/aktif istek sayısını tutuyor ve kuyruğu sınırlıyor (`MAX_QUEUE_LENGTH`, varsayılan kapasite×4; `QUEUE_TIMEOUT_MS`, varsayılan 250). Sınır aşılırsa `503`, `Retry-After: 1` ve `X-Rasp-Shed: queue-full|queue-timeout` dönüyor. `SHEDDING_ENABLED=false` ile kapatılabiliyor.
- **Brownout sinyali değişti.** Önceki sinyal doluluk oranıydı (`aktif/kapasite ≥ 0.8`). Yeni sinyal **tahmini kuyruk bekleme süresi** = bekleyen × EWMA servis süresi / kapasite. Eşik 50 ms. Toparlanma için sinyalin 2 sn boyunca kesintisiz 25 ms'nin altında kalması gerekiyor. Durum okuma anında da yeniden hesaplanıyor, böylece trafik bitince eski durumda takılı kalmıyor. `BROWNOUT_ENABLED=false` ile kapatılabiliyor.
- **Gateway:** `Slo/RecentTrafficWindow.cs` son 120 sn'yi saniye saniye tutuyor: zamanında / geç / reddedilen / hata ve 2xx'ler için p50/p95. `/debug/live` panelin tek veri kaynağı; backend'lerin `/debug/state` çıktısını da topluyor. v2'nin her istekte yazdığı Information logu Warning seviyesine çekildi, çünkü saniyede yüzlerce log satırı yük testini çarpıtıyordu.
- **Panel yeniden yazıldı** (`wwwroot/`): üstte son 5 sn özeti, altında backend tablosu, sonuç/p95/kuyruk grafikleri ve olay listesi.

## Normal yük (kapalı döngü, 3000 istek, concurrency=20)

| | Önce (doluluk sinyali) | Sonra (kuyruk sinyali) |
|---|---|---|
| Azaltılmış modda dönen istek | %84 | %31 |
| Throughput | 196 istek/sn | 163 istek/sn |
| p95 | 152 ms | 157 ms |

Throughput'taki düşüş beklenen bir sonuç: eskiden normal yükte bile zenginleştirme adımı atlanıyordu, yani hız için sessizce kaliteden kesiliyordu.

## Açık döngülü aşırı yük testi (k6, `benchmarks/load/overload.js`)

Fazlar: 120 → 220 → 350 → 120 istek/sn, her biri ~60 sn. Kaba kapasite tam modda ~160, brownout'ta ~230 istek/sn. Faz tablosu gateway'in saniyelik serisinden çıkarıldı; rampa saniyeleri hariç tutuldu.

### Shedding kapalı

| Faz | Gelen/sn | Zamanında/sn | Geç/sn | Reddedilen/sn | Hata/sn | p95 ort. | p95 maks. |
|---|---|---|---|---|---|---|---|
| 120/sn | 120 | 120 | 0 | 0 | 0 | 153 | 155 |
| 220/sn | 220 | 220 | 0 | 0 | 0 | 166 | 243 |
| 350/sn | 240* | 0 | 196 | 0 | 44 | 7330 | 10134 |
| 120/sn (toparlanma) | 164 | 82 | 60 | 0 | 23 | 3028 | 10036 |

\* İstemci 350/sn göndermeye çalıştı ama 2.924 istek hiç gönderilemedi; bekleyen istekler bütün sanal kullanıcıları meşgul etti. Toplam: 38.777 başarılı, 5.573 hata (10 sn istemci timeout'u). Kuyruklar maksimum 407 / 1286 / 1287 isteğe çıktı. Yük düştükten sonra ~30 sn boyunca hâlâ geç yanıt verildi.

### Shedding açık

| Faz | Gelen/sn | Zamanında/sn | Geç/sn | Reddedilen/sn | Hata/sn | p95 ort. | p95 maks. |
|---|---|---|---|---|---|---|---|
| 120/sn | 120 | 120 | 0 | 0 | 0 | 152 | 153 |
| 220/sn | 220 | 220 | 0 | 0 | 0 | 169 | 228 |
| 350/sn | 350 | 228 | 0 | 122 | 0 | 358 | 367 |
| 120/sn (toparlanma) | 120 | 120 | 0 | 0 | 0 | 152 | 155 |

Toplam: 47.274 istek, 40.581 başarılı, 6.693 reddedilen (503), başka hata yok. **Deadline'ı aşan istek: 0.** En yavaş başarılı istek 379 ms. Kuyruklar sınırda kaldı (8 / 31 / 32). Yük düşünce sistem 1-2 sn içinde normale döndü.

Aşırı yükte reddedilen miktar (~122/sn), gelen yük ile kapasite arasındaki farka (~350 − 230) denk.

### Brownout düzeltmesinin etkisi

Shedding açık testin ilk koşusu, toparlanma kuralı düzeltilmeden önce yapıldı. O koşuda brownout her ~2 sn'de açılıp kapanıyordu ve 220/sn fazında sonuç p95 328 ms, saniyede 10 reddedilen istekti. Kural düzeltilince aynı faz p95 169 ms ve sıfır reddetmeyle geçti.

## Açık kalanlar / sonraki adımlar

1. **Hızlı 503'ler v2'yi yanıltıyor olabilir.** Reddedilen isteklerin süresi (~1 ms) gateway'deki EWMA gecikmeye karışıyor ve gateway'deki in-flight sayısını düşük tutuyor. Sonuç: reddetmelerin çoğu (4.299'u) kapasitesi 2 olan backend1'de. Reddedilen yanıtlar EWMA gecikmeden hariç tutulmalı ya da gateway reddedilen isteği başka backend'e retry etmeli.
2. **`queue-timeout` ile reddetme istemciye 250 ms kaybettiriyor** (backend3'te 1.464 istek). Tahmini bekleme timeout'u zaten aşıyorsa istek kapıda hemen reddedilebilir.
3. Backend3, orta yükte (220/sn) brownout'a hâlâ ~6-8 sn'de bir girip çıkıyor. Sinyal yumuşatılabilir.
4. Kapasitesi 2 olan backend1 neredeyse sürekli azaltılmış modda. Küçük kapasitede tek bir bekleyen istek bile eşiği aşıyor.

## Yeniden üretmek

```powershell
docker compose up -d --build                         # shedding + brownout açık
$env:SHEDDING_ENABLED="false"; docker compose up -d --force-recreate   # shedding kapalı
docker run --rm -i --network rasplb_default -e BASE_URL=http://gateway:8080 grafana/k6 run - < benchmarks/load/overload.js
```

Panel: http://localhost:5200/

## İyileştirme turu: reddedilen 503'ler ve kapıda erken reddetme (2026-10-03)

Yukarıdaki "Açık kalanlar" listesinin 1. ve 2. maddeleri, her seferinde tek değişiklikle aynı k6 senaryosunda ölçüldü. Her satır tek bir koşu.

| | A: İlk sürüm | B: + reddedilenler EWMA gecikmeden hariç | C: + kapıda erken reddetme |
|---|---|---|---|
| Başarılı istek (toplam) | 40.581 | 40.586 | 40.601 |
| 350/sn fazında zamanında/sn | 228 | 228 | 227 |
| Reddetmeler (b1 / b2 / b3) | 4.299 / 871 / 1.523 | 2.449 / 2.237 / 2.002 | 2.489 / 2.160 / 2.024 |
| Bekledikten sonra reddedilen | 2.061 | 2.909 | 371 |
| Reddedilen isteğin ort. süresi* | 78 ms | 110 ms | 15 ms |
| Ortalama yanıt süresi (tümü) | 147 ms | 160 ms | 149 ms |
| p95 (2xx) | 321 ms | 348 ms | 352 ms |

\* k6'nın tüm istekler ve 2xx'ler için verdiği ortalamalardan türetildi.

**B — hipotez doğrulandı ama sonuç kötüleşti.** Gateway artık backend'in reddettiği yanıtları (`DestinationRequestOutcome.Shed`) tanıyor. Bunlar sadece hata EWMA'sını besliyor, gecikme EWMA'sına girmiyor. Reddetmeler backend'lere dengeli dağıldı; yani hızlı 503'ler gerçekten backend1'e trafik çekiyordu. Ancak backend1'in anında yaptığı reddetmeler bu sefer backend2 ve backend3'te 250 ms bekledikten sonra yapıldı ve reddedilenler daha uzun bekledi. Eski davranış yanlış nedenle iyi sonuç veriyordu.

**C — asıl kaldıraç.** Backend, öndeki istekler kuyruk timeout'undan önce bitmeyecekse (tahmini bekleme > `QUEUE_TIMEOUT_MS`) isteği kapıda hemen reddediyor (`X-Rasp-Shed: predicted-wait`). Bekledikten sonra reddedilenler %87 azaldı, reddedilen istekler ortalama 15 ms'de dönüyor ve kuyruklar daha kısa kaldı (backend3 en fazla 16).

**Değişmeyen:** Kabul edilen iş miktarı. Sistem kapasitenin sınırında çalışıyor; bu turun kazancı reddedilen kullanıcının beklediği süre. Kabul edilen isteklerin p95'i A'ya göre ~30 ms yüksek. Tek koşuda bu gürültü olabilir; daha düşük p95 istenirse `QUEUE_TIMEOUT_MS` küçültülebilir, bu da daha çok reddetme demek.

**Sınırlama:** Her yapılandırma bir kez koşuldu. Kesin sonuç için her biri en az 3 kez tekrarlanmalı.
