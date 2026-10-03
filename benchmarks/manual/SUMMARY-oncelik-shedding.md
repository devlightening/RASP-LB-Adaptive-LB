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
