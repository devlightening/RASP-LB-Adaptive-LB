# RASP-LB — Proje Vizyonu, Problem Tanımı ve Uçtan Uca Geliştirme Planı

> Claude için başlangıç belgesi: Önce bu dosyayı, ardından gerçek kaynak kodunu ve Git durumunu incele. Mevcut davranışı ölçmeden algoritmayı değiştirme. Buradaki hedefleri tamamlanmış özellikler olarak sunma.

## 1. Belgenin kapsamı ve kanıt durumu

Bu belge, RASP-LB projesinin amacını, mevcut uygulamasını, araştırma sorularını ve portfolyoda sunulabilecek nihai ürününü bir arada anlatır. 18 Eylül 2026 tarihinde depo doğrudan okunarak hazırlanmıştır. İlk incelemede kullanılan HEAD: `21775f4ceb69fc0c7c7b179c568b8e27ab394e28`.

Belge, önceki proje görüşmelerindeki hedeflerin kaynak koduyla karşılaştırılmasıyla oluşturuldu. Tarihsel bir iddiayı doğrulayacak ham veri bulunmadığında bu durum açıkça işaretlendi; ölçüm sonucu veya tamamlanmış özellik üretilmiş gibi gösterilmedi.

Belgede üç kanıt düzeyi kullanılır:

- **Kodda doğrulandı:** Dosyalar veya Git geçmişi üzerinden gözlemlendi. Çalıştırılarak doğrulandığı anlamına gelmez.
- **Kullanıcının aktardığı geçmiş:** İstekte belirtilen, fakat ham sonuçları veya ayrıntılı konuşması mevcut olmayan bilgi.
- **Önerilen hedef / hipotez:** Henüz uygulanmamış plan veya deneyle sınanması gereken açıklama.

Bu belge hazırlanırken uygulama veya benchmark çalıştırılmadı; yalnızca dokümantasyon eklendi. Depodaki mevcut `.claude/` ve `graphify-out/` içerikleri değiştirilmedi. Erişilebilir takip edilen dosyalarda ham benchmark sonuçları, NGINX uygulaması ve otomatik test projesi bulunmadı.

## 2. Hangi problemi çözmeye çalışıyoruz?

Birden fazla backend'e trafik dağıtmak, her backend'e eşit sayıda istek göndermekten daha büyük bir problemdir. Sunucuların yanıt süresi, eşzamanlı işleme kapasitesi, hata oranı ve o an taşıdığı yük birbirinden farklı olabilir. Boştayken hızlı olan bir backend, kapasitesi dolduğunda en kötü seçeneklerden birine dönüşebilir. Başka bir backend daha yavaş görünmesine rağmen aynı anda daha fazla isteği işleyebilir.

Örneğin mevcut deney ortamında bir backend'in yapılandırılmış işlem gecikmesi 40 ms, eşzamanlı kapasitesi 2; diğerinin gecikmesi 70 ms, kapasitesi 8'dir. Yalnızca geçmiş gecikmeye bakarak ilk backend'e yönelmek, onun önünde kuyruk birikmesine neden olabilir. Bu sayıların deney girdisi olduğu, ölçülmüş uçtan uca gecikme olmadığı unutulmamalıdır.

Benzer biçimde hızlı hata üreten bir servis, sadece yanıt süresi ölçülürse iyi görünebilir. Başarısız istekleri sınırsız tekrar denemek ise zaten zorlanan sistemi daha fazla yükleyebilir. Aşırı yük altında her isteği kabul etmek, çok sayıda kullanıcının uzun süre bekleyip sonunda başarısız olmasıyla sonuçlanabilir.

Temel araştırma sorumuz şudur:

**Bir isteği, mevcut yükü ve güvenilirliği dikkate alarak hangi backend'e yönlendirmeliyiz; sistemin toplam kapasitesi aşıldığında hangi işi, hangi kalite düzeyinde ve hangi zaman bütçesi içinde kabul etmeliyiz?**

Bu soru projenin yönlendirme, retry, SLO, brownout ve load shedding aşamalarını birbirine bağlar.

## 3. Misyon ve başarı tanımı

RASP-LB'nin misyonu, değişken ve heterojen backend koşullarında kullanıcıya ulaşan başarılı, zamanında tamamlanmış yanıtları artıran; kararları açıklanabilen ve deneyleri tekrar üretilebilen bir yük dengeleme prototipi geliştirmektir.

Nihai başarı, tek bir testte en düşük ortalama gecikmeyi elde etmek değildir. Aşağıdaki sonuçları birlikte değerlendirmeliyiz:

- Başarı oranı ve son kullanıcının gördüğü hata oranı.
- p50, p95 ve p99 uçtan uca gecikme; özellikle kuyruğun sonundaki kullanıcıların deneyimi.
- **Goodput:** Birim zamanda, tanımlı SLO içinde başarıyla tamamlanan mantıksal istek sayısı.
- Kuyruk beklemesi, backend kullanımı ve kapasite dağılımı.
- Bozulma ve iyileşmeden sonra uyum süresi; yönlendirme salınımı.
- Gateway'in CPU, bellek, tahsis ve karar verme maliyeti.
- Retry nedeniyle üretilen ek yük ve brownout nedeniyle azaltılan hizmet kalitesi.

Başarı kriterleri senaryo bazında deneyden önce yazılmalıdır. “Her durumda NGINX'ten hızlı”, “production-ready” veya belirli bir yüzde iyileşme gibi iddialar kanıt oluşmadan kullanılmamalıdır. RASP adının açılımı erişilebilir kaynaklarda doğrulanmadığından burada bir açılım icat edilmemiştir.

## 4. Tasarım ilkeleri

