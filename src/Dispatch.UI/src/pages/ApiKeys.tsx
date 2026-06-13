import { useEffect, useState } from "react";
import { api, type ApiKeyItem, type ApiKeyCreated } from "../lib/api";

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
  const [name, setName] = useState("");
  const [rateLimit, setRateLimit] = useState("");
  const [created, setCreated] = useState<ApiKeyCreated | null>(null);
  const [copied, setCopied] = useState(false);
  const [err, setErr] = useState<string | null>(null);

  const refresh = async () => setKeys(await api.keys.list());
  useEffect(() => { refresh(); }, []);

  const create = async () => {
    setErr(null);
    if (!name.trim()) { setErr("Name is required."); return; }
    const limit = rateLimit.trim() === "" ? null : Number(rateLimit);
    if (limit !== null && (!Number.isInteger(limit) || limit < 0)) {
      setErr("Rate limit must be a non-negative whole number.");
      return;
    }
    try {
      const result = await api.keys.create(name.trim(), limit);
      setCreated(result);
      setCopied(false);
      setName("");
      setRateLimit("");
      setCreating(false);
      await refresh();
    } catch (e) {
      setErr((e as Error).message);
    }
  };

  const revoke = async (k: ApiKeyItem) => {
    if (!confirm(`Revoke key "${k.name}"? This cannot be undone and the key stops working immediately.`)) return;
    await api.keys.revoke(k.id);
    await refresh();
  };

  const copy = async () => {
    if (!created) return;
    await navigator.clipboard.writeText(created.key);
    setCopied(true);
  };

  const visible = keys.filter((k) => showRevoked || !k.revoked);

  return (
    <>
      <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between" }}>
        <h1 className="page-title">API Keys</h1>
        <button onClick={() => { setCreating(true); setErr(null); }}>+ Create New Key</button>
      </div>
      <p className="muted" style={{ marginTop: -10, marginBottom: 18 }}>
        Keys authenticate the HTTP ingestion API (<code>Authorization: Bearer dsp_live_…</code>). Each key is
        bcrypt-hashed and shown in full only once, at creation.
      </p>

      {created && (
        <div className="panel" style={{ maxWidth: 720, borderColor: "var(--green, #2e7d32)" }}>
          <h2>New API Key Created</h2>
          <div style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
            <code style={{ flex: 1, minWidth: 280, padding: "8px 10px", background: "rgba(0,0,0,0.25)", borderRadius: 6, wordBreak: "break-all" }}>
              {created.key}
            </code>
            <button onClick={copy}>{copied ? "Copied" : "Copy"}</button>
          </div>
          <p style={{ marginTop: 12 }}>
            <span className="badge error">This key will not be shown again. Copy it now.</span>
          </p>
          <button style={{ marginTop: 8 }} onClick={() => setCreated(null)}>Done</button>
        </div>
      )}

      {creating && (
        <div className="panel" style={{ maxWidth: 720 }}>
          <h2>Create API Key</h2>
          <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
            <input placeholder="Name (e.g. Production app)" value={name} onChange={(e) => setName(e.target.value)} />
            <input placeholder="Rate limit / min (optional)" value={rateLimit} onChange={(e) => setRateLimit(e.target.value)} style={{ width: 200 }} />
            <button onClick={create}>Create</button>
            <button onClick={() => { setCreating(false); setErr(null); }}>Cancel</button>
          </div>
          {err && <p style={{ marginTop: 10 }}><span className="badge error">{err}</span></p>}
        </div>
      )}

      <div className="panel" style={{ maxWidth: 720, padding: 0, overflow: "hidden" }}>
        <table>
          <thead>
            <tr><th>Name</th><th>ID prefix</th><th>Last used</th><th>Messages</th><th></th></tr>
          </thead>
          <tbody>
            {visible.map((k) => (
              <tr key={k.id} style={k.revoked ? { opacity: 0.5 } : undefined}>
                <td>
                  {k.name}
                  {k.revoked && <span className="badge error" style={{ marginLeft: 8 }}>revoked</span>}
                </td>
                <td><code>{k.keyId}</code></td>
                <td>{relativeTime(k.lastUsedAt)}</td>
                <td>{k.messageCount.toLocaleString()}</td>
                <td>
                  {!k.revoked && <button onClick={() => revoke(k)}>✕</button>}
                </td>
              </tr>
            ))}
            {visible.length === 0 && <tr><td colSpan={5} className="center">No API keys yet.</td></tr>}
          </tbody>
        </table>
      </div>

      <label className="muted" style={{ display: "inline-flex", gap: 6, marginTop: 12, alignItems: "center" }}>
        <input type="checkbox" checked={showRevoked} onChange={(e) => setShowRevoked(e.target.checked)} />
        Show revoked keys
      </label>
    </>
  );
}
