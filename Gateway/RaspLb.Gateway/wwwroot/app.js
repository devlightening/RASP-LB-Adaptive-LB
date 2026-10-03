"use strict";

const POLL_MS = 1000;
const WINDOW = 119; // gateway'in döndürdüğü tamamlanmış saniye sayısı

const OUTCOMES = [
  { key: "onTime", label: "Zamanında", color: "--good" },
  { key: "late", label: "Geç (deadline aşıldı)", color: "--warning" },
  { key: "shed", label: "Reddedildi (503)", color: "--serious" },
  { key: "errors", label: "Hata", color: "--critical" },
];

const BACKEND_COLORS = ["--backend1", "--backend2", "--backend3"];

const state = {
  paused: false,
  last: null,
  queueHistory: new Map(), // id -> [{ t, waiting }]
  prev: new Map(),         // id -> önceki backend durumu (olaylar için)
  events: [],
};

const $ = (id) => document.getElementById(id);
const css = (name) => getComputedStyle(document.documentElement).getPropertyValue(name).trim();
const fmt = (n, digits = 0) => (n == null || Number.isNaN(n) ? "–" : n.toLocaleString("tr-TR", { maximumFractionDigits: digits, minimumFractionDigits: digits }));
const clock = (ms) => new Date(ms).toLocaleTimeString("tr-TR");

// ---------------------------------------------------------------- polling

async function poll() {
  if (!state.paused) {
    try {
      const response = await fetch("/debug/live", { cache: "no-store" });
      if (!response.ok) throw new Error(response.status);
      const live = await response.json();
      update(live);
      $("status").textContent = "canlı · " + clock(live.serverTime);
      $("status").className = "live";
    } catch {
      $("status").textContent = "gateway'e ulaşılamıyor";
      $("status").className = "down";
    }
  }
  setTimeout(poll, POLL_MS);
}

$("pause").addEventListener("click", () => {
  state.paused = !state.paused;
  $("pause").textContent = state.paused ? "Devam" : "Duraklat";
  if (state.paused) {
    $("status").textContent = "duraklatıldı";
    $("status").className = "muted";
  }
});

function update(live) {
  state.last = live;
  const now = Math.floor(live.serverTime / 1000);

  for (const d of live.destinations) {
    const history = state.queueHistory.get(d.id) ?? [];
    const waiting = d.backend ? d.backend.admission.waiting : null;
    if (history.length && history[history.length - 1].t === now) history.pop();
    history.push({ t: now, waiting });
    while (history.length && history[0].t < now - WINDOW) history.shift();
    state.queueHistory.set(d.id, history);
  }

  detectEvents(live);
  render();
}

// ---------------------------------------------------------------- events

function detectEvents(live) {
  const at = live.serverTime;

  for (const d of live.destinations) {
    const prev = state.prev.get(d.id);
    const b = d.backend;
    const next = {
      reachable: !!b,
      reduced: b ? b.brownout.reducedModeActive : false,
      shedTotal: b ? b.admission.rejectedQueueFull + b.admission.rejectedPredictedWait + b.admission.rejectedQueueTimeout : 0,
      shedding: false,
      quiet: 0,
    };

    if (prev) {
      if (prev.reachable && !next.reachable) log(at, `${d.id} yanıt vermiyor`);
      if (!prev.reachable && next.reachable) log(at, `${d.id} tekrar yanıt veriyor`);

      if (b) {
        if (!prev.reduced && next.reduced) {
          log(at, `${d.id} brownout'a geçti — tahmini bekleme ${fmt(b.brownout.activationQueueWaitMs)} ms (eşik ${fmt(b.brownout.tripQueueWaitMs)} ms)`);
        }
        if (prev.reduced && !next.reduced) log(at, `${d.id} tam moda döndü`);

        const newlyShed = next.shedTotal - prev.shedTotal;
        next.shedding = prev.shedding;
        next.quiet = newlyShed > 0 ? 0 : prev.quiet + 1;

        if (newlyShed > 0 && !prev.shedding) {
          next.shedding = true;
          log(at, `${d.id} istek reddetmeye başladı — kuyrukta ${b.admission.waiting}`);
        }
        if (prev.shedding && next.quiet >= 3) {
          next.shedding = false;
          log(at, `${d.id} reddetmeyi bıraktı`);
        }
      }
    }

    state.prev.set(d.id, next);
  }
}

function log(at, text) {
  state.events.unshift({ at, text });
  state.events.length = Math.min(state.events.length, 100);
}

// ---------------------------------------------------------------- render