1. **Önce ölçüm:** Her algoritma değişikliği, tekrar üretilebilir bir problem ve karşılaştırılabilir sonuçla ilişkilendirilmeli.
2. **Basit ve açıklanabilir karar:** Bir backend'in neden seçildiği; gecikme, hata, yük ve kapasite bileşenleri üzerinden anlaşılabilmeli.
3. **Öğrenme ve keşif dengesi:** İyi görünen backend kullanılmalı, iyileşen diğer backend'lerin yeniden keşfedilmesi mümkün olmalı.
4. **Kapasite farkındalığı:** Kısa servis süresi ile yüksek toplam işleme kapasitesi aynı şey değildir.
5. **Sınırlı kaynak kullanımı:** Retry sayısı, kuyruk, bekleme süresi ve keşif maliyeti sınırlanmalı.
6. **Doğru hata semantiği:** Uygulama hatası, ağ hatası, timeout, istemci iptali ve bilinçli reddetme ayrılmalı.
7. **Kararlı geri besleme:** Eski ölçüm, gürültü ve eşzamanlı kararların aynı backend'e yığılması hesaba katılmalı.
8. **Adil karşılaştırma:** Algoritmalar aynı iş yükü, kaynak sınırı, log seviyesi ve ısınma düzeniyle karşılaştırılmalı.
9. **Aşamalı geliştirme:** Önce v2 anlaşılmalı; retry ve aşırı yük kontrolü aynı anda eklenerek neden-sonuç ilişkisi kaybedilmemeli.
10. **Olumsuz sonuçlar da teslimattır:** Bir yöntemin neden kötüleştiğini göstermek, mühendislik değerinin bir parçasıdır.

## 5. Mevcut mimari — kodda doğrulandı

```text
Host üzerinde .NET benchmark
  http://localhost:5200/api/work
                 |
                 v
  ASP.NET Core + YARP gateway
  Load balancing -> ölçüm middleware'i -> passive health middleware'i -> proxy
                 |
        +--------+--------+
        |        |        |
    backend1 backend2 backend3
    40 ms    70 ms    120 ms      yapılandırılmış işlem gecikmesi
    2 slot   8 slot   8 slot      SemaphoreSlim kapasitesi

  Tamamlanan yanıt -> destination bazlı EWMA -> sonraki yönlendirme kararı
```

### Teknoloji ve dosya haritası

| Bileşen | Kaynak | Doğrulanan rol |
|---|---|---|
| Çözüm | `RaspLb.slnx` | .NET projelerinin kökü |
| Ortam | `docker-compose.yml` | Üç backend ve gateway |
| Gateway başlangıcı | `Gateway/RaspLb.Gateway/Program.cs` | Policy kaydı, proxy pipeline, ölçüm, debug endpoint |
| Gateway ayarları | `Gateway/RaspLb.Gateway/appsettings.json` | Route, destination adresleri, seçili policy ve kapasiteler |
| Algoritmalar | `Gateway/RaspLb.Gateway/LoadBalancing/RaspV*LoadBalancingPolicy.cs` | v0, v1 ve v2 seçimleri |
| Ölçümler | `DestinationMetrics.cs`, `DestinationMetricsStore.cs` aynı klasörde | Kilitli sayaçlar, latency/error EWMA, destination sözlüğü |
| Backend | `backend/RaspLb.Backend/Controllers/WorkController.cs` | Gecikme/hata simülasyonu, kapasite ve kuyruk |
| Benchmark | `Benchmark/RaspLb.Benchmark/Program.cs` | İstek üretimi ve konsol raporu |

Gateway proje dosyası `net10.0` ve `Yarp.ReverseProxy` paketinin `2.3.0` sürümünü bildiriyor. Bu, depodaki bağımlılık durumudur; güncel sürüm önerisi değildir.

### Çalışma ayrıntıları

- Gateway host portu `5200`, backend host portları `5101`, `5102`, `5103`; konteynerlerin HTTP portu `8080`.
- `/api/{**catch-all}` rotası `work-cluster` kümesine gidiyor. Etkin policy `RaspV2`.
- Backend `GET /api/work` isteğinde kapasite kapısını bekliyor, yapılandırılmış gecikme kadar `Task.Delay` uyguluyor ve hata olasılığına göre 200 veya 503 dönüyor.
- Üç backend için mevcut `ERROR_RATE` değeri `0`; bu ayar hata farkındalığını sınamaz.
- Yanıt gövdesinde instance, yapılandırılmış gecikme, kapasite, aktif istek sayısı, kuyruk ve toplam backend süresi var.
- `/health` backend'de mevcut. Compose'taki `depends_on` hazır olma kanıtı değildir; ayrıca hazır olma kontrolü gerekir.
- Gateway'in ölçümü yük dengeleme sonrasındaki proxy süresini kapsıyor; saf backend işlem süresi değil. Kuyruk ve iletişim etkilerini içerebilir.
- Gateway ve benchmark yalnızca 2xx yanıtları kullanıcı açısından başarılı sayıyor. Gateway ayrıca 4xx, 5xx, YARP forwarder hatası, timeout, iptal ve beklenmeyen exception sonuçlarını ayrı sayaçlarda tutuyor. İstemci kaynaklı 4xx ve iptal, backend'in yönlendirme hata EWMA'sını yükseltmiyor.
- `/debug/metrics`, tek kilit altında alınan tutarlı snapshot üzerinden latency/error EWMA, toplam hata, destination kaynaklı hata ve sonuç sınıfı sayaçlarını yayımlıyor.
- Passive health middleware'i çağrılıyor; mevcut cluster ayarlarında health policy yapılandırması bulunmuyor. Bu çağrı tek başına çalışan arıza eleme mekanizmasının kanıtı değildir.
- Benchmark için Dockerfile var, fakat Compose içinde benchmark servisi yok. Kodun `localhost:5200` hedefi host üzerinde çalışmayı varsayıyor; ayrı konteyner içinde aynı adres gateway'i göstermez.

## 6. Sürüm geçmişi: v0, v0.1, v1, v2

Git geçmişinde erişilen iki commit:

- `2580e76` — `Initial RASP-LB prototype with YARP baselines and RASP v0.1`.
- `21775f4` — `Add RASP v2 load-aware routing and queue-aware benchmarking Experiment with RASP v2 in-flight capacity awareness and queue metrics`.

Commit mesajları niyeti gösterir; benchmark başarısını kanıtlamaz.

### v0 ve v0.1 — gecikme, ısınma ve keşif

