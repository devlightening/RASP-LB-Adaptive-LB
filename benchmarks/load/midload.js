// Orta yük testi (~75 sn): 220 istek/sn. Tam mod kapasitesinin (~160/sn)
// üstünde, brownout kapasitesinin (~230/sn) altında - brownout'un kararlı
// bir noktaya oturup oturmadığını ya da açılıp kapandığını gösterir.
//
// Çalıştırma:
//   docker run --rm -i --network rasplb_default \
//     -e BASE_URL=http://gateway:8080 grafana/k6 run - < benchmarks/load/midload.js

import http from "k6/http";

const BASE_URL = __ENV.BASE_URL || "http://localhost:5200";

export const options = {
  discardResponseBodies: true,
  scenarios: {
    midload: {
      executor: "ramping-arrival-rate",
      startRate: 120,
      timeUnit: "1s",
      preAllocatedVUs: 100,
      maxVUs: 1000,
      stages: [
        { target: 220, duration: "10s" },
        { target: 220, duration: "65s" },
      ],
    },
  },
};

export default function () {
  http.get(`${BASE_URL}/api/work`, { timeout: "10s" });
}
