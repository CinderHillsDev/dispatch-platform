import { useEffect, useState } from "react";
import { api, type FailedItem, type FailedMessage } from "../lib/api";
import { Modal } from "../Modal";

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

      <div className="panel" style={{ padding: 0, overflow: "hidden" }}>
        <table>
          <thead><tr><th>From</th><th>To</th><th>Subject</th><th>Error</th><th>When</th><th></th></tr></thead>
          <tbody>
            {items.map((m) => (
              <tr key={m.id} style={{ cursor: "pointer" }} onClick={() => open(m.id)}>
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
        <Modal title={selected.subject || "(no subject)"} onClose={() => setSelected(null)}>
          <div style={{ display: "grid", gap: 16 }}>
            <div>
              <div className="muted" style={{ fontSize: 13, wordBreak: "break-word" }}>{selected.from} → {selected.to}</div>
              {selected.lastError && (
                <div className="badge error" style={{ whiteSpace: "pre-wrap", display: "block", padding: 10, borderRadius: 6, marginTop: 8, lineHeight: 1.4 }}>{selected.lastError}</div>
              )}
              <div style={{ display: "flex", gap: 8, marginTop: 12 }}>
                <button disabled={busy} onClick={() => retry(selected.id)}>Retry delivery</button>
                <button disabled={busy} onClick={() => remove(selected.id)}>Delete</button>
              </div>
            </div>
            {selected.html
              ? <iframe title="message" sandbox="" srcDoc={selected.html} style={{ width: "100%", height: 360, border: "1px solid var(--border)", borderRadius: 8, background: "#fff" }} />
              : <pre style={{ whiteSpace: "pre-wrap", background: "var(--panel-2)", padding: 14, borderRadius: 8, margin: 0 }}>{selected.text ?? "(empty body)"}</pre>}
          </div>
        </Modal>
      )}
    </>
  );
}
