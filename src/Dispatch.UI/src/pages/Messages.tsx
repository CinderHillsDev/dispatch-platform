import { useCallback, useEffect, useRef, useState } from "react";
import { api, type MessageRow, type MessageDetail, type RelayListItem } from "../lib/api";

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

interface Filters {
  status: string;
  source: string;
  relay: string;
  fromDomain: string;
  toDomain: string;
  tag: string;
  from: string;
  to: string;
}

const EMPTY: Filters = { status: "", source: "", relay: "", fromDomain: "", toDomain: "", tag: "", from: "", to: "" };

function toParams(f: Filters): URLSearchParams {
  const p = new URLSearchParams();
  p.set("limit", "50");
  if (f.status) p.set("status", f.status);
  if (f.source) p.set("source", f.source);
  if (f.relay) p.set("relay", f.relay);
  if (f.fromDomain) p.set("fromDomain", f.fromDomain);
  if (f.toDomain) p.set("toDomain", f.toDomain);
  if (f.tag) p.set("tag", f.tag);
  // Date inputs are local dates; treat as start/end of day in UTC for the keyset query.
  if (f.from) p.set("from", new Date(f.from + "T00:00:00").toISOString());
  if (f.to) p.set("to", new Date(f.to + "T00:00:00Z").toISOString().slice(0, 10) + "T23:59:59.999Z");
  return p;
}