**Kodda doğrulandı:** `RaspV0LoadBalancingPolicy` mevcut. Destination başına en az 5 tamamlanmış örnek toplanana kadar az gözlenen hedefler arasında dönüşümlü seçim yapılıyor. Isınma sonrasında %10 olasılıkla rastgele keşif, diğer durumlarda en düşük latency EWMA seçiliyor. Keşif aynı kazanan backend'i de seçebilir; isteklerin tam %10'unun farklı backend'e gideceği anlamına gelmez.

Latency EWMA güncellemesi `L_yeni = 0.20 × örnek + 0.80 × L_eski`; ilk örnekte doğrudan ölçüm kullanılıyor.

**Sürüm adlandırması sınırı:** İlk commit v0.1 diye adlandırılmış, ancak sınıf ve policy adı `RaspV0`. İncelenen ilk commit'te de ısınma ve keşif mevcut. Ayrı, daha eski bir “yalın v0” uygulaması veya v0'dan v0.1'e kesin fark bu geçmişten doğrulanamıyor. v0.1'i ayrı bir policy dosyası varmış gibi anlatma.

Bu aşamanın araştırma sorusu: Heterojen gecikmelerde geçmiş yanıt süresine dayalı seçim nasıl davranır; ilk ölçüm yanlılığı ve hiç tekrar denenmeyen backend sorunu nasıl azaltılır?

### v1 — hata farkındalığı

**Kodda doğrulandı:** Aynı ısınma ve keşif düzenine hata EWMA'sı ekleniyor:

```text
score_v1 = latency_EWMA × (1 + 4.0 × error_EWMA)
```

Error EWMA da `alpha = 0.20` ile güncelleniyor. Düşük skor seçiliyor. Bu mekanizma hızlı ama hatalı backend'in cazibesini azaltmayı amaçlar. Katsayının uygunluğu ve hızlı hata yanıtlarının etkisi deneyle sınanmalıdır. v1 kodunun bulunması, v1'in başarısının ölçülmüş olduğu anlamına gelmez.

### v2 — yük ve kapasite farkındalığı

**Kodda doğrulandı:** v1 skoruna destination'ın `ConcurrentRequestCount` değeri ve metadata'daki `RaspCapacity` ekleniyor:

```text
load_factor = 1 + in_flight / capacity
predicted_completion = latency_EWMA × load_factor
score_v2 = predicted_completion × (1 + 4.0 × error_EWMA)
```

Metadata eksik, geçersiz veya pozitif değilse kapasite `1` kabul ediliyor. Şu an metadata kapasiteleri Compose'taki `2/8/8` değerleriyle eşleşiyor. Isınma ve %10 keşif dalları bu skoru kullanmadan seçim yapıyor.

`predicted_completion` adı matematiksel olarak doğrulanmış bir tamamlanma tahmini anlamına gelmez; mevcut sezgisel skorun adıdır. v2'nin amacı, geçmişte hızlı olan fakat şu an dolan backend'e aşırı trafik göndermeyi azaltmaktır.

## 7. Benchmark sayıları ve kanıt sınırları

**Geçmişte gözlenen performans sayıları mevcut kaynaklarda bulunamadı.** Konuşmanın tam metni ve ham konsol çıktıları erişilebilir değil. Bu nedenle aşağıdaki yapılandırma değerleri sonuç gibi sunulmamalı; geçmiş ortalama/p95/p99/RPS rakamları tahmin edilmemelidir.

### Kaynak kodundan doğrulanan deney girdileri

| Parametre | Değer |
|---|---:|
| Toplam benchmark isteği | 300 |
| Varsayılan warm-up isteği | 50 (sonraki ölçümlerde sonuçlara dahil edilmez) |
| Azami istemci eşzamanlılığı | 20 |
| HTTP timeout | 10 saniye |
| Backend gecikmeleri | 40 / 70 / 120 ms |
| Backend kapasiteleri | 2 / 8 / 8 |
| Hata olasılıkları | 0 / 0 / 0 |
| Destination başına ısınma alt sınırı | 5 tamamlanmış örnek |
| Keşif olasılığı | %10 |
| Latency ve error EWMA alpha | 0.20 |
| Hata cezası katsayısı | 4.0 |

### Geri kazanılması gereken tarihsel sonuçlar

| Karşılaştırma | Ortalama / p95 / p99 | Throughput / başarı | Kanıt durumu |
|---|---|---|---|
| YARP baseline koşuları | Bilinmiyor | Bilinmiyor | İlk commit mesajında baseline ifadesi var; sonuç yok |
| v0 / v0.1 | Bilinmiyor | Bilinmiyor | Kod mevcut; sonuç yok |
| v1 | Bilinmiyor | Bilinmiyor | Kod mevcut; sonuç yok |
| v2 ve karşılaştırıldığı önceki yöntem | Bilinmiyor | Bilinmiyor | Kullanıcı mevcut regresyonu belirtiyor; çıktı yok |

Geçmiş sonuçlar elde edildiğinde her satıra commit, policy, istek/eşzamanlılık veya geliş hızı, backend ayarları, ısınma durumu, tekrar sayısı ve ham çıktı yolu eklenmeli. Yeni koşular “yeni ölçüm” diye tarihlemeli; eski konuşmanın sayıları yerine geçirilmemeli.

### Yeni ölçüm (2026-09-18) — v0/v1/v2 karşılaştırması, mevcut senaryo

**Kodda doğrulandı — gerçek koşuyla:** Commit `21775f4` (HEAD), kod değişmeden, `appsettings.json`'daki `LoadBalancingPolicy` alanı sırayla `RaspV0`/`RaspV1`/`RaspV2` yapılarak, her defasında gateway image'ı yeniden build edilip (`docker compose up --build -d gateway`) ve her tekrardan önce `docker compose restart gateway` ile EWMA state sıfırlanarak koşuldu. Senaryo: 300 istek, concurrency=20, timeout=10s (Benchmark projesinin sabit varsayılanları), backend1=40ms/cap2, backend2=70ms/cap8, backend3=120ms/cap8, `ERROR_RATE=0` üçünde de. Her policy için 5 tekrar alındı (toplam 15 koşu, 15/15 %100 başarı). Ham çıktılar `benchmarks/manual/variance/*.txt`, analiz `benchmarks/manual/SUMMARY-aşama-a-variance.md`.