function render() {
  const live = state.last;
  if (!live) return;

  const backendCaps = live.destinations.map((d) => d.capacity).join("/");
  $("config").textContent =
    `${live.activePolicy} · deadline ${live.deadlineMs} ms · kapasite ${backendCaps}`;

  renderKpis(live);
  renderBackends(live);
  renderTraffic(live);
  renderLatency(live);
  renderQueue(live);
  renderEvents();
  renderTotals(live);
}

function renderKpis(live) {
  const recent = live.series.slice(-5);
  const sum = (key) => recent.reduce((acc, s) => acc + s[key], 0);
  const total = sum("onTime") + sum("late") + sum("shed") + sum("errors");
  const seconds = recent.length || 1;
  const p95s = recent.map((s) => s.p95Ms).filter((v) => v != null);
  const onTimeRate = total ? sum("onTime") / total : null;

  $("kpi-rps").textContent = fmt(total / seconds);
  $("kpi-ontime").textContent = onTimeRate == null ? "–" : "%" + fmt(onTimeRate * 100, 1);
  $("kpi-ontime").classList.toggle("bad", onTimeRate != null && onTimeRate < 0.95);
  $("kpi-p95").textContent = p95s.length ? fmt(p95s.reduce((a, b) => a + b, 0) / p95s.length) + " ms" : "–";
  $("kpi-p95").classList.toggle("bad", p95s.some((v) => v > live.deadlineMs));
  $("kpi-shed").textContent = fmt(sum("shed") / seconds);
  $("kpi-shed").classList.toggle("bad", sum("shed") > 0);
  $("kpi-err").textContent = fmt(sum("errors") / seconds);
  $("kpi-err").classList.toggle("bad", sum("errors") > 0);
}

function renderBackends(live) {
  const rows = live.destinations.map((d, i) => {
    const swatch = `<span class="swatch" style="background:var(${BACKEND_COLORS[i]})"></span>`;
    const b = d.backend;

    if (!b) {
      return `<tr><td>${swatch}${d.id}</td><td class="num">${d.capacity}</td>
        <td colspan="6" class="unreachable">yanıt yok</td></tr>`;
    }

    const a = b.admission;
    const fill = Math.min(1, a.active / a.capacity) * 100;
    const mode = !b.brownout.enabled
      ? `<span class="mode-off">kapalı</span>`
      : b.brownout.reducedModeActive
        ? `<span class="mode-reduced">Azaltılmış</span>`
        : "Tam";
    const queueLimit = a.sheddingEnabled ? ` <span class="muted">/ ${a.maxQueueLength}</span>` : "";

    return `<tr>
      <td>${swatch}${d.id}</td>
      <td class="num">${a.capacity}</td>
      <td><span class="meter"><span style="width:${fill}%"></span></span>${a.active}</td>
      <td class="num">${a.waiting}${queueLimit}</td>
      <td class="num">${fmt(a.estimatedQueueWaitMs)} ms</td>
      <td>${mode}</td>
      <td class="num">${a.sheddingEnabled ? `${fmt(a.rejectedQueueFull + a.rejectedPredictedWait)} / ${fmt(a.rejectedQueueTimeout)}` : `<span class="mode-off">kapalı</span>`}</td>
      <td class="num">${fmt(d.ewmaLatencyMs)} ms</td>
    </tr>`;
  });

  document.querySelector("#backends tbody").innerHTML = rows.join("");
}

function renderEvents() {
  $("events").innerHTML = state.events.length
    ? state.events.map((e) => `<li><time>${clock(e.at)}</time>${e.text}</li>`).join("")
    : `<li class="muted">Henüz olay yok.</li>`;
}

function renderTotals(live) {
  const slo = live.slo;
  const r = live.retries;
  $("totals").textContent =
    `Başlangıçtan beri ${fmt(slo.totalRequests)} istek · goodput %${fmt(slo.goodputRate * 100, 2)}` +
    ` · deadline aşan ${fmt(slo.deadlineExceeded)} · retry ${fmt(r.retryAttempts)} (${fmt(r.retrySuccesses)} kurtarıldı)`;
}

// ---------------------------------------------------------------- charts

const PAD = { top: 8, right: 84, bottom: 20, left: 40 };

function frame(el) {
  const width = el.clientWidth;
  const height = el.clientHeight;
  return {
    width,
    height,
    plotW: width - PAD.left - PAD.right,
    plotH: height - PAD.top - PAD.bottom,
  };
}

function niceMax(value) {
  if (!(value > 0)) return 1;
  const power = 10 ** Math.floor(Math.log10(value));
  for (const m of [1, 2, 2.5, 5, 10]) if (m * power >= value) return m * power;
  return 10 * power;
}

