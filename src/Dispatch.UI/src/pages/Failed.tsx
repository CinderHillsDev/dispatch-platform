import { useEffect, useState } from "react";
import { api, type FailedItem, type FailedMessage } from "../lib/api";

export function Failed() {
  const [items, setItems] = useState<FailedItem[]>([]);
  const [selected, setSelected] = useState<FailedMessage | null>(null);
  const [busy, setBusy] = useState(false);

  const refresh = async () => setItems(await api.failed.list());
  useEffect(() => { refresh(); }, []);

  const open = async (id: string) => setSelected(await api.failed.get(id));

  const retry = async (id: string) => {
    setBusy(true);
    try { await api.failed.retry(id); if (selected?.id === id) setSelected(null); await refresh(); }
    finally { setBusy(false); }
  };
  const remove = async (id: string) => {
    setBusy(true);
    try { await api.failed.remove(id); if (selected?.id === id) setSelected(null); await refresh(); }
    finally { setBusy(false); }
  };

  return (
    <>
      <h1 className="page-title">Failed Messages</h1>
      <p className="muted" style={{ marginTop: -10, marginBottom: 18 }}>
        Messages that exhausted all retries. Fix the relay, then retry — or delete.
      </p>

      <div style={{ display: "flex", gap: 22, alignItems: "flex-start", flexWrap: "wrap" }}>
        <div className="panel" style={{ flex: "1 1 460px", padding: 0, overflow: "hidden" }}>
          <table>
            <thead><tr><th>From</th><th>To</th><th>Subject</th><th>Error</th><th>When</th><th></th></tr></thead>
            <tbody>
              {items.map((m) => (
                <tr key={m.id} style={{ cursor: "pointer", background: selected?.id === m.id ? "var(--panel-2)" : undefined }} onClick={() => open(m.id)}>
                  <td>{m.from}</td>
                  <td>@{(m.to[0] ?? "").split("@")[1] ?? ""}</td>
                  <td className="subject">{m.subject || <span className="muted">(none)</span>}</td>
                  <td className="subject" style={{ maxWidth: 220 }}><span className="badge error">{m.lastError ?? "error"}</span></td>
                  <td>{new Date(m.failedAt).toLocaleTimeString()}</td>
                  <td style={{ display: "flex", gap: 4 }}>
                    <button disabled={busy} onClick={(e) => { e.stopPropagation(); retry(m.id); }}>Retry</button>
                    <button disabled={busy} onClick={(e) => { e.stopPropagation(); remove(m.id); }}>✕</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {items.length === 0 && <div className="center">No failed messages. 🎉</div>}
        </div>

        {selected && (
          <div className="panel" style={{ flex: "1 1 420px" }}>
            <h2>{selected.subject || "(no subject)"}</h2>
            <div className="muted" style={{ fontSize: 13 }}>From: {selected.from}</div>
            <div className="muted" style={{ fontSize: 13, marginBottom: 8 }}>To: {selected.to}</div>
            {selected.lastError && <p><span className="badge error">{selected.lastError}</span></p>}
            <div style={{ display: "flex", gap: 8, margin: "10px 0 14px" }}>
              <button disabled={busy} onClick={() => retry(selected.id)}>Retry</button>
              <button disabled={busy} onClick={() => remove(selected.id)}>Delete</button>
            </div>
            {selected.html
              ? <iframe title="message" sandbox="" srcDoc={selected.html} style={{ width: "100%", height: 320, border: "1px solid var(--border)", borderRadius: 8, background: "#fff" }} />
              : <pre style={{ whiteSpace: "pre-wrap", background: "var(--panel-2)", padding: 14, borderRadius: 8 }}>{selected.text ?? "(empty body)"}</pre>}
          </div>
        )}
      </div>
    </>
  );
}
