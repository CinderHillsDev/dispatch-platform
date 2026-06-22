import { useEffect, useState } from "react";
import { api, type InboxItem, type InboxMessage, type MessageDetail } from "../lib/api";
import { Modal } from "../Modal";

const badgeClass = (status: string) =>
  status === "OK" ? "badge ok" : status === "Denied" ? "badge denied" : "badge error";

const fmtBytes = (n: number) =>
  n < 1024 ? `${n} B` : n < 1048576 ? `${(n / 1024).toFixed(1)} KB` : `${(n / 1048576).toFixed(1)} MB`;

export function LocalInbox() {
  const [items, setItems] = useState<InboxItem[]>([]);
  const [selected, setSelected] = useState<InboxMessage | null>(null);
  const [retentionDays, setRetentionDays] = useState<number | null>(null);
  // Delivery-log detail for the open message, fetched on demand from the database (by spool id).
  const [log, setLog] = useState<MessageDetail | null>(null);
  const [logLoading, setLogLoading] = useState(false);
  const [logErr, setLogErr] = useState<string | null>(null);

  const refresh = async () => setItems(await api.inbox.list());
  useEffect(() => { refresh(); }, []);
  useEffect(() => { api.settings.get().then((s) => setRetentionDays(s.retention.capturedRetentionDays)).catch(() => {}); }, []);

  // Optional auto-refresh so new captures appear (off by default — same control as the Message Log).
  const [autoMs, setAutoMs] = useState(0);
  useEffect(() => {
    if (!autoMs) return;
    const t = setInterval(refresh, autoMs);
    return () => clearInterval(t);
  }, [autoMs]);

  const open = async (id: string) => { setLog(null); setLogErr(null); setSelected(await api.inbox.get(id)); };
  const closeDetail = () => { setSelected(null); setLog(null); setLogErr(null); };

  // The captured file is named "{spoolId}.eml", so the spool id (matching relay_log) is the id minus .eml.
  const loadLog = async () => {
    if (!selected) return;
    setLogLoading(true); setLogErr(null);
    try {
      const spoolId = selected.id.replace(/\.eml$/i, "");
      const { id } = await api.messageIdBySpool(spoolId);
      setLog(await api.message(id));
    } catch {
      setLogErr("No delivery-log entry found for this message.");
    } finally {
      setLogLoading(false);
    }
  };

  const remove = async (id: string) => {
    await api.inbox.remove(id);
    if (selected?.id === id) setSelected(null);
    await refresh();
  };

  const clearAll = async () => { await api.inbox.clear(); setSelected(null); await refresh(); };

  return (
    <>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 10 }}>
        <h1 className="page-title">Local Inbox</h1>
        <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
          <button onClick={() => refresh()} title="Refresh now">↻ Refresh</button>
          <label className="muted" style={{ display: "flex", alignItems: "center", gap: 6, fontSize: 12 }}>
            Auto-refresh
            <select value={autoMs} onChange={(e) => setAutoMs(Number(e.target.value))}>
              <option value={0}>Never</option>
              <option value={15000}>15s</option>
              <option value={30000}>30s</option>
              <option value={60000}>60s</option>
            </select>
          </label>
          {items.length > 0 && <button onClick={clearAll}>Clear all</button>}
        </div>
      </div>
      <p className="muted" style={{ marginTop: -10, marginBottom: 18 }}>
        Messages captured by the Local / developer provider — never sent externally.
        {retentionDays !== null && (
          retentionDays > 0
            ? <> They're automatically deleted after <strong>{retentionDays} day{retentionDays === 1 ? "" : "s"}</strong> (change under Settings → Storage &amp; retention).</>
            : <> They're kept until you delete them (retention is off in Settings → Storage &amp; retention).</>
        )}
      </p>

      <div className="panel" style={{ padding: 0, overflow: "hidden" }}>
        <table>
          <thead><tr><th>From</th><th>To</th><th>Subject</th><th>When</th><th></th></tr></thead>
          <tbody>
            {items.map((m) => (
              <tr key={m.id} style={{ cursor: "pointer" }} onClick={() => open(m.id)}>
                <td>{m.from}</td>
                <td>{m.to}</td>
                <td className="subject">{m.subject || <span className="muted">(no subject)</span>}</td>
                <td>{new Date(m.date).toLocaleTimeString()}</td>
                <td><button onClick={(e) => { e.stopPropagation(); remove(m.id); }}>✕</button></td>
              </tr>
            ))}
          </tbody>
        </table>
        {items.length === 0 && <div className="center">No captured messages yet. Configure a relay as “Local” and send mail.</div>}
      </div>

      {selected && (
        <Modal title={selected.subject || "(no subject)"} onClose={closeDetail}>
          <div style={{ display: "grid", gap: 16 }}>
            <div className="muted" style={{ fontSize: 13, wordBreak: "break-word" }}>
              <div>{selected.from} → {selected.to}</div>
              {selected.cc && <div>Cc: {selected.cc}</div>}
              <div style={{ marginTop: 4 }}>{new Date(selected.date).toLocaleString()}</div>
              <div style={{ marginTop: 8 }}>
                {!log && <button onClick={loadLog} disabled={logLoading}>{logLoading ? "Loading…" : "Show delivery log"}</button>}
                {logErr && <span className="badge denied" style={{ marginLeft: 8 }}>{logErr}</span>}
              </div>
            </div>

            {log && (
              <div style={{ border: "1px solid var(--border)", borderRadius: 8, padding: 12 }}>
                <div style={{ display: "flex", alignItems: "center", gap: 8, marginBottom: 8 }}>
                  <strong style={{ fontSize: 13 }}>Delivery log</strong>
                  <span className={badgeClass(log.status)}>{log.event}</span>
                </div>
                <div className="muted" style={{ fontSize: 13, display: "grid", gap: 4 }}>
                  <div>Relay: {log.relayName ?? "—"} · Provider: {log.provider ?? "—"}</div>
                  <div>Source: {log.ingestSource}{log.durationMs != null ? ` · ${log.durationMs} ms` : ""}</div>
                  <div>Logged: {new Date(log.loggedAt).toLocaleString()}</div>
                </div>
                {log.error && (
                  <div className="badge error" style={{ whiteSpace: "pre-wrap", display: "block", padding: 8, borderRadius: 6, marginTop: 8, lineHeight: 1.4 }}>{log.error}</div>
                )}
                {log.history.length > 1 && (
                  <ol style={{ margin: "10px 0 0", paddingLeft: 18 }}>
                    {log.history.map((h, i) => (
                      <li key={i} style={{ fontSize: 13 }}>
                        <span className={badgeClass(h.status)} style={{ marginRight: 6 }}>{h.event}</span>
                        <span className="muted">{new Date(h.loggedAt).toLocaleString()}</span>
                        {h.durationMs != null ? <span className="muted"> · {h.durationMs} ms</span> : null}
                      </li>
                    ))}
                  </ol>
                )}
              </div>
            )}

            {selected.attachments.length > 0 && (
              <div>
                <div className="muted" style={{ fontSize: 12, textTransform: "uppercase", letterSpacing: ".04em", marginBottom: 6 }}>
                  Attachments ({selected.attachments.length})
                </div>
                <div style={{ display: "grid", gap: 6 }}>
                  {selected.attachments.map((a) => (
                    <a
                      key={a.index}
                      href={`/api/local/messages/${encodeURIComponent(selected.id)}/attachments/${a.index}`}
                      download={a.name}
                      style={{ display: "flex", alignItems: "center", gap: 10, fontSize: 13, background: "var(--panel-2)", border: "1px solid var(--border)", borderRadius: 8, padding: "8px 10px", textDecoration: "none", color: "var(--text)" }}
                    >
                      <span>📎</span>
                      <span style={{ flex: 1, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>{a.name}</span>
                      <span className="muted" style={{ fontSize: 12 }}>{fmtBytes(a.sizeBytes)} · ↓</span>
                    </a>
                  ))}
                </div>
              </div>
            )}

            {selected.html
              ? <iframe title="message" sandbox="" srcDoc={selected.html} style={{ width: "100%", height: 420, border: "1px solid var(--border)", borderRadius: 8, background: "#fff" }} />
              : <pre style={{ whiteSpace: "pre-wrap", background: "var(--panel-2)", padding: 14, borderRadius: 8, margin: 0 }}>{selected.text ?? "(empty body)"}</pre>}
          </div>
        </Modal>
      )}
    </>
  );
}
