// Açık döngülü (open-loop) aşırı yük testi.
//
// Kapalı döngülü benchmark (RaspLb.Benchmark) sabit sayıda istemciyle
// çalışır: sistem yavaşlayınca istemciler de yavaşlar, gerçek aşırı yük
// oluşmaz. Burada istekler sistemin yetişip yetişmediğine bakılmadan
// sabit bir hızla gelir - gerçek kullanıcı trafiğine daha yakın.
//
// Kaba kapasite (3 backend toplamı):
//   tam modda       ~160 istek/sn  (2/70ms + 8/100ms + 8/150ms)
//   brownout'ta     ~230 istek/sn  (2/40ms + 8/70ms  + 8/120ms)
//
// Çalıştırma:
//   docker run --rm -i --network rasplb_default \
//     -e BASE_URL=http://gateway:8080 grafana/k6 run - < benchmarks/load/overload.js

import http from "k6/http";
import { Counter } from "k6/metrics";

const BASE_URL = __ENV.BASE_URL || "http://localhost:5200";

const ok = new Counter("rasp_ok");
const shed = new Counter("rasp_shed");
const failed = new Counter("rasp_failed");

export const options = {
  discardResponseBodies: true,
  scenarios: {
    overload: {
      executor: "ramping-arrival-rate",
      startRate: 20,
      timeUnit: "1s",
      preAllocatedVUs: 200,
      maxVUs: 3000,
      stages: [
        { target: 120, duration: "15s" }, // normal
        { target: 120, duration: "45s" },
        { target: 220, duration: "15s" }, // tam mod kapasitesinin üstü
        { target: 220, duration: "45s" },
        { target: 350, duration: "15s" }, // her modun üstü: aşırı yük
        { target: 350, duration: "45s" },
        { target: 120, duration: "10s" }, // toparlanma
        { target: 120, duration: "50s" },
      ],
    },
  },
  thresholds: {
    // Bilgi amaçlı: testi düşürmez, özette görünür.
    "http_req_duration{expected_response:true}": [{ threshold: "p(95)<500", abortOnFail: false }],
  },
};

export default function () {
  const response = http.get(`${BASE_URL}/api/work`, { timeout: "10s" });

  if (response.status >= 200 && response.status < 300) {
    ok.add(1);
  } else if (response.status === 503 && response.headers["X-Rasp-Shed"]) {
    shed.add(1);
  } else {
    failed.add(1);
  }
}
