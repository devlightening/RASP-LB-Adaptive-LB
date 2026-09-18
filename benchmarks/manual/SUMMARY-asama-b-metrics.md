# Aşama B — Ölçüm katmanı performans kontrolü (2026-09-18)

Amaç: Gateway ölçüm middleware'inde başarı tanımının `2xx` olarak düzeltilmesi ve exception kaydının eklenmesinden sonra, sağlıklı istek yolunda ölçülebilir bir performans gerilemesi olup olmadığını kontrol etmek.

## Yöntem

- Policy: `RaspV2`
- 300 ölçülen istek, concurrency=20
- Backend'ler: `40ms/cap2`, `70ms/cap8`, `120ms/cap8`
- Üç backend'de de `ERROR_RATE=0`
- Her koşudan önce gateway restart edildi ve readiness için bir istek gönderildi.
- Beş koşu alındı. Önceki Aşama A metodolojisiyle karşılaştırılabilmesi için run 2-5 ortalaması kullanıldı.
- Ham çıktılar: `benchmarks/manual/post-stage-b/RaspV2_post_stage_b_run*.txt`

Bu ölçüm, benchmark runner'a açık warm-up fazı eklenmeden hemen önce alınmıştır. Yeni warm-up kullanan gelecekteki sonuçlar bu eski seriyle doğrudan birleştirilmemelidir.

## Sonuçlar

| Run | P50 | P95 | P99 | Throughput | Queue rate |
|---:|---:|---:|---:|---:|---:|
| 1 | 106.18 ms | 331.21 ms | 434.74 ms | 155.85 req/s | 48.67% |
| 2 | 100.05 ms | 188.28 ms | 250.36 ms | 175.93 req/s | 63.00% |
| 3 | 101.19 ms | 201.77 ms | 270.60 ms | 177.00 req/s | 56.00% |
| 4 | 104.82 ms | 198.78 ms | 242.37 ms | 170.37 req/s | 58.67% |
| 5 | 104.61 ms | 176.21 ms | 259.75 ms | 173.32 req/s | 64.00% |
| **Run 2-5 ort.** | **102.67 ms** | **191.26 ms** | **255.77 ms** | **174.15 req/s** | **60.42%** |

## Önceki RaspV2 serisiyle karşılaştırma

| Metrik | Aşama A run 2-5 | Ölçüm değişikliği sonrası | Fark |
|---|---:|---:|---:|
| P50 | 101.7 ms | 102.67 ms | +0.97 ms |
| P95 | 195.0 ms | 191.26 ms | -3.74 ms |
| Throughput | 175.5 req/s | 174.15 req/s | -1.35 req/s |

Farklar önceki koşul içi varyansın çok altındadır. Bu senaryoda ölçüm middleware'i değişikliğine bağlanabilecek anlamlı bir performans gerilemesi gözlenmedi.

## Hata sınıflandırması doğrulaması

Sonraki Aşama B değişikliğinde YARP'ın `IForwarderErrorFeature` bilgisi okunarak sonuçlar `2xx`, HTTP client/server hatası, forwarder hatası, timeout, iptal ve beklenmeyen exception olarak ayrıldı.

- Backend2 durdurulup istemci timeout'u 3 saniye yapıldığında beş başarısız deneme `cancellations=5` olarak kaydedildi. Bu test, YARP'ın her proxy hatasında dışarı exception fırlatmadığını da gösterdi.
- `/api/not-found` isteği 404 döndürdü; `httpClientErrors=1`, toplam `failures=1`, `destinationFailures=0` oldu. Böylece istemci kaynaklı 4xx, backend'in yönlendirme hata EWMA'sını yükseltmedi.
- İkinci bir yerel gateway örneğinde backend2 adresi kapalı `127.0.0.1:1` portuna yönlendirildi. 30 isteğin 6'sı 502 oldu ve destination için `forwarderFailures=6`, `destinationFailures=6`, `ewmaErrorRate=100%` kaydedildi; beklenen connection-refused yolu dışarı exception fırlatmadan doğru sınıflandırıldı.
- Yanıt vermeyen kontrollü TCP destination ve 1 saniyelik YARP activity timeout ile beş istek `timeouts=5` olarak kaydedildi.
- `ERROR_RATE=1` olan kontrollü backend'e düşen beş istek 503 döndü; `httpServerErrors=5`, `destinationFailures=5` ve `ewmaErrorRate=100%` kaydedildi.
- Küçük doğrulama koşusunda 5 warm-up + 10 ölçülen istek gönderildi; benchmark yalnızca 10 isteği raporlarken gateway toplam 15 örnek kaydetti. Warm-up sonuçlardan başarıyla ayrıldı.

Başarı, 404, 503, connection-refused, YARP timeout ve istemci iptali yolları kontrollü entegrasyon senaryolarıyla doğrulandı. Pipeline'dan gerçekten kaçan beklenmeyen exception için sayaç bulunuyor; bu yol üretim davranışını yapay biçimde bozacak test middleware'i eklenmediği için yalnızca derleme düzeyinde doğrulandı.
