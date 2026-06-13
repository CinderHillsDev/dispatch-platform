import { useEffect, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { LineChart, Line, ResponsiveContainer, YAxis, Tooltip } from "recharts";
import { api, type RelayEvent } from "../lib/api";
import { createLogConnection } from "../lib/signalr";

function badgeClass(status: string) {
  if (status === "OK") return "badge ok";
  if (status === "Denied") return "badge denied";
  return "badge error";
}

function time(iso: string) {
  return new Date(iso).toLocaleTimeString();
}

export function Dashboard() {
  const stats = useQuery({ queryKey: ["stats"], queryFn: api.stats, refetchInterval: 5000 });
  const throughput = useQuery({ queryKey: ["throughput"], queryFn: api.throughput, refetchInterval: 60000 });
  const relayStats = useQuery({ queryKey: ["relayStats"], queryFn: api.relayStats, refetchInterval: 10000 });
  const [feed, setFeed] = useState<RelayEvent[]>([]);

  useEffect(() => {
    const conn = createLogConnection(
      (recent) => setFeed(recent.slice().reverse()),
      (evt) => setFeed((prev) => [evt, ...prev].slice(0, 50)),
    );
    conn.start().catch(() => { /* reconnect handled automatically */ });
    return () => { conn.stop(); };
  }, []);

  const s = stats.data;
  const inSpool = s ? s.spool.incoming + s.spool.processing : 0;
  const spark = (throughput.data ?? []).map((v, i) => ({ i, v }));

  return (
    <>
      <h1 className="page-title">Dashboard</h1>

      {s && s.intake !== "Normal" && (
        <div
          className="panel"
          style={{
            borderColor: s.intake === "Suspended" ? "#f85149" : "#d29922",
            color: s.intake === "Suspended" ? "#f85149" : "#d29922",
          }}
        >
          {s.intake === "Suspended"
            ? "⚠ Spool disk critically low — SMTP intake is SUSPENDED. Inbound mail is being rejected (senders will retry). Free disk space to resume."
            : "⚠ Spool disk low — SMTP intake is THROTTLED. Inbound mail is being delayed to slow the arrival rate. Free disk space to resume normal operation."}
        </div>
      )}

      <div className="cards">
        <Card label="Received today" value={s?.received} />
        <Card label="Delivered" value={s?.delivered} tone="green" />
        <Card label="Failed" value={s?.failed} tone="red" />
        <Card label="Denied" value={s?.denied} tone="amber" />
        <Card label="In spool" value={inSpool} />
      </div>

      <div className="panel">
        <h2>Throughput — delivered per minute (last 60)</h2>
        <div style={{ height: 120 }}>
          <ResponsiveContainer width="100%" height="100%">
            <LineChart data={spark}>
              <YAxis hide domain={[0, "dataMax + 1"]} />
              <Tooltip contentStyle={{ background: "#1f2430", border: "1px solid #2a2f3a" }} labelFormatter={() => ""} />
              <Line type="monotone" dataKey="v" stroke="#58a6ff" strokeWidth={2} dot={false} isAnimationActive={false} />
            </LineChart>
          </ResponsiveContainer>
        </div>
      </div>

      <div className="panel">
        <h2>Active relays</h2>
        <div style={{ display: "flex", gap: 12, flexWrap: "wrap" }}>
          {(relayStats.data ?? []).map((r) => (
            <div key={r.id} className="card" style={{ minWidth: 180 }}>
              <div style={{ fontWeight: 600 }}>
                {r.isDefault ? "★ " : ""}{r.name}
                {" "}<span className={r.failed > 0 ? "badge error" : "badge ok"} style={{ marginLeft: 4 }}>{r.failed > 0 ? "errors" : "ok"}</span>
              </div>
              <div className="muted" style={{ fontSize: 12, marginTop: 4 }}>{r.provider}{r.enabled ? "" : " · disabled"}</div>
              <div style={{ fontSize: 13, marginTop: 8 }}>
                {r.delivered} delivered · {r.failed} failed
              </div>
              <div className="muted" style={{ fontSize: 12 }}>{r.inFlight} in flight</div>
            </div>
          ))}
          {(relayStats.data ?? []).length === 0 && <span className="muted">No relays.</span>}
        </div>
      </div>

      <div className="panel">
        <h2>Spool health</h2>
        <div className="cards" style={{ marginBottom: 0 }}>
          <Card label="Incoming" value={s?.spool.incoming} />
          <Card label="Processing" value={s?.spool.processing} />
          <Card label="Failed" value={s?.spool.failed} tone={s && s.spool.failed > 0 ? "red" : undefined} />
        </div>
      </div>

      <div className="panel">
        <h2>Recent activity</h2>
        <div className="feed">
          {feed.length === 0 && <div className="center">Waiting for messages…</div>}
          {feed.map((e, i) => (
            <div className="feed-row" key={`${e.spoolId}-${i}`}>
              <span className="when">{time(e.loggedAt)}</span>
              <span className={badgeClass(e.status)}>{e.event}</span>
              <span className="who">{e.fromAddress} → @{e.toDomain} · {e.subject ?? "(no subject)"}</span>
              <span className="src">{e.ingestSource} · {e.provider ?? ""}</span>
            </div>
          ))}
        </div>
      </div>
    </>
  );
}

function Card({ label, value, tone }: { label: string; value?: number; tone?: "green" | "red" | "amber" }) {
  return (
    <div className="card">
      <div className="label">{label}</div>
      <div className={`value ${tone ?? ""}`}>{value ?? "—"}</div>
    </div>
  );
}
