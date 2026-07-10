import { useEffect, useState } from "react";
import { api, type ApiKeyItem, type ApiKeyCreated } from "../lib/api";
import { Modal } from "../Modal";
import { ActionsMenu } from "../ActionsMenu";

function relativeTime(iso: string | null): string {
  if (!iso) return "Never";
  const then = new Date(iso).getTime();
  const secs = Math.round((Date.now() - then) / 1000);
  if (secs < 60) return "just now";
  const mins = Math.round(secs / 60);
  if (mins < 60) return `${mins} min ago`;
  const hours = Math.round(mins / 60);
  if (hours < 24) return `${hours} hr ago`;
  const days = Math.round(hours / 24);
  if (days < 30) return `${days} day${days === 1 ? "" : "s"} ago`;
  return new Date(iso).toLocaleDateString();
}

export function ApiKeys() {
  const [keys, setKeys] = useState<ApiKeyItem[]>([]);
  const [showRevoked, setShowRevoked] = useState(false);
  const [creating, setCreating] = useState(false);
  const [created, setCreated] = useState<ApiKeyCreated | null>(null);
  const [err, setErr] = useState<string | null>(null);

  const refresh = async () => setKeys(await api.keys.list());
  useEffect(() => { refresh(); }, []);

  const revoke = async (k: ApiKeyItem) => {
    if (!confirm(`Revoke key “${k.name}”? It stops working immediately and can't be restored.`)) return;
    try { await api.keys.revoke(k.id); await refresh(); }
    catch (e) { setErr((e as Error).message); }
  };

  const visible = keys.filter((k) => showRevoked || !k.revoked);

  return (
    <div style={{ maxWidth: 760 }}>
      <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", gap: 12 }}>
        <h1 className="page-title" style={{ margin: 0 }}>API Keys</h1>
        <button onClick={() => setCreating(true)}>+ Create key</button>
      </div>
      <p className="muted" style={{ fontSize: 13, margin: "8px 0 18px" }}>
        Keys authenticate the HTTP ingestion API (<code>Authorization: Bearer dsp_live_…</code>). Each key is
        bcrypt-hashed and shown in full only once, at creation.
      </p>

      {err && <p style={{ marginBottom: 14 }}><span className="badge error">{err}</span></p>}

      <div className="panel" style={{ maxWidth: 760, padding: 0 }}>
        <table>
          <thead><tr><th>Name</th><th>ID prefix</th><th>Last used</th><th>Messages</th><th></th></tr></thead>
          <tbody>
            {visible.map((k) => (
              <tr key={k.id} style={k.revoked ? { opacity: 0.5 } : undefined}>
                <td>{k.name}{k.revoked && <span className="badge error" style={{ marginLeft: 8 }}>revoked</span>}</td>
                <td><code>{k.keyId}</code></td>
                <td>{relativeTime(k.lastUsedAt)}</td>
                <td>{k.messageCount.toLocaleString()}</td>
                <td style={{ textAlign: "right" }}>
                  <ActionsMenu items={[
                    { label: k.revoked ? "Revoked" : "Revoke", danger: true, disabled: k.revoked, onClick: () => revoke(k) },
                  ]} />
                </td>
              </tr>
            ))}
            {visible.length === 0 && <tr><td colSpan={5} className="center">No API keys yet - click “Create key”.</td></tr>}
          </tbody>
        </table>
      </div>

      <label className="muted" style={{ display: "inline-flex", gap: 6, marginTop: 12, alignItems: "center" }}>
        <input type="checkbox" checked={showRevoked} onChange={(e) => setShowRevoked(e.target.checked)} />
        Show revoked keys
      </label>

      {creating && <CreateKeyModal onClose={() => setCreating(false)} onCreated={async (r) => { setCreated(r); setCreating(false); await refresh(); }} />}
      {created && <CreatedKeyModal created={created} onClose={() => setCreated(null)} />}
    </div>
  );
}

function CreateKeyModal({ onClose, onCreated }: { onClose: () => void; onCreated: (r: ApiKeyCreated) => Promise<void> }) {
  const [name, setName] = useState("");
  const [rateLimit, setRateLimit] = useState("");
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);

  const create = async () => {
    if (!name.trim()) { setErr("Name is required."); return; }
    const limit = rateLimit.trim() === "" ? null : Number(rateLimit);
    if (limit !== null && (!Number.isInteger(limit) || limit < 0)) { setErr("Rate limit must be a non-negative whole number."); return; }
    setBusy(true); setErr(null);
    try { await onCreated(await api.keys.create(name.trim(), limit)); }
    catch (e) { setErr((e as Error).message); }
    finally { setBusy(false); }
  };

  return (
    <Modal title="Create API key" onClose={onClose}>
      <div style={{ display: "grid", gap: 12 }}>
        <Lbl label="Name"><input placeholder="e.g. Production app" value={name} onChange={(e) => setName(e.target.value)} style={{ width: "100%" }} /></Lbl>
        <Lbl label="Rate limit / minute (optional)"><input placeholder="blank = global default" value={rateLimit} onChange={(e) => setRateLimit(e.target.value)} style={{ width: "100%" }} /></Lbl>
        {err && <p style={{ color: "var(--red)", fontSize: 13, margin: 0 }}>{err}</p>}
        <div style={{ display: "flex", gap: 8, justifyContent: "flex-end", marginTop: 4 }}>
          <button onClick={onClose}>Cancel</button>
          <button onClick={create} disabled={busy}>{busy ? "Creating…" : "Create key"}</button>
        </div>
      </div>
    </Modal>
  );
}

function CreatedKeyModal({ created, onClose }: { created: ApiKeyCreated; onClose: () => void }) {
  const [copied, setCopied] = useState(false);
  const copy = async () => { await navigator.clipboard.writeText(created.key); setCopied(true); };
  return (
    <Modal title="API key created" onClose={onClose}>
      <div style={{ display: "grid", gap: 12 }}>
        <p style={{ margin: 0 }}><span className="badge error">Copy it now - it won't be shown again.</span></p>
        <div style={{ display: "flex", gap: 8, alignItems: "center" }}>
          <code style={{ flex: 1, padding: "8px 10px", background: "rgba(0,0,0,0.25)", borderRadius: 6, wordBreak: "break-all" }}>{created.key}</code>
          <button onClick={copy}>{copied ? "Copied" : "Copy"}</button>
        </div>
        <div style={{ display: "flex", justifyContent: "flex-end" }}><button onClick={onClose}>Done</button></div>
      </div>
    </Modal>
  );
}

function Lbl({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label style={{ display: "block" }}>
      <div className="muted" style={{ fontSize: 12, marginBottom: 4 }}>{label}</div>
      {children}
    </label>
  );
}
