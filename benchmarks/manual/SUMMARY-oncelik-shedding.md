# Öncelik bazlı shedding (2026-10-03)

## Ne değişti

- İstekler `X-Rasp-Priority: critical | normal | sheddable` başlığı taşıyabiliyor. Başlık yoksa ya da tanınmıyorsa öncelik `normal` sayılıyor.
- `AdmissionGate` kuyruk bütçesini (kuyruk uzunluğu ve bekleme süresi) önceliğe göre paylaştırıyor: critical %100, normal %60, sheddable %30. Kuyruk dolmaya başlayınca önce sheddable, sonra normal istekler kapıda reddediliyor; kuyruğun son kısmı yalnızca critical isteklere kalıyor.
- Gateway'in saniyelik trafik penceresi sonuçları öncelik sınıfına göre de ayırıyor. Panelde "Öncelik sınıfları" tablosu var.
- `tests/RaspLb.Tests` (xUnit, 18 test): `AdmissionGate`, `BrownoutState`, `DestinationMetrics`, `RecentTrafficWindow`.

## Ölçüm (k6 `benchmarks/load/priority.js`, tek koşu)

Trafik %20 critical, %50 normal, %30 sheddable. Fazlar: 120 → 350 → 120 istek/sn, ~1,5 dk.

| Öncelik | Başarılı | Reddedilen | Başarı oranı |
|---|---|---|---|
| critical | 4.761 | 0 | %100 |
| normal | 10.775 | 1.251 | %89,6 |
| sheddable | 2.051 | 5.211 | %28,2 |

- Başarılı isteklerin p95'i 241 ms; en yavaş başarılı istek 313 ms. Öncelik olmadan, aynı yükte p95 ~350 ms idi. Kuyruğun derin kısmına yalnızca critical istekler girebildiği için kuyruklar daha kısa kalıyor.
- Aşırı yük fazında (panelden, son 5 sn) critical %100, normal %86,5, sheddable %3,7 zamanında yanıtlandı.

## Sınırlar

- Kuyruk FIFO. Critical bir istek, kendisinden önce kuyruğa girmiş normal isteklerin arkasında bekleyebilir. Bu ölçümde bir sorun yaratmadı; sorun olursa öncelikli kuyruk gerekir.
- Gateway önceliği yönlendirme kararında kullanmıyor; sadece sayıyor.

## Reddedilen isteğin başka backend'e yeniden gönderilmesi (2026-10-03)

**Ne değişti:**
- Backend bir isteği reddettiğinde (503 + `X-Rasp-Shed`), retry mümkünse YARP response transform'u (`Resilience/ShedRetry.cs`) 503'ün gövdesini bastırıyor. Yanıt istemciye hiç başlamadığı için `RaspRetryMiddleware` isteği denenmemiş bir backend'e yeniden gönderebiliyor.
- Kurallar bağlantı hatası retry'ı ile aynı: sadece GET/HEAD, en fazla 2 deneme, zaman bütçesi ve ortak token bütçesi (saniyede 20).
- `sheddable` istekler bu şekilde yeniden denenmiyor; onları başka backend'e göndermek, hafifletilmek istenen yükü başka yere taşımak olurdu.
- Retry yapılamazsa (bütçe yok, başka backend kalmadı) istemciye eşdeğer bir 503 yazılıyor.
- `RaspRetry:RetryOnShed` ile kapatılabiliyor.

**Ölçüm** (`priority.js`, tek koşu, önceki koşuyla aynı senaryo):

| Öncelik | Önce: başarılı / reddedilen | Sonra: başarılı / reddedilen |
|---|---|---|
| critical | 4.761 / 0 | 4.856 / 0 |
| normal | 10.775 / 1.251 (%89,6) | 10.970 / 1.038 (%91,4) |
| sheddable | 2.051 / 5.211 (%28,2) | 1.773 / 5.412 (%24,7) |

- 1.069 yeniden deneme yapıldı, 843'ü başarılı oldu (%79). 812 deneme token bütçesine takıldı. Reddetme dışında hata yok.
- **Toplam başarılı istek değişmedi** (17.587 → 17.599); sistem kapasite sınırında. Kurtarılan normal/critical isteklerin bedelini sheddable trafik ödedi; öncelik sisteminin amacı da bu.
- p95 (2xx) 241 → 249 ms; en yavaş başarılı istek 313 → 368 ms. İkinci deneme süreyi uzatıyor ama deadline'ın (500 ms) altında kalıyor.

**Yan bulgu:** Reddedilen isteklerin %79'unun başka backend'de yer bulması, v2'nin bazen dolu bir backend'i seçtiğini, o anda başkasında boş yer olduğunu gösteriyor. Yönlendirme tarafında iyileştirme payı var.

## Keşfin dolu backend'lere gitmemesi ve deadline'a duyarlı yeniden deneme (2026-10-03)

**1. v2 keşfi (exploration):** v2 isteklerin %10'unu skora bakmadan rastgele bir backend'e gönderiyordu; bu, dolu backend'leri de kapsıyordu. Keşif artık sadece gateway'e göre boş kapasitesi olan backend'ler arasında yapılıyor (`ConcurrentRequestCount < RaspCapacity`). Hepsi doluysa o istek için keşif atlanıyor.

