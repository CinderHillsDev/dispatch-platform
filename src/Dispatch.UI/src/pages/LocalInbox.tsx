import { useEffect, useState } from "react";
import { api, type InboxItem, type InboxMessage } from "../lib/api";
import { Modal } from "../Modal";

export function LocalInbox() {
  const [items, setItems] = useState<InboxItem[]>([]);
  const [selected, setSelected] = useState<InboxMessage | null>(null);

  const refresh = async () => setItems(await api.inbox.list());
  useEffect(() => {
    refresh();
    const t = setInterval(refresh, 5000);   // poll so new captures appear
    return () => clearInterval(t);
  }, []);

  const open = async (id: string) => setSelected(await api.inbox.get(id));

  const remove = async (id: string) => {
    await api.inbox.remove(id);
    if (selected?.id === id) setSelected(null);
    await refresh();
  };

  const clearAll = async () => { await api.inbox.clear(); setSelected(null); await refresh(); };

  return (
    <>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
        <h1 className="page-title">Local Inbox</h1>
        {items.length > 0 && <button onClick={clearAll}>Clear all</button>}
      </div>
      <p className="muted" style={{ marginTop: -10, marginBottom: 18 }}>
        Messages captured by the Local / developer provider — never sent externally.
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
        <Modal title={selected.subject || "(no subject)"} onClose={() => setSelected(null)}>
          <div style={{ display: "grid", gap: 16 }}>
            <div className="muted" style={{ fontSize: 13, wordBreak: "break-word" }}>
              <div>{selected.from} → {selected.to}</div>
              {selected.cc && <div>Cc: {selected.cc}</div>}
              <div style={{ marginTop: 4 }}>{new Date(selected.date).toLocaleString()}</div>
            </div>
            {selected.html
              ? <iframe title="message" sandbox="" srcDoc={selected.html} style={{ width: "100%", height: 420, border: "1px solid var(--border)", borderRadius: 8, background: "#fff" }} />
              : <pre style={{ whiteSpace: "pre-wrap", background: "var(--panel-2)", padding: 14, borderRadius: 8, margin: 0 }}>{selected.text ?? "(empty body)"}</pre>}
          </div>
        </Modal>
      )}
    </>
  );
}
