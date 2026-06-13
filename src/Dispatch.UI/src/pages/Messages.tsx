import { useCallback, useEffect, useState } from "react";
import { api, type MessageRow } from "../lib/api";

function badgeClass(status: string) {
  if (status === "OK") return "badge ok";
  if (status === "Denied") return "badge denied";
  return "badge error";
}

const STATUS_OPTIONS = [
  { label: "All", value: "" },
  { label: "Delivered", value: "OK" },
  { label: "Error", value: "Error" },
  { label: "Denied", value: "Denied" },
];

export function Messages() {
  const [status, setStatus] = useState("");
  const [source, setSource] = useState("");
  const [rows, setRows] = useState<MessageRow[]>([]);
  const [cursor, setCursor] = useState<{ at: string; id: number } | null>(null);
  const [loading, setLoading] = useState(false);
  const [done, setDone] = useState(false);

  const load = useCallback(async (reset: boolean) => {
    setLoading(true);
    const params = new URLSearchParams();
    params.set("limit", "50");
    if (status) params.set("status", status);
    if (source) params.set("source", source);
    if (!reset && cursor) { params.set("cursorAt", cursor.at); params.set("cursorId", String(cursor.id)); }
    try {
      const page = await api.messages(params);
      setRows((prev) => (reset ? page.rows : [...prev, ...page.rows]));
      setCursor(page.nextCursor);
      setDone(page.nextCursor === null);
    } finally {
      setLoading(false);
    }
  }, [status, source, cursor]);

  // Reload from scratch whenever filters change.
  useEffect(() => {
    setRows([]); setCursor(null); setDone(false);
    setLoading(true);
    const params = new URLSearchParams();
    params.set("limit", "50");
    if (status) params.set("status", status);
    if (source) params.set("source", source);
    api.messages(params).then((page) => {
      setRows(page.rows); setCursor(page.nextCursor); setDone(page.nextCursor === null);
    }).finally(() => setLoading(false));
  }, [status, source]);

  return (
    <>
      <h1 className="page-title">Message Log</h1>

      <div className="filters">
        <select value={status} onChange={(e) => setStatus(e.target.value)}>
          {STATUS_OPTIONS.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
        </select>
        <select value={source} onChange={(e) => setSource(e.target.value)}>
          <option value="">All sources</option>
          <option value="SMTP">SMTP</option>
          <option value="API">API</option>
        </select>
      </div>

      <div className="panel" style={{ padding: 0, overflow: "hidden" }}>
        <table>
          <thead>
            <tr>
              <th>Time</th><th>Status</th><th>From</th><th>To</th><th>Subject</th>
              <th>Relay</th><th>Provider</th><th>Source</th><th>Duration</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.id}>
                <td>{new Date(r.loggedAt).toLocaleString()}</td>
                <td><span className={badgeClass(r.status)}>{r.event}</span></td>
                <td>{r.fromAddress}</td>
                <td>@{r.toDomain}</td>
                <td className="subject">{r.subject ?? <span className="muted">(none)</span>}</td>
                <td>{r.relayName ?? "—"}</td>
                <td>{r.provider ?? "—"}</td>
                <td>{r.ingestSource}</td>
                <td>{r.durationMs != null ? `${r.durationMs} ms` : "—"}</td>
              </tr>
            ))}
          </tbody>
        </table>
        {rows.length === 0 && !loading && <div className="center">No messages match these filters.</div>}
        {loading && <div className="center">Loading…</div>}
      </div>

      {!done && rows.length > 0 && (
        <button onClick={() => load(false)} disabled={loading}>Load more</button>
      )}
    </>
  );
}
