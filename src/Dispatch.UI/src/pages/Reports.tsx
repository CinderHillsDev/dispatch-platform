import { useCallback, useEffect, useState } from "react";
import { api, type ReportData } from "../lib/api";

// Local-date → yyyy-MM-dd (matches the API's date params).
const fmt = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;

type Preset = { label: string; range: () => [string, string] };

const PRESETS: Preset[] = [
  { label: "Today", range: () => { const t = fmt(new Date()); return [t, t]; } },
  { label: "Last 7 days", range: () => { const t = new Date(); const s = new Date(); s.setDate(t.getDate() - 6); return [fmt(s), fmt(t)]; } },
  { label: "This week", range: () => { const t = new Date(); const s = new Date(); const dow = (t.getDay() + 6) % 7; s.setDate(t.getDate() - dow); return [fmt(s), fmt(t)]; } },
  { label: "This month", range: () => { const t = new Date(); return [fmt(new Date(t.getFullYear(), t.getMonth(), 1)), fmt(t)]; } },
  { label: "Last month", range: () => { const t = new Date(); return [fmt(new Date(t.getFullYear(), t.getMonth() - 1, 1)), fmt(new Date(t.getFullYear(), t.getMonth(), 0))]; } },
  { label: "This year", range: () => { const t = new Date(); return [fmt(new Date(t.getFullYear(), 0, 1)), fmt(t)]; } },
];

const n = (v: number) => v.toLocaleString();

const DEFAULT_PRESET = PRESETS.find((p) => p.label === "This month")!;

export function Reports() {
  const [[from, to], setRange] = useState<[string, string]>(DEFAULT_PRESET.range());
  const [activePreset, setActivePreset] = useState<string | null>(DEFAULT_PRESET.label);
  const [data, setData] = useState<ReportData | null>(null);
  const [loading, setLoading] = useState(false);

  const load = useCallback(async (f: string, t: string) => {
    setLoading(true);
    try { setData(await api.reports(f, t)); } finally { setLoading(false); }
  }, []);

  useEffect(() => { load(from, to); }, [from, to, load]);

  const applyPreset = (p: Preset) => { setActivePreset(p.label); setRange(p.range()); };
  const setCustom = (which: 0 | 1, value: string) => {
    setActivePreset(null);
    setRange((r) => (which === 0 ? [value, r[1]] : [r[0], value]));
  };

  const s = data?.summary;
  const maxDaily = Math.max(1, ...(data?.daily ?? []).map((d) => d.received));

  return (
    <>
      <h1 className="page-title">Reports</h1>
      <p className="muted" style={{ marginTop: -10, marginBottom: 16 }}>
        Inbound and relay activity over a date range, from the daily counters.
      </p>

      {/* Date range: presets + custom */}
      <div style={{ display: "flex", flexWrap: "wrap", alignItems: "center", gap: 8, marginBottom: 18 }}>
        {PRESETS.map((p) => {
          const on = activePreset === p.label;
          return (
            <button key={p.label} onClick={() => applyPreset(p)}
              style={{ background: on ? "var(--blue)" : "transparent", color: on ? "#fff" : "var(--muted)", borderColor: on ? "var(--blue)" : "var(--border)", fontSize: 13 }}>
              {p.label}
            </button>
          );
        })}
        <span style={{ flex: 1 }} />
        <input type="date" value={from} max={to} onChange={(e) => setCustom(0, e.target.value)} />
        <span className="muted" style={{ fontSize: 12 }}>to</span>
        <input type="date" value={to} min={from} onChange={(e) => setCustom(1, e.target.value)} />
      </div>

      {/* Summary cards */}
      <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(150px, 1fr))", gap: 12, marginBottom: 22 }}>
        <Card label="Received" value={s?.received} accent="var(--blue)" />
        <Card label="Delivered" value={s?.delivered} accent="var(--green)" />
        <Card label="Failed" value={s?.failed} accent="var(--red)" />
        <Card label="Denied" value={s?.denied} accent="var(--amber)" />
        <Card label="Delivery rate" value={s ? deliveryRate(s.delivered, s.received) : undefined} suffix="" />
      </div>

      {/* Inbound by day */}
      <div className="panel" style={{ marginBottom: 22 }}>
        <h2>Inbound by day</h2>
        {loading && !data ? <div className="center">Loading…</div> : (data?.daily.length ?? 0) === 0
          ? <div className="center">No activity in this range.</div>
          : (
            <table>
              <thead><tr><th>Date</th><th>Received</th><th></th><th>Delivered</th><th>Failed</th><th>Denied</th></tr></thead>
              <tbody>
                {data!.daily.map((d) => (
                  <tr key={d.date}>
                    <td style={{ whiteSpace: "nowrap" }}>{d.date}</td>
                    <td>{n(d.received)}</td>
                    <td style={{ width: "40%" }}>
                      <div style={{ height: 8, borderRadius: 4, background: "var(--blue)", width: `${Math.round((d.received / maxDaily) * 100)}%`, minWidth: d.received > 0 ? 2 : 0 }} />
                    </td>
                    <td>{n(d.delivered)}</td>
                    <td>{d.failed > 0 ? <span className="badge error">{n(d.failed)}</span> : "0"}</td>
                    <td>{d.denied > 0 ? <span className="badge denied">{n(d.denied)}</span> : "0"}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
      </div>

      {/* By relay */}
      <div className="panel">
        <h2>By relay</h2>
        {(data?.relays.length ?? 0) === 0
          ? <div className="center">No relay activity in this range.</div>
          : (
            <table>
              <thead><tr><th>Relay</th><th>Received</th><th>Delivered</th><th>Failed</th><th>Denied</th><th>Delivery rate</th></tr></thead>
              <tbody>
                {data!.relays.map((r) => (
                  <tr key={r.relayId}>
                    <td>{r.relayName}</td>
                    <td>{n(r.received)}</td>
                    <td>{n(r.delivered)}</td>
                    <td>{r.failed > 0 ? <span className="badge error">{n(r.failed)}</span> : "0"}</td>
                    <td>{r.denied > 0 ? <span className="badge denied">{n(r.denied)}</span> : "0"}</td>
                    <td>{deliveryRate(r.delivered, r.received)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
      </div>
    </>
  );
}

function deliveryRate(delivered: number, received: number): string {
  if (received <= 0) return "—";
  return `${Math.round((delivered / received) * 100)}%`;
}

function Card({ label, value, accent, suffix }: { label: string; value: number | string | undefined; accent?: string; suffix?: string }) {
  return (
    <div className="card">
      <div className="muted" style={{ fontSize: 12, textTransform: "uppercase", letterSpacing: ".04em" }}>{label}</div>
      <div style={{ fontSize: 26, fontWeight: 600, marginTop: 6, color: accent ?? "var(--text)" }}>
        {value === undefined ? "—" : typeof value === "number" ? n(value) : value}{suffix ?? ""}
      </div>
    </div>
  );
}