Ölçüm (tek koşu): normal %91,4 → %92,1; reddedilip başka backend'e gönderilmesi gereken normal/critical istek sayısı 1.881 → 1.750 (−%7). Etki tek koşunun gürültü sınırında. Keşif, reddedilen isteklerin backend'de yer bulmasının ana nedeni değil. Değişiklik mantıken doğru olduğu için kaldı.

**2. Yeniden denemenin deadline'ı aşması:** Aynı koşuda 22 başarılı istek 500 ms'yi aştı; en yavaşı 738 ms. Sebep: kuyrukta 150-250 ms bekleyip zaman aşımıyla reddedilen istekler de yeniden deneniyordu, ikinci bekleme deadline'ı aşıyordu. Retry'ın 5 sn'lik zaman bütçesi 500 ms'lik SLO için çok gevşekti.

Düzeltme: `RaspRetry:ShedRetryMaxElapsedMs` (varsayılan 100). Reddedilen istek sadece bu süre içinde döndüyse yeniden deneniyor; aksi halde istemciye 503 dönülüyor.

| | Düzeltmeden önce | Sonra |
|---|---|---|
| Deadline aşan başarılı istek | 22 | 5 |
| En yavaş başarılı istek | 738 ms | 615 ms |
| Yeniden deneme / kurtarılan | 1.059 / 799 | 1.073 / 824 |
| Geç döndüğü için denenmeyen | – | 104 |
| normal başarı oranı | %92,1 | %91,6 |

**Kalan:** Kalan 5 geç istek büyük ihtimalle hızlı reddedilip yeniden gönderilen ama ikinci backend'de de uzun bekleyen istekler. Tam çözüm deadline propagation: gateway kalan süreyi bir başlıkla backend'e iletir, backend kuyruk bekleme süresini buna göre kısar.

## Deadline propagation (2026-10-03)

**Ne değişti:**
- Gateway (`Slo/DeadlinePropagation.cs`) her denemede (retry dahil) SLO deadline'ından kalan süreyi `X-Rasp-Deadline-Ms` başlığıyla backend'e iletiyor. Başlığı her zaman gateway yazıyor; istemcinin gönderdiği değer siliniyor. `X-Rasp-Deadline-Ms: 99999` gönderen istemcinin isteği backend'e 499 olarak ulaştı.
- `AdmissionGate` kuyrukta en fazla `kalan süre − EWMA işlem süresi` kadar bekletiyor. Bu süre sıfırın altındaysa istek kapıda reddediliyor (`predicted-wait`). Boş slot varsa istek her zamanki gibi hemen kabul ediliyor. Shedding kapalıyken deadline yok sayılıyor.
- 3 yeni birim testi; toplam 21.

**Ölçüm** (`priority.js`, tek koşu):

| | Önce | Sonra |
|---|---|---|
| Deadline aşan başarılı istek | 5 | **0** |
| En yavaş başarılı istek | 615 ms | 333 ms |
| p95 (2xx) | 248 ms | 248 ms |
| critical / normal / sheddable başarı | %100 / %91,6 / %23,7 | %100 / %92,5 / %23,7 |
| Yeniden deneme / kurtarılan | 1.073 / 824 | 1.045 / 805 |

Bu turla birlikte aşırı yük altında gecikme garantisi uçtan uca sağlanıyor: gateway kalan süreyi biliyor, backend de geç kalacak işi kabul etmiyor.

## Öncelikli kuyruk (2026-10-03)

**Ne değişti:**
- `AdmissionGate` içindeki `SemaphoreSlim` (FIFO) kaldırıldı. Yerine her öncelik için ayrı bir FIFO bekleme sırası geldi; boşalan slot önce critical, sonra normal, en son sheddable bekleyene veriliyor.
- Zaman aşımı ve istemci iptalinde bekleyen sıradan çıkıyor. Slot ile iptal aynı anda gelirse hangisi önce gelirse o geçerli; slot kaybolmuyor.
- Kapıda erken reddetme hesabı artık sadece öndekileri sayıyor: aynı ya da daha yüksek öncelikteki bekleyenler. Brownout sinyali toplam kuyruğa bakmaya devam ediyor. Snapshot'ta öncelik başına bekleyen sayısı da var.
- 4 yeni test; toplam 27. Bunlardan biri stres testi: 32 eşzamanlı işçi, rastgele öncelikler; kapasite hiç aşılmıyor ve sonunda hiç slot kaybolmuyor.

**A/B ölçüm** (`priority.js`, k6'da öncelik başına p95, aynı build, sadece `AdmissionGate` farklı, her biri tek koşu):

| p95 (2xx) | FIFO kapı | Öncelikli kuyruk |
|---|---|---|
| critical | 256 ms | **153 ms** |
| normal | 248 ms | 240 ms |
| sheddable | 155 ms | 155 ms |

Critical istekler artık önlerindeki normal isteklerin arkasında beklemiyor; p95'leri %40 düştü. Sheddable'ın p95'i her iki durumda da düşük, çünkü bu istekler ancak kuyruk kısayken kabul ediliyor. Başarı oranları değişmedi; deadline aşan istek 0.
