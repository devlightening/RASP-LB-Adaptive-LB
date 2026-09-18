# Aşama C — Sınırlı Retry doğrulaması (2026-09-18)

Bu aşama RaspV2 algoritmasının yeni bir sürümü değildir. Yük dengelemenin önünde çalışan bağımsız bir dayanıklılık katmanıdır.

## Uygulanan sözleşme

- Yalnızca GET ve HEAD istekleri tekrar edilebilir.
- Yalnızca YARP'ın yanıt başlamadan bildirdiği `Request`, `RequestTimedOut`, `RequestCreation` ve `ResponseHeaders` forwarder hataları tekrar edilebilir.
- Varsayılan azami attempt sayısı 2'dir: ilk deneme + en fazla bir retry.
- Başarısız destination aynı mantıksal isteğin sonraki denemesinden çıkarılır; yük dengeleme kalan destination'lar üzerinde yeniden çalışır.
- Varsayılan retry zaman bütçesi 5 saniyedir. Bu değer ilk denemeyi zorla kesen kesin deadline değil, yeni attempt başlatma sınırıdır.
- Sistem geneli token bucket varsayılan olarak saniyede 20 token ve 20 burst kapasitesi kullanır.
- Yanıt başladıysa, güvenli alternatif destination yoksa veya bütçe yoksa retry yapılmaz.
- HTTP 500/503 uygulama yanıtları bu ilk sürümde retry edilmez; varsayılan YARP forwarder bu yanıtı istemciye aktarmaya başlamış olabilir.

## Kontrollü connection-refused senaryosu

İkinci bir gateway örneğinde destination'lar `localhost:5101`, kapalı `localhost:1`, `localhost:5103` olarak ayarlandı.

| Durum | Mantıksal istek | Sonuç | Retry metriği |
|---|---:|---|---|
| Retry öncesi sınıflandırma testi | 30 GET | 24×200, 6×502 | Retry yok |
| Retry açık | 30 GET | **30×200** | 5 attempt, 5 başarı |
| POST güvenlik testi | 15 POST | 10×405, 5×502 | 0 attempt, `unsafeMethodRejected=5` |

Rastgele/ısınma seçimi nedeniyle kapalı destination'a düşen istek sayısı iki GET koşusunda 6 ve 5 olarak farklıydı. Bu tablo retry'ın mutlak performans karşılaştırması değil, davranış doğrulamasıdır.

Destination metrikleri retry attempt'lerini ayrı backend denemeleri olarak saydı: başarısız backend için `forwarderFailures=5`, başarılı alternatifler için toplam 5 ek success örneği oluştu. `/debug/retries` ise 30 mantıksal isteği ve 5 ek denemeyi ayrı gösterdi.

## Koruma testleri

### Zaman bütçesi

`TimeBudgetMs=1000` yapıldı. Connection-refused denemesi yaklaşık 2 saniye sürdüğü için 15 GET'in kapalı destination'a düşen 5'i retry edilmedi:

```text
retryAttempts=0
timeBudgetRejected=5
```

### Sistem geneli retry bütçesi

`TokensPerSecond=1`, `BurstCapacity=1` ile 15 eşzamanlı GET gönderildi:

```text
Successful=11
Failed=4
retryAttempts=1
retrySuccesses=1
budgetRejected=4
```

Bu sonuç, aynı anda oluşan arızaların sınırsız retry fırtınasına dönüşmediğini gösteriyor.

### YARP timeout ve uygulama 503 ayrımı

Yanıt vermeyen TCP destination ve 1 saniyelik YARP activity timeout ile 15 GET gönderildi. Beş timeout `timeouts=5` olarak kaydedildi, beşi de alternatif destination'da kurtarıldı ve istemci 15×200 aldı (`retryAttempts=5`, `retrySuccesses=5`).

`ERROR_RATE=1` olan kontrollü backend ile ayrı bir 15 GET koşusunda 10×200 ve 5×503 alındı. Gateway `httpServerErrors=5` kaydetti; retry metriği sıfır kaldı. Bu, ilk sürümün yalnızca forwarder/transport hatalarını tekrar ettiği ve uygulama yanıtlarını bilinçli olarak tekrar etmediği sözleşmesini doğruladı.

## Sağlıklı Docker senaryosu

Varsayılan üç sağlıklı backend ile 50 warm-up + 300 ölçülen istek:

```text
Success Rate : 100.00%
P50          : 81.16 ms
P95          : 142.25 ms
P99          : 147.11 ms
Throughput   : 197.14 req/sec
Queue Rate   : 51.33%
```

Retry metrikleri `logicalRequests=350`, `retryAttempts=0` gösterdi. Sağlıklı senaryoda gereksiz ek trafik üretilmedi. Ham çıktı `benchmarks/manual/stage-c/healthy-retry-enabled-run1.txt` dosyasındadır. Bu tek koşu, performans üstünlüğü kanıtı olarak kullanılmamalıdır.

## Açık işler

- Retry kapalı/açık aynı arıza zaman çizelgesinde birden fazla tekrar ve ham veriyle başarı kazanımı, ek attempt yükü ve latency maliyetini karşılaştırmak.
- Uygulama 503 yanıtlarını retry etmek istenirse yanıtın istemciye aktarılmasını erteleyen doğrudan forwarder/response-transform tasarımını ayrı ele almak.
- Kesin uçtan uca deadline ve istemci iptali semantiğini Aşama D'de tamamlamak.