**Metodolojik bulgu:** Her üç policy'de de policy geçişi/rebuild sonrası ilk koşu (run 1) diğer 4 koşudan sistematik olarak daha kötüydü (JIT/bağlantı ısınması — bu belgenin zaten öngördüğü bir sınırlama, bkz. yukarı). Bu yüzden karşılaştırma run 2-5 ortalamasına dayanıyor.

| Metrik | RaspV0 (run2-5 ort.) | RaspV1 (run2-5 ort.) | RaspV2 (run2-5 ort.) |
|---|---:|---:|---:|
| P50 latency | 148.4 ms | 164.5 ms | 101.7 ms |
| P95 latency | 310.9 ms | 345.4 ms | 195.0 ms |
| P95 aralığı | 288–330 ms | 309–371 ms | 170–239 ms |
| Throughput | 119.8 req/s | 103.3 req/s | 175.5 req/s |

**Sonuç:** Bu heterojen-kapasite senaryosunda (docker-compose'un varsayılan ayarları, projenin merkezi test senaryosu) **v2 regresyonu 5 tekrarlı ölçümle de doğrulanamadı** — tam tersine RaspV2, RaspV0 ve RaspV1'den tutarlı biçimde daha iyi performans gösterdi (RaspV2'nin en kötü koşusu, RaspV0/V1'in en iyi koşusundan daha iyiydi; gruplar arası fark grup içi varyanstan büyük). Kullanıcının aktardığı geçmiş regresyon iddiası bu ortamda/koşullarda yeniden üretilemedi; farklı bir senaryoya (homojen kapasite, hata oranı>0, sürekli aşırı yük) veya farklı bir kod/config durumuna ait olabilir — bu ayrı bir araştırma sorusu olarak açık bırakılıyor.

Yan bulgu: `ERROR_RATE=0` iken v1'in skor formülü matematiksel olarak v0'ın ham EWMA sıralamasıyla özdeş olmalıydı, ama v1 ölçümde hafifçe daha kötü çıktı (p95: 345 vs 311ms) — bu muhtemelen %10 rastgele keşif dalının örnekleme gürültüsü, 4-5 tekrarla kesin ayrım yapılamaz.

### Aşama B ölçüm kontrolü (2026-09-18)

Başarı tanımı ve exception kaydı değişikliğinden sonra RaspV2 aynı eski yöntemle beş kez yeniden koşuldu. Run 2-5 ortalaması p50 `102.67 ms`, p95 `191.26 ms`, p99 `255.77 ms`, throughput `174.15 req/s` oldu. Önceki RaspV2 serisine göre farklar koşul içi varyansın çok altındadır; bu senaryoda ölçüm değişikliğine bağlanabilecek anlamlı performans gerilemesi gözlenmedi. Ham çıktılar `benchmarks/manual/post-stage-b/`, ayrıntılı analiz `benchmarks/manual/SUMMARY-asama-b-metrics.md` altındadır.

### Mevcut ölçümün önemli sınırlamaları

- 300 isteklik tek koşu, özellikle p99 için az kuyruk ucu örneği içerir. Kod nearest-rank yüzdelik kullanır; 300 gözlemde p99 sıralı dizinin 297. gözlemidir.
- `Parallel.ForEachAsync` ile sabit eşzamanlılık kullanılıyor. Sistem yavaşladığında yeni istek üretimi de yavaşlar; bu deney sabit dış geliş hızındaki aşırı yük davranışını tek başına göstermez.
- İlk Aşama A/B sonuçları ayrı warm-up fazı olmayan eski runner ile üretildi; bu serilerde ilk koşu bu nedenle ayrıca raporlandı. Güncel runner varsayılan 50 warm-up isteğini sonuçlardan hariç tutuyor ve istek/warm-up/concurrency/target değerlerini ortam değişkenleriyle alabiliyor. Yeni metodolojiyle üretilecek sonuçlar eski seriyle doğrudan birleştirilmemeli.
- Gateway yeniden başlatılmazsa singleton EWMA durumu sonraki koşuya taşınabilir.
- Throughput tüm tamamlanan istemci sonuçlarını kapsıyor; başarılı throughput veya SLO goodput ile aynı değildir.
- Başarılı ve başarısız isteklerin gecikmeleri genel yüzdeliklerde birleşiyor. Hızlı hatalar ortalamayı iyileştiriyor gibi gösterebilir.
- Eksik/geçersiz JSON ve exception durumlarında queue değeri `0` yazılıyor. Bilinmeyen ölçüm gerçek sıfırdan ayrılmalı.
- Backend queue süresi tam milisaniyeye kırpılıyor; `QueueDelayMs > 0` gerçek beklemelerin tamamını yakalamaz.
- `activeRequestsAtStart`, kapasite kapısından geçtikten sonraki aktif sayıdır; bekleyen kuyruğun uzunluğunu göstermez.
- İstek başına Information logları ve skor metni üretimi karşılaştırmayı etkileyebilir. Politikalardaki log içerikleri de aynı değil.
- İş yükü `Task.Delay` tabanlı sentetik iş; gerçek CPU, veri tabanı veya karma iş yüklerine genellenemez.
- Rastgele keşif ve hata üretimi için tekrar oynatılabilir seed arayüzü yok.

## 8. Mevcut v2 regresyonu: önce açıklanması gereken problem

**Kullanıcının aktardığı geçmiş:** v2'de regresyon var. Hangi metrikte, hangi policy'ye karşı ve hangi koşullarda ne kadar kötüleştiği mevcut önizlemede yok. Kod okunarak regresyonun büyüklüğü veya nedeni kesinleştirilemez.

Öncelik yeni özellik eklemek değil, bu davranışı yeniden üretip nedenini ayırmaktır.

**Güncelleme (2026-09-18, kodda doğrulandı):** Projenin merkezi test senaryosunda (docker-compose'un varsayılan heterojen kapasite ayarları: 40ms/cap2, 70ms/cap8, 120ms/cap8, `ERROR_RATE=0`) regresyon 15 koşuluk (5×3 policy) tekrarlı ölçümle **yeniden üretilemedi** — bkz. Bölüm 7 "Yeni ölçüm" ve `benchmarks/manual/SUMMARY-aşama-a-variance.md`. RaspV2 bu senaryoda RaspV0/V1'den tutarlı biçimde daha iyi. Aşağıdaki hipotezler (özellikle #1, çifte ceza) hâlâ teorik olarak geçerli olabilir ama bu senaryoda pratikte gözlenen net etkiyi (kapasite farkındalığının getirdiği kazanç) geçersiz kılacak kadar baskın çıkmadı. Regresyonun farklı bir senaryoda (homojen kapasite, hata oranı>0, sürekli aşırı yük altında concurrency≫toplam kapasite) ortaya çıkıp çıkmadığı hâlâ açık bir soru.

### Koddan türeyen araştırma hipotezleri

1. **Kuyruk etkisinin iki kez cezalandırılması:** Gateway latency EWMA zaten kuyruk beklemesini içeriyor olabilir. Bunu ayrıca `1 + in_flight/capacity` ile çarpmak aşırı cezaya neden olabilir. Saf servis süresi ile uçtan uca süre ayrılarak sınanmalı.
2. **Gecikmiş geri besleme:** Ölçümler istek tamamlandıktan sonra güncelleniyor. Hızlı yük değişimlerinde eski örnekler kararları geciktirebilir.
3. **Eşzamanlı karar yığılması:** Birden fazla seçim benzer in-flight anlık görüntüsüne bakabilir. YARP sayacının yaşam döngüsü ve seçimle ilişkisi kurulu sürümde incelenmeli; atomik rezervasyon varmış gibi varsayılmamalı.
4. **Isınma ve keşfin yükü dikkate almaması:** Tamamlanmış örnek sayısına dayalı ısınma, çok sayıda bekleyen istek varken yeni istek göndermeye devam edebilir. Rastgele keşif de düşük kapasiteli backend'e ek yük getirebilir.
5. **Skorun model uyumsuzluğu:** Boş slot varken bile in-flight arttıkça ceza artıyor. Bunun backend'in gerçek hizmet davranışını ne kadar temsil ettiği ölçülmeli.
6. **Eski/gürültülü ölçüm ve log maliyeti:** Zamana bağlı eskime yok; getter'lar ayrı ayrı kilitleniyor, birleşik atomik snapshot sunulmuyor. Bunların sonuç üzerindeki etkisi kanıtlanmadan kök neden ilan edilmemeli.

### Regresyon inceleme sırası

1. Önce aynı commit ve ayarlarla bir baseline ile v2 koşusunu kaydet; mevcut davranışı dondur.
2. Her karar için zaman, seçim dalı, destination, latency/error EWMA, in-flight, kapasite ve skor bileşenlerini kontrollü tanı modunda yakala.
3. Backend kuyruk süresini ve gateway gecikmesini zaman ekseninde eşleştir; trafik bir backend'den diğerine salınıyor mu bak.
4. Ablation yap: v1, mevcut v2, yalnızca normalize yük ve ayrıştırılmış servis süresine dayalı aday modeli ayrı ayrı karşılaştır.
5. Keşif oranı, ısınma davranışı ve log seviyesi etkisini tek değişkenli deneylerle ayır.
6. Aday düzeltmeyi farklı yüklerde ve birden fazla tekrarda doğrula. Tek senaryoya göre katsayı ayarlayıp genel çözüm diye sunma.

Çıkış ölçütü: Yeniden üretilebilir bir regresyon senaryosu, ham veriye dayalı neden analizi, dar kapsamlı düzeltme veya mevcut formülün korunmasına ilişkin gerekçe ve bağımsız senaryolardaki etkisi.

## 9. Tekrar üretilebilir benchmark planı

### Mevcut prototipi başlatma — bu belgede çalıştırılmadı

Aşağıdaki komutlar depo kökünde, Docker ve uygun .NET SDK kurulu olduğunda kullanılacak başlangıç akışıdır:

```powershell
docker compose up --build -d
Invoke-RestMethod http://localhost:5101/health
Invoke-RestMethod http://localhost:5102/health
Invoke-RestMethod http://localhost:5103/health
Invoke-RestMethod http://localhost:5200/api/work
dotnet run --project Benchmark/RaspLb.Benchmark/RaspLb.Benchmark.csproj -c Release
Invoke-RestMethod http://localhost:5200/debug/metrics
```

Sağlık ve örnek iş isteği başarılı olmadan benchmark'a başlanmamalı. Örnek gateway isteği bile EWMA durumunu değiştirir; temiz başlangıç deneylerinde readiness/probe ve reset sırası standartlaştırılmalı. Mevcut kodda benchmark hedefi, istek sayısı ve eşzamanlılık sabit; aşağıdaki gelişmiş parametreler henüz uygulanmış değildir.

### Geliştirilecek deney sözleşmesi

- CLI veya yapılandırmayla hedef, süre, warm-up, eşzamanlılık/geliş hızı, seed ve senaryo seçimi.
- Baseline adayları: RoundRobin, LeastRequests, PowerOfTwoChoices; policy isimleri ve davranışları kurulu YARP sürümünde doğrulanarak yapılandırılmalı.
- Her policy için aynı kaynak limitleri, bağlantı ayarları, timeout, log seviyesi, veri ve backend senaryosu.
- Soğuk başlangıç ile ısınmış kararlı durum ayrı raporlanmalı. Koşular arasında tanımlı reset uygulanmalı.
- Sabit eşzamanlılık testinin yanına zamanlanmış geliş hızına sahip açık döngü testi eklenmeli. Planlanan gönderim ile gerçek gönderim arasındaki gecikme de kaydedilmeli.
- Önce birkaç bağımsız tekrar ile varyans görülmeli; nihai tekrar sayısı ve süre, beklenen etki ve güven aralığına göre belirlenmeli. Başlangıç planı olarak en az 5 tekrar kullanılabilir; bu sayı istatistiksel yeterlilik garantisi değildir.
- Policy sırası rastgeleleştirilmeli veya dengelenmeli. Load generator'ın kendisi darboğaz mı kontrol edilmeli.
- Ham kayıtlar JSON/CSV olarak saklanmalı; yalnızca ekran görüntüsü yeterli değil.

Önerilen çıktı düzeni:

```text
benchmarks/results/<run-id>/
  manifest.json       # commit, ortam, policy, senaryo, ayarlar, zaman
  requests.csv        # mantıksal istek, attempt, durum, latency, backend, queue
  summary.json        # metrikler, örnek sayıları, eksik veri sayıları
  gateway.log
  report.md
```

Ortam kaydı; CPU, RAM, işletim sistemi, Docker/SDK/runtime sürümleri, container limitleri ve image kimliklerini içermeli. Timeout ve iptaller kayıttan atılmamalı. Histogram veya ham örnekler saklanmalı; farklı koşuların p99 değerlerini basitçe ortalayıp birleşik p99 diye sunmamalı.

### Senaryo matrisi

| Senaryo | Değişken | Araştırılan davranış |
|---|---|---|
| Homojen sağlıklı | Eşit gecikme/kapasite | Adaptif kararın ek maliyeti |
| Heterojen gecikme | Farklı işlem süreleri | v0/v0.1 yönlendirmesi |
| Heterojen kapasite | Mevcut 2/8/8 ve alternatifleri | v2 ve kuyruk dengesi |
| Hata üreten hızlı backend | Artan hata olasılığı | v1 cezası ve goodput |
| Ani yavaşlama / iyileşme | Zaman içinde gecikme değişimi | Uyum ve yeniden keşif |
| Backend kaybı / dönüşü | Süreç veya bağlantı kesintisi | Health, hata ve toparlanma |
| Burst ve sürekli aşırı yük | Artan geliş hızı | Kuyruk, SLO ve shedding |
| Karışık maliyetli işler | Kısa/uzun iş dağılımı | Tek EWMA'nın sınırları |

Dinamik senaryo değiştirme altyapısı mevcut kodda yok; kontrollü deney desteği olarak geliştirilmeli.

## 10. Uçtan uca yol haritası

Aşağıdaki aşamalar önerilen plandır; v3/v4 gibi tarihsel sürüm etiketleri değildir.

### Aşama A — Mevcut durumu sabitle ve v2'yi açıkla

Teslimatlar: Çalıştırma rehberi, sürüm/ayar manifesti, baseline çıktıları, regresyon raporu, gerekiyorsa v2 düzeltmesi ve ilgili davranış testleri. İlk iş benchmark başarısı iddia etmek değil, ölçüm tanımlarını tutarlı hale getirmektir.

Tamamlanma ölçütü: Başka biri aynı senaryoyu çalıştırıp aynı yönde etkiyi gözleyebilmeli; sapma aralığı raporda bulunmalı.

### Aşama B — Ölçüm ve güvenilirlik temeli

**Durum (2026-09-18): Tamamlandı.** Gateway ve benchmark 2xx kullanıcı başarısı tanımında birleşti. Gateway; HTTP client/server hatası, YARP forwarder hatası, timeout, iptal ve beklenmeyen exception sonuçlarını ayrı kaydediyor. Debug çıktısı tutarlı snapshot kullanıyor. Başarı, 404, 503, istemci iptali, connection-refused ve YARP timeout kontrollü entegrasyon senaryolarıyla doğrulandı. Sağlıklı istek yolunda yapılan beş tekrarlı performans kontrolünde anlamlı regresyon görülmedi.

Gateway başarı tanımı ile istemci başarısını ayırıp ilişkilendir. Timeout, transport hatası, iptal, uygulama 5xx'i ve reddetmeyi ayrı kaydet. Ölçüm middleware'inin exception yolunda kayıt üretmemesini ve `await next()` sonrasındaki durum okumasını incele. Destination kimliği çoklu cluster'da benzersiz mi doğrula; store şu an yalnızca `DestinationId` ile anahtarlanıyor.

Kontrollü health davranışı, konfigürasyon doğrulama, metrik snapshot'ı, karar açıklaması ve gerekirse örnek eskimesi ekle. Sentetik backend kapasite/hata parametrelerinin geçerliliğini doğrula.

Tamamlanma ölçütü: Başarı, hata, timeout ve iptal senaryolarında sayaçlar tutarlı; kaynak ve kapasite serbest bırakma davranışı doğrulanmış olmalı.

### Aşama C — Retry: sınırlı ve deadline farkında kurtarma

**Durum (2026-09-18): İlk dar kapsamlı sürüm uygulandı.** GET/HEAD isteklerinde yanıt başlamadan oluşan seçili YARP forwarder hataları, başarısız destination dışlanarak en fazla bir kez yeniden deneniyor. Yeni attempt için zaman bütçesi, sistem geneli token bucket ve `/debug/retries` sayaçları var. Connection-refused senaryosunda 5 başarısız backend denemesinin tamamı alternatif destination'da kurtarıldı; POST, zaman bütçesi ve sistem bütçesi korumaları ayrıca doğrulandı. HTTP 500/503 retry'ı ve ilk attempt'i de kesen kesin uçtan uca deadline henüz uygulanmadı. Ayrıntılar `benchmarks/manual/SUMMARY-asama-c-retry.md` dosyasında.

Amaç geçici arızadan kurtarmak; her hatayı tekrar etmek değildir. Mantıksal kullanıcı isteği ile backend denemesi ayrı kimlik ve metrik taşımalı.

- Yalnızca izin verilen hata sınıfları ve güvenle yeniden oynatılabilen istekler için retry.
- Yazma işlemlerinde idempotency/replay sözleşmesi olmadan otomatik tekrar yok.
- Azami attempt sayısı, toplam zaman bütçesi ve sistem genelinde retry budget.
- Uygun backoff/jitter ve mümkün olduğunda farklı sağlıklı destination.
- Yanıt istemciye aktarılmaya başladıktan sonraki retry sınırlarının açık tanımı.
- Her attempt'in hata ve latency kaydı; kullanıcıya görünen nihai sonucun ayrıca kaydı.

YARP'ın policy arayüzüne retry davranışı sığdırılmamalı; forwarder ve istek gövdesi yaşam döngüsü incelendikten sonra doğru katman seçilmeli.

Tamamlanma ölçütü: Aynı arıza senaryosunda başarı kazanımı ve ek trafik birlikte gösterilmeli; aşırı yükte retry fırtınası oluşmamalı.

### Aşama D — SLO ve deadline farkındalığı

SLO, hizmetin kullanıcıya verdiği ölçülebilir sözleşmedir. Örneğin “isteklerin belirli yüzdesi belirli sürede başarıyla tamamlanır” biçiminde tanımlanır; kesin eşik ürün hedefi ve baseline sonuçlarıyla belirlenecek, burada geçmiş hedefmiş gibi sayı verilmeyecektir.

İstek sınıfına göre deadline, kalan zaman bütçesi ve goodput ölçümü ekle. Retry için kalan süreyi kullan. Latency yüzdeliği ile başarı oranını birlikte raporla; hızlı reddetmeleri başarılı SLO sonucu sayma. Tahminler kararsızsa açık fallback davranışı tanımla.

Tamamlanma ölçütü: SLO pay/payda tanımları belgelenmiş, deadline aşan ve reddedilen işler görünür, kabul edilen işlerin zamanında başarı oranı ayrıca ölçülmüş olmalı.

### Aşama E — Brownout: isteğe bağlı işi azalt

Brownout, yük yükseldiğinde çekirdek işlevi koruyarak isteğe bağlı pahalı parçaları geçici olarak azaltmaktır. Bunun için backend'in bir hizmet kalitesi sözleşmesi sunması gerekir. Mevcut tek `Task.Delay` işini yalnızca kısaltmak, gerçek uygulama kalitesindeki değişimi tek başına modellemez.

Önerilen demo: Zorunlu yanıt üretimi ile opsiyonel zenginleştirmeyi ayır; tam ve azaltılmış modun maliyetini, çıktı farkını ve kullanım oranını kaydet. Hysteresis ve minimum bekleme süresiyle modların sürekli açılıp kapanmasını önle. İstemcinin gönderdiği kalite kontrol bilgisini güven sınırında doğrula veya gateway tarafından yeniden üret.

Tamamlanma ölçütü: Hangi özelliklerin azaltıldığı anlaşılır olmalı; SLO kazanımı ile kalite kaybı birlikte raporlanmalı. Brownout normal başarı gibi gizlenmemeli.

### Aşama F — Load shedding ve admission control

Toplam kapasite yetersizse bazı işleri erken reddederek kabul edilen işlerin hizmetini koru. Sınırlı eşzamanlılık ve sınırlı bekleme kuyruğu kullan; sınırları yük testleriyle belirle. Uygun 429/503 ve gerektiğinde `Retry-After` semantiğini tasarla; istemci retry'sinin tekrar yük yaratabileceğini hesaba kat.

Öncelik sınıfları varsa starvation ve adaleti izle. Reddedilen istek sayısını başarı oranından saklama. Brownout, retry ve shedding sıralamasını açık bir karar akışıyla belirt.

Tamamlanma ölçütü: Sürekli aşırı yükte kuyruk/bellek büyümesi sınırlı, kabul edilen işlerin gecikmesi kontrol altında ve toplam reddetme oranı görünür olmalı.

### Aşama G — Birleşik dayanıklılık deneyleri

Yönlendirme + retry + SLO + brownout + shedding birlikte çalışırken ayrı ayrı kazançların korunup korunmadığını ölç. Her özelliği kapatarak katkısını göster. Arıza sonrasında normale dönüşü, kapasite artışını ve birden fazla gateway örneğinde yerel metriklerin etkisini incele.

Tamamlanma ölçütü: Normal yük, kısa burst, sürekli aşırı yük ve arıza/iyileşme için açıklanabilir sonuçlar; limitler ve başarısız olunan durumlar belgelenmiş olmalı.

## 11. NGINX'e taşıma ve karşılaştırma hedefi

Konuşma başlığında NGINX geçmesine rağmen mevcut depo **ASP.NET Core + YARP prototipidir**. NGINX konfigürasyonu, modülü veya port edilmiş RASP kodu incelenen dosyalarda yok.

Önerilen sıra:

1. Önce YARP üzerinde algoritmanın davranışını ve deney sözleşmesini sabitle.
2. Aynı backend'lerle standart NGINX baseline'ı ekle. Bu yalnızca karşılaştırmadır; RASP portu sayılmaz.
3. Port için hedef sürümü ve uygulama yolunu seç: uygun bir modül/uzantı yaklaşımı veya Lua destekli bir dağıtım değerlendirmesi. Güncel resmi belgelerle yetenekler ve lisans/dağıtım sınırları o aşamada doğrulanmalı.
4. Destination kimliği, kapasite, ölçüm, hata sınıfları, EWMA ve keşif semantiğini yazılı sözleşmeye dönüştür.
5. Worker'lar arası durum paylaşımı, atomik sayaçlar, reload, backend üyelik değişimi ve ölçüm maliyetini tasarla. Tek süreçteki .NET singleton'ı aynı şekilde taşınmış varsayılmamalı.
6. Deterministik olay dizileriyle skor ve seçim eşdeğerliğini sınayıp ardından aynı benchmark matrisini çalıştır.

İki ayrı soru cevaplanmalı: “Aynı YARP altyapısında hangi algoritma daha iyi?” ve “Aynı hizmet koşullarında farklı proxy uygulamalarının uçtan uca maliyeti ne?” YARP ile NGINX arasındaki farkı yalnızca yük dengeleme algoritmasına atfetme.

Tamamlanma ölçütü: Çalıştırılabilir NGINX tabanlı uygulama, sürüm sabitlemesi, yapılandırma, semantik farklar belgesi ve karşılaştırmalı ham sonuçlar. Port uygulanamazsa sınırlama ve gerekçeli alternatif de açık teslimat olmalı.

## 12. Portfolyoda nasıl fark yaratacak?

Projenin güçlü hikâyesi, “Docker'da üç servis ve bir proxy çalıştırdım” seviyesinin ötesinde; bir performans problemini kurup ölçmek, yanlış çıkan varsayımı göstermek ve kanıtla geliştirmektir.

Anlatının omurgası şu olabilir:

> Heterojen backend'lerde gecikmeye dayalı yönlendirmenin sınırlarını araştırdım. Isınma, keşif ve hata farkındalığı ekledim. Kapasiteyi kullanan v2 modelinin regresyonunu tekrar üretip nedenini inceledim. Ardından retry bütçesi, SLO, brownout ve admission control ile kullanıcıya zamanında ulaşan başarılı yanıtları koruyan bir prototip geliştirdim. Sonuçları ham veriler, baseline'lar ve yeniden çalıştırılabilir senaryolarla sundum.

Bu paragraf gelecekteki tamamlanmış proje anlatısıdır. Bugünkü CV'de yalnızca gerçekten tamamlanan kısmı geçmiş zamanla kullan. Regresyon analizi ve sonraki özellikler bitmeden bunları yapılmış gibi yazma.

Somut ayrışma alanları:

- **Dağıtık sistemler:** Geri besleme gecikmesi, heterojen kapasite, arıza ve toparlanma.
- **Performans mühendisliği:** Kuyruk, kuyruk ucu gecikmesi, goodput ve kontrollü deneyler.
- **Güvenilirlik:** Deadline, retry budget, kontrollü kalite düşürme ve yük reddetme.
- **Mühendislik disiplini:** Küçük değişiklikler, anlamlı testler, karar kayıtları ve açık sınırlamalar.
- **Taşınabilir tasarım:** Algoritma sözleşmesini YARP ve NGINX uygulamalarından ayırabilme.

### Nihai teslimat listesi

| Teslimat | Beklenen içerik |
|---|---|
| Ana README | Problem, hızlı başlangıç, mevcut durum, demo ve sonuç bağlantıları |
| Bu vizyon belgesi | Amaç, kanıt sınırları, aşamalar ve Claude devralma rehberi |
| Mimari belgesi | Veri akışı, metrik yaşam döngüsü, karar ve hata akışları |
| Algoritma/ADR belgeleri | Formüller, varsayımlar, reddedilen alternatifler ve nedenleri |
| Benchmark runner | Parametreli çalıştırma, senaryolar, reset ve ham kayıt |
| Sonuç raporu | Baseline karşılaştırmaları, tekrarlar, güven aralığı ve caveat'ler |
| v2 regresyon incelemesi | Tetikleyici, kanıt, kök neden/hipotez ayrımı, önce-sonra |
| Davranış testleri | Hata, iptal, retry limiti, kapasite, deadline, iyileşme ve aşırı yük |
| Gözlemlenebilirlik | Skor bileşenleri, kuyruk, goodput, retry, brownout ve reddetme |
| NGINX çalışması | Baseline ve sonrasında port, eşdeğerlik ve maliyet analizi |
| Kısa demo | Tekrar çalıştırılabilir normal yük → arıza → aşırı yük → iyileşme akışı |
| Portfolyo vaka yazısı | Problem, hipotez, deney, yanlış çıkan varsayım, çözüm, sınırlamalar |

Görsellerde ortalama latency tek başına kullanılmamalı. Zaman serisi, latency dağılımı, yük arttıkça goodput, backend trafik payı ve kuyruk grafikleri anlatıyı güçlendirir. Her grafik ham sonuç dosyasına bağlanmalı.

## 13. Claude için çalışma talimatı

Bu dosyayı kullanıcı hedefinin ve hazırlanan planın açıklaması olarak oku. Canlı depo ve yeni kullanıcı talimatlarıyla çelişirse farkı belirt; eski belgeyi körü körüne uygulama.

1. Önce depo kökündeki ve ilgili alt dizinlerdeki `AGENTS.md` / `CLAUDE.md` talimatlarını varsa oku. `git status` ile kullanıcının değişikliklerini belirle ve koru.
2. Bu dosyanın 5. bölümündeki kaynakları gerçekten aç. Policy kaydı, seçili konfigürasyon, backend parametreleri ve benchmark'ın aynı senaryoyu tarif ettiğini doğrula.
3. Mevcut commit'i ve tarihsel farkları incele. v0.1'i ayrı sınıf sanma; başlıktaki NGINX'i uygulanmış bileşen sanma.
4. “Doğrulanan mevcut davranış / aktarılan fakat kanıtsız geçmiş / önerilen değişiklik” ayrımını kısa bir durum notuyla koru.
5. Eski konuşma veya benchmark çıktısı elde edilirse sayıları bağlamıyla bu belgenin 7. bölümüne işle. Kaynağı yoksa sayı üretme.
6. Kod değişikliğinden önce baseline'ı kaydet ve v2 regresyonunu yeniden üretmeye çalış. Yeniden üretilemiyorsa koşulları ve belirsizliği raporla; hayali kök nedenle düzeltme yapma.
7. İlk değişikliği tek soruna odakla. Algoritma formülü, retry ve benchmark yöntemini aynı değişiklikte karıştırma.
8. Testleri anlamlı davranışlara bağla: hiçbir destination olmaması, tek destination, ısınma, geçersiz kapasite, hata, iptal, kuyruk ve toparlanma. Rastgeleliğe bağlı kırılgan testlerden kaçın.
9. Sonuçları aynı koşullarda karşılaştır; iyileşmeyen metrikleri de yaz. Başarılı test ile gerçek performans kanıtını birbirine karıştırma.
10. Her aşama sonunda değişen davranışı, nasıl doğrulandığını, açık sınırları ve sonraki araştırma sorusunu belgeye yansıt.

İlk somut görev: **Mevcut v2 davranışını ve benchmark ölçüm sınırlarını doğrula; regresyonu tekrar üretmek için minimum deney setini oluştur; yalnızca elde edilen kanıta göre dar kapsamlı bir değişiklik öner veya uygula.**

## 14. Projenin tamamlanma tanımı

Proje; başka bir geliştirici temiz ortamda çalıştırabildiğinde, baseline ve RASP karşılaştırmalarını yeniden üretebildiğinde, v2'nin sınırları açıklandığında, retry/SLO/brownout/shedding davranışları ölçüldüğünde ve NGINX hedefi somut uygulama veya gerekçeli sonuçla kapatıldığında portfolyo teslimatı açısından tamamlanmış sayılabilir.

En değerli çıktı yalnızca çalışan kod değil; hangi koşulda hangi kararın neden işe yaradığını, nerede yetersiz kaldığını ve bu sonuca hangi deneyle ulaşıldığını gösteren incelenebilir bir mühendislik çalışmasıdır.