function axes(f, yMax, unit = "") {
  const parts = [];
  for (let i = 0; i <= 4; i++) {
    const v = (yMax / 4) * i;
    const y = PAD.top + f.plotH - (v / yMax) * f.plotH;
    const stroke = i === 0 ? "var(--axis)" : "var(--grid)";
    parts.push(`<line x1="${PAD.left}" x2="${PAD.left + f.plotW}" y1="${y}" y2="${y}" stroke="${stroke}" stroke-width="1"/>`);
    if (i % 2 === 0) {
      parts.push(`<text x="${PAD.left - 6}" y="${y + 4}" text-anchor="end">${fmt(v)}${unit}</text>`);
    }
  }
  for (const s of [-120, -90, -60, -30, 0]) {
    const x = PAD.left + ((s + WINDOW + 1) / (WINDOW + 1)) * f.plotW;
    const label = s === 0 ? "şimdi" : `${s} sn`;
    const anchor = s === 0 ? "end" : s === -120 ? "start" : "middle";
    parts.push(`<text x="${x}" y="${f.height - 4}" text-anchor="${anchor}">${label}</text>`);
  }
  return parts.join("");
}

function xForIndex(f, index) {
  return PAD.left + ((index + 0.5) / WINDOW) * f.plotW;
}

// bridge: bu kadar saniyelik boşluğu çizgiyle birleştir. Kuyruk geçmişi
// tarayıcıda yoklamayla toplanıyor ve yoklama aralığı 1 sn'yi biraz aştığında
// arada bir saniye boş kalıyor - bu, çizgiyi kopuk noktalara bölmemeli.
function linePath(f, values, yMax, bridge = 0) {
  let d = "";
  let pen = false;
  let gap = 0;
  values.forEach((v, i) => {
    if (v == null) {
      if (++gap > bridge) pen = false;
      return;
    }
    gap = 0;
    const x = xForIndex(f, i);
    const y = PAD.top + f.plotH - (Math.min(v, yMax) / yMax) * f.plotH;
    d += `${pen ? "L" : "M"}${x.toFixed(1)},${y.toFixed(1)}`;
    pen = true;
  });
  return d;
}

function lastValue(values) {
  for (let i = values.length - 1; i >= 0; i--) if (values[i] != null) return { i, v: values[i] };
  return null;
}

function legend(el, items) {
  el.innerHTML = items
    .map((it) => `<span><span class="swatch" style="background:var(${it.color})"></span>${it.label}</span>`)
    .join("");
}

function renderTraffic(live) {
  const el = $("chart-traffic");
  const f = frame(el);
  const series = live.series;
  const totals = series.map((s) => s.onTime + s.late + s.shed + s.errors);
  const yMax = niceMax(Math.max(10, ...totals));
  const slot = f.plotW / WINDOW;
  const barW = Math.max(1, slot - 1.5);
  const bars = [];

  series.forEach((s, i) => {
    let base = 0;
    for (const o of OUTCOMES) {
      const v = s[o.key];
      if (!v) continue;
      const h = (v / yMax) * f.plotH;
      const y = PAD.top + f.plotH - ((base + v) / yMax) * f.plotH;
      bars.push(`<rect x="${(PAD.left + i * slot).toFixed(1)}" y="${y.toFixed(1)}" width="${barW.toFixed(1)}" height="${Math.max(0.5, h).toFixed(1)}" fill="var(${o.color})"/>`);
      base += v;
    }
  });

  legend($("legend-traffic"), OUTCOMES);
  el.innerHTML = `<svg>${axes(f, yMax)}${bars.join("")}<g class="hover"></g></svg>`;

  attachHover(el, f, (i) => {
    const s = series[i];
    if (!s) return null;
    return {
      time: s.unixSecond * 1000,
      rows: OUTCOMES.map((o) => [o.label, fmt(s[o.key]), o.color]),
    };
  });
}

function renderLatency(live) {
  const el = $("chart-latency");
  const f = frame(el);
  const p95 = live.series.map((s) => s.p95Ms);
  const yMax = niceMax(Math.max(live.deadlineMs * 1.2, ...p95.filter((v) => v != null)));
  const deadlineY = PAD.top + f.plotH - (live.deadlineMs / yMax) * f.plotH;
  const last = lastValue(p95);

  let end = "";
  if (last) {
    const x = xForIndex(f, last.i);
    const y = PAD.top + f.plotH - (Math.min(last.v, yMax) / yMax) * f.plotH;
    end = `<circle cx="${x}" cy="${y}" r="4" fill="var(--backend1)" stroke="var(--surface)" stroke-width="2"/>
      <text class="end-label" x="${x + 8}" y="${y + 4}">${fmt(last.v)} ms</text>`;
  }

  el.innerHTML = `<svg>${axes(f, yMax)}
    <line x1="${PAD.left}" x2="${PAD.left + f.plotW}" y1="${deadlineY}" y2="${deadlineY}" stroke="var(--ink-2)" stroke-width="1" stroke-dasharray="4 4"/>
    <text x="${PAD.left + f.plotW + 6}" y="${deadlineY + 4}">deadline</text>
    <path d="${linePath(f, p95, yMax)}" fill="none" stroke="var(--backend1)" stroke-width="2" stroke-linejoin="round"/>
    ${end}<g class="hover"></g></svg>`;

  attachHover(el, f, (i) => {
    const s = live.series[i];
    if (!s) return null;
    return {
      time: s.unixSecond * 1000,
      rows: [["p50", s.p50Ms == null ? "–" : fmt(s.p50Ms) + " ms"], ["p95", s.p95Ms == null ? "–" : fmt(s.p95Ms) + " ms"]],
    };
  });
}

