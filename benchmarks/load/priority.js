// Öncelik bazlı shedding için kısa açık döngülü test (~1,5 dk).
//
// İstekler %20 critical, %50 normal, %30 sheddable olarak X-Rasp-Priority
// başlığıyla gönderilir. Aşırı yükte (350 istek/sn, kapasite ~230) beklenen:
// reddetmenin yükünü önce sheddable, sonra normal taşır; critical korunur.
//
// Çalıştırma:
//   docker run --rm -i --network rasplb_default \
//     -e BASE_URL=http://gateway:8080 grafana/k6 run - < benchmarks/load/priority.js

import http from "k6/http";
import { Counter } from "k6/metrics";

const BASE_URL = __ENV.BASE_URL || "http://localhost:5200";

const MIX = [
  { name: "critical", share: 0.2 },
  { name: "normal", share: 0.5 },
  { name: "sheddable", share: 0.3 },
];

const ok = new Counter("rasp_ok");
const shed = new Counter("rasp_shed");
const failed = new Counter("rasp_failed");

export const options = {
  discardResponseBodies: true,
  scenarios: {
    priority: {
      executor: "ramping-arrival-rate",
      startRate: 120,
      timeUnit: "1s",
      preAllocatedVUs: 200,
      maxVUs: 3000,
      stages: [
        { target: 120, duration: "20s" }, // normal
        { target: 350, duration: "5s" },
        { target: 350, duration: "50s" }, // aşırı yük
        { target: 120, duration: "5s" },
        { target: 120, duration: "15s" }, // toparlanma
      ],
    },
  },
  // Özet tabloda öncelik başına ayrı satır görünsün diye.
  thresholds: {
    "rasp_ok{priority:critical}": ["count>=0"],
    "rasp_ok{priority:normal}": ["count>=0"],
    "rasp_ok{priority:sheddable}": ["count>=0"],
    "rasp_shed{priority:critical}": ["count>=0"],
    "rasp_shed{priority:normal}": ["count>=0"],
    "rasp_shed{priority:sheddable}": ["count>=0"],
    "http_req_duration{priority:critical,expected_response:true}": ["p(95)>=0"],
    "http_req_duration{priority:normal,expected_response:true}": ["p(95)>=0"],
    "http_req_duration{priority:sheddable,expected_response:true}": ["p(95)>=0"],
  },
};

function pickPriority() {
  let r = Math.random();
  for (const p of MIX) {
    if ((r -= p.share) < 0) return p.name;
  }
  return MIX[MIX.length - 1].name;
}

export default function () {
  const priority = pickPriority();
  const tags = { priority };

  const response = http.get(`${BASE_URL}/api/work`, {
    timeout: "10s",
    headers: { "X-Rasp-Priority": priority },
    tags,
  });

  if (response.status >= 200 && response.status < 300) {
    ok.add(1, tags);
  } else if (response.status === 503 && response.headers["X-Rasp-Shed"]) {
    shed.add(1, tags);
  } else {
    failed.add(1, tags);
  }
}