export function Messages() {
  const [filters, setFilters] = useState<Filters>(EMPTY);
  const [relays, setRelays] = useState<RelayListItem[]>([]);
  const [rows, setRows] = useState<MessageRow[]>([]);
  const [cursor, setCursor] = useState<{ at: string; id: number } | null>(null);
  const [loading, setLoading] = useState(false);
  const [done, setDone] = useState(false);
  const [selected, setSelected] = useState<number | null>(null);
  const [detail, setDetail] = useState<MessageDetail | null>(null);
  const [detailLoading, setDetailLoading] = useState(false);

  const set = <K extends keyof Filters>(key: K, value: Filters[K]) =>
    setFilters((f) => ({ ...f, [key]: value }));

  useEffect(() => { api.relays.list().then(setRelays).catch(() => setRelays([])); }, []);

  const loadMore = useCallback(async () => {
    if (!cursor) return;
    setLoading(true);
    const params = toParams(filters);
    params.set("cursorAt", cursor.at);
    params.set("cursorId", String(cursor.id));
    try {
      const page = await api.messages(params);
      setRows((prev) => [...prev, ...page.rows]);
      setCursor(page.nextCursor);
      setDone(page.nextCursor === null);
    } finally {
      setLoading(false);
    }
  }, [filters, cursor]);

  // Reload the first page whenever filters change, debounced 300 ms (spec §9.2).
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(() => {
    if (timer.current) clearTimeout(timer.current);
    timer.current = setTimeout(() => {
      setRows([]); setCursor(null); setDone(false); setLoading(true);
      api.messages(toParams(filters)).then((page) => {
        setRows(page.rows); setCursor(page.nextCursor); setDone(page.nextCursor === null);
      }).finally(() => setLoading(false));
    }, 300);
    return () => { if (timer.current) clearTimeout(timer.current); };
  }, [filters]);

  const openDetail = useCallback((id: number) => {
    if (selected === id) { setSelected(null); setDetail(null); return; }
    setSelected(id); setDetail(null); setDetailLoading(true);
    api.message(id).then(setDetail).catch(() => setDetail(null)).finally(() => setDetailLoading(false));
  }, [selected]);

  return (
    <div style={{ display: "flex", gap: 18, alignItems: "flex-start" }}>
      <div style={{ flex: 1, minWidth: 0 }}>
        <h1 className="page-title">Message Log</h1>

        <div className="filters">
          <input type="date" value={filters.from} onChange={(e) => set("from", e.target.value)} title="From date" />
          <input type="date" value={filters.to} onChange={(e) => set("to", e.target.value)} title="To date" />
          <select value={filters.status} onChange={(e) => set("status", e.target.value)}>
            {STATUS_OPTIONS.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
          </select>
          <select value={filters.relay} onChange={(e) => set("relay", e.target.value)}>
            <option value="">Any relay</option>
            {relays.map((r) => <option key={r.id} value={r.name}>{r.name}</option>)}
          </select>
          <select value={filters.source} onChange={(e) => set("source", e.target.value)}>
            <option value="">All sources</option>
            <option value="SMTP">SMTP</option>
            <option value="API">API</option>
          </select>
          <input placeholder="Sender domain" value={filters.fromDomain} onChange={(e) => set("fromDomain", e.target.value)} />
          <input placeholder="Recipient domain" value={filters.toDomain} onChange={(e) => set("toDomain", e.target.value)} />
          <input placeholder="Tag ⚠" value={filters.tag} onChange={(e) => set("tag", e.target.value)} />
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
                <tr
                  key={r.id}
                  onClick={() => openDetail(r.id)}
                  style={{ cursor: "pointer", background: selected === r.id ? "var(--border)" : undefined }}
                >
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
          <button onClick={loadMore} disabled={loading}>Load more</button>
        )}
      </div>

      {selected !== null && (
        <aside className="panel" style={{ width: 360, flexShrink: 0, position: "sticky", top: 18 }}>
          <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", marginBottom: 12 }}>
            <h2 style={{ margin: 0 }}>Message detail</h2>
            <button onClick={() => { setSelected(null); setDetail(null); }}>Close</button>
          </div>
          {detailLoading && <div className="center">Loading…</div>}
          {!detailLoading && !detail && <div className="center">Not found.</div>}
          {detail && (
            <dl className="kv" style={{ margin: 0, display: "grid", gridTemplateColumns: "1fr", gap: 10 }}>
              <Field label="Status"><span className={badgeClass(detail.status)}>{detail.event}</span></Field>
              <Field label="Logged at">{new Date(detail.loggedAt).toLocaleString()}</Field>
              <Field label="Spool ID"><code>{detail.spoolId}</code></Field>
              <Field label="From">{detail.fromAddress}</Field>
              <Field label="To">{detail.toAddresses.length > 0 ? detail.toAddresses.join(", ") : <span className="muted">—</span>}</Field>
              <Field label="Subject">{detail.subject ?? <span className="muted">(none)</span>}</Field>
              <Field label="Routing">
                {detail.routingMatched
                  ? (detail.routingRuleName ?? "(rule)")
                  : <span className="muted">Default (no rule)</span>}
              </Field>
              <Field label="Relay">{detail.relayName ?? <span className="muted">—</span>}</Field>
              <Field label="Provider">{detail.provider ?? <span className="muted">—</span>}</Field>
              <Field label="Provider message ID">{detail.providerMessageId ?? <span className="muted">—</span>}</Field>
              <Field label="Provider response">{detail.providerResponse ?? <span className="muted">—</span>}</Field>
              <Field label="Duration">{detail.durationMs != null ? `${detail.durationMs} ms` : <span className="muted">—</span>}</Field>
              <Field label="Size">{detail.sizeBytes} bytes</Field>
              <Field label="Retry attempt">{detail.retryAttempt}</Field>
              <Field label="Source">{detail.ingestSource}{detail.sourceIp ? ` (${detail.sourceIp})` : ""}</Field>
              <Field label="API key">{detail.apiKeyName ?? <span className="muted">—</span>}</Field>
              <Field label="Tags">
                {detail.tags.length > 0
                  ? detail.tags.map((t) => <span key={t} className="badge" style={{ marginRight: 6 }}>{t}</span>)
                  : <span className="muted">—</span>}
              </Field>
              {detail.error && <Field label="Error"><span className="badge error" style={{ whiteSpace: "pre-wrap" }}>{detail.error}</span></Field>}
            </dl>
          )}
        </aside>
      )}
    </div>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <div className="muted" style={{ fontSize: 12, textTransform: "uppercase", letterSpacing: ".04em", marginBottom: 2 }}>{label}</div>
      <div style={{ wordBreak: "break-word" }}>{children}</div>
    </div>
  );
}