function renderQueue(live) {
  const el = $("chart-queue");
  const f = frame(el);
  const now = Math.floor(live.serverTime / 1000);
  const start = now - WINDOW;

  const lines = live.destinations.map((d, n) => {
    const values = new Array(WINDOW).fill(null);
    for (const point of state.queueHistory.get(d.id) ?? []) {
      const i = point.t - start - 1;
      if (i >= 0 && i < WINDOW) values[i] = point.waiting;
    }
    return { id: d.id, color: BACKEND_COLORS[n], values };
  });

  const yMax = niceMax(Math.max(4, ...lines.flatMap((l) => l.values.filter((v) => v != null))));

  const ends = lines.map((l) => {
    const last = lastValue(l.values);
    if (!last) return null;
    return { ...l, x: xForIndex(f, last.i), y: PAD.top + f.plotH - (last.v / yMax) * f.plotH, v: last.v };
  }).filter(Boolean);

  // Uç etiketleri üst üste binmesin.
  const labels = ends.slice().sort((a, b) => a.y - b.y);
  for (let k = 1; k < labels.length; k++) {
    if (labels[k].y - labels[k - 1].y < 13) labels[k].ly = (labels[k - 1].ly ?? labels[k - 1].y) + 13;
  }

  const paths = lines.map((l) =>
    `<path d="${linePath(f, l.values, yMax, 3)}" fill="none" stroke="var(${l.color})" stroke-width="2" stroke-linejoin="round"/>`);
  const marks = ends.map((e) =>
    `<circle cx="${e.x}" cy="${e.y}" r="4" fill="var(${e.color})" stroke="var(--surface)" stroke-width="2"/>
     <text class="end-label" x="${e.x + 8}" y="${(e.ly ?? e.y) + 4}">${e.id} ${e.v}</text>`);

  legend($("legend-queue"), lines.map((l) => ({ label: l.id, color: l.color })));
  el.innerHTML = `<svg>${axes(f, yMax)}${paths.join("")}${marks.join("")}<g class="hover"></g></svg>`;

  attachHover(el, f, (i) => {
    const rows = lines.map((l) => [l.id, l.values[i] == null ? "–" : fmt(l.values[i]), l.color]);
    if (rows.every((r) => r[1] === "–")) return null;
    return { time: (start + i + 1) * 1000, rows };
  });
}

// ---------------------------------------------------------------- hover

function attachHover(el, f, describe) {
  const svg = el.querySelector("svg");
  const layer = svg.querySelector(".hover");
  const tooltip = $("tooltip");

  svg.onmousemove = (event) => {
    const rect = svg.getBoundingClientRect();
    const x = event.clientX - rect.left;
    const i = Math.round(((x - PAD.left) / f.plotW) * WINDOW - 0.5);
    const info = i >= 0 && i < WINDOW ? describe(i) : null;

    if (!info) {
      layer.innerHTML = "";
      tooltip.hidden = true;
      return;
    }

    const cx = xForIndex(f, i);
    layer.innerHTML = `<line x1="${cx}" x2="${cx}" y1="${PAD.top}" y2="${PAD.top + f.plotH}" stroke="var(--ink-2)" stroke-width="1"/>`;

    tooltip.innerHTML = `<div class="muted">${clock(info.time)}</div>` +
      info.rows.map(([label, value, color]) =>
        `<div class="row"><span>${color ? `<span class="swatch" style="background:var(${color})"></span>` : ""}${label}</span><b>${value}</b></div>`).join("");
    tooltip.hidden = false;

    const left = Math.min(event.clientX + 14, window.innerWidth - tooltip.offsetWidth - 8);
    tooltip.style.left = left + "px";
    tooltip.style.top = event.clientY + 14 + "px";
  };

  svg.onmouseleave = () => {
    layer.innerHTML = "";
    tooltip.hidden = true;
  };
}

window.addEventListener("resize", render);
poll();
