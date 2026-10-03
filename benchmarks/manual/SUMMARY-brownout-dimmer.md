# Brownout: açma/kapama yerine dimmer (2026-10-03)

## Sorun

Orta yükte (220 istek/sn; tam modun ~160/sn kapasitesinin üstünde, brownout'un ~230/sn kapasitesinin altında) açma/kapama brownout salınıyordu: aç → kuyruk erir → 2 sn sakin → kapat → kuyruk dolar. Backend3 75 sn'de 13 kez brownout'a girip çıktı; saniyelik p95 122-222 ms arasında dalgalandı.

## Ne değişti

`BrownoutState` artık bir dimmer (Klein ve ark., *Brownout*, ICSE 2014). Her istek zenginleştirme adımını θ olasılığıyla yapıyor. Kontrolcü θ'yı yumuşatılmış kuyruk bekleme sinyaline göre ayarlıyor:
- Sinyal ~500 ms'lik zaman sabitiyle yumuşatılıyor. Hedef bekleme 40 ms (`BROWNOUT_QUEUE_WAIT_MS`).
- θ, sinyal hedefin üstündeyken saniyede en fazla 2,0, altındayken en fazla 0,5 değişiyor.
- "Brownout aktif" bilgisi sadece raporlama için: θ < 0,9 olunca aktif, θ ≥ 0,99'a dönünce kapalı sayılıyor.

Panelde "Mod" sütunu "Zenginleştirme %θ" oldu. Saat ve rastgele sayı üreteci dışarıdan verilebildiği için birim testleri deterministik ve `Thread.Sleep` kullanmıyor (6 test; toplam 23).

## Ölçüm (`benchmarks/load/midload.js`, 220 istek/sn, 52 sn'lik sabit faz, her biri tek koşu)

| | Açma/kapama | Dimmer (kazanç 2,0 / 0,5) | Dimmer (kazanç 0,6 / 0,25) |
|---|---|---|---|
| Backend3 brownout'a giriş | 13 | 5 | 2 |
| Backend3'te zenginleştirme yapılan istek | %35 | %44 | %42 |
| Saniyelik p95: ort. / std. sapma | 171 / 34,7 ms | 184 / 29,7 ms | 191 / 34,9 ms |

Dimmer (2,0 / 0,5) kaldı: daha az giriş-çıkış, daha çok zenginleştirme, biraz daha az dalgalanma. Ortalama p95'teki artış daha çok zenginleştirme yapılmasının bedeli. Düşük kazanç salınımı gidermedi, sadece θ'nın 0,1 ile 0,9 arasında salınmasına yol açtı.

## Bulgu: asıl salınım kaynağı yönlendirme

Saniyelik örnekler ~5 sn periyotlu, sistem genelinde bir döngü gösteriyor. Backend3'te θ yükselince EWMA gecikmesi 125 ms'den 210 ms'ye çıkıyor ve v2 trafiği backend2'ye kaydırıyor. Backend2'nin kuyruğu 2'den 9'a, EWMA'sı 100 ms'den 155 ms'ye çıkıyor ve v2 trafiği geri veriyor. v2 her istekte en düşük skorlu backend'i deterministik olarak seçiyor, bu yüzden EWMA güncellenene kadar trafik tek backend'e yığılıyor ("herding"). Bilinen çözüm, iki rastgele aday arasından seçmek ("power of two choices") ya da skorla orantılı rastgele seçim. Bu ayrı bir iş olarak bekliyor.

## Hipotez sınandı: salınım yönlendirmeden değil (2026-10-03)

`RaspV3LoadBalancingPolicy` eklendi: v2 skoru ve "power of two choices" seçimi. Global en düşük skor yerine iki rastgele adaydan iyi olanı seçiyor. Politika `LB_POLICY` ortam değişkeniyle seçilebiliyor; varsayılan RaspV2.

Aynı build, sadece politika farklı, her biri tek koşu:

| | v2 | v3 (P2C) |
|---|---|---|
| Saniyelik p95 std. sapma (midload) | 29,2 ms | 28,2 ms |
| İstek dağılımı b1 / b2 / b3 (midload) | 3.728 / 8.037 / 4.234 | 3.717 / 8.004 / 4.278 |
| Backend3 θ ort. / std. sapma | 0,5 / 0,4 | 0,3 / 0,3 |
| Backend2 kuyruk ort. / std. sapma | 5,9 / 2,6 | 5,0 / 2,5 |
| priority.js critical / normal p95 | 157 / 240 ms | 155 / 244 ms |

**Sonuç: herding hipotezi yanlış.** İstek dağılımı ve dalgalanmalar iki politikada aynı. Backend3'ün θ değeri v3'te de aynı ~5 sn periyotlu testere dişini çiziyor. Salınım, brownout kontrolcüsünün kendi döngüsünden geliyor: θ yükseliyor → işlem süresi uzuyor → kuyruk büyüyor → yumuşatılmış sinyal ~500 ms gecikmeyle hedefi aşıyor → θ hızla düşüyor. Gecikmeli ölçümle çalışan saf integral kontrolcünün tipik limit döngüsü.

v3'ü varsayılan yapmak için kanıt yok; varsayılan v2 kaldı. Muhtemel düzeltmeler: sinyal gecikmesini azaltmak (τ 500 → ~150 ms), oransal terim eklemek (PI) ya da θ'yı sadece eşik bandının dışında değiştirmek.

Yan düzeltme: v2, log seviyesi kapalıyken bile her istekte `BuildMetricsLog` ile log metnini oluşturuyordu; artık `IsEnabled` kontrolü var.
