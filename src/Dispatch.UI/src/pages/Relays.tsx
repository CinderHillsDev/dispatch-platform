import { useEffect, useState } from "react";
import { api, type RelayDetail, type RelayListItem, type TestResult } from "../lib/api";

// Mirrors RelayProviderSchema on the server so the form can render fields immediately on provider change.
const PROVIDER_FIELDS: Record<string, { name: string; secret: boolean; required: boolean }[]> = {
  None: [],
  Smtp: [
    { name: "Host", secret: false, required: true },
    { name: "Port", secret: false, required: false },
    { name: "Username", secret: false, required: false },
    { name: "Password", secret: true, required: false },
    { name: "TlsMode", secret: false, required: false },
  ],
  Mailgun: [
    { name: "ApiKey", secret: true, required: true },
    { name: "Domain", secret: false, required: true },
    { name: "Region", secret: false, required: false },
  ],
  SendGrid: [{ name: "ApiKey", secret: true, required: true }],
  AzureCommunication: [
    { name: "ConnectionString", secret: true, required: true },
    { name: "SenderAddress", secret: false, required: true },
  ],
};

export function Relays() {
  const [list, setList] = useState<RelayListItem[]>([]);
  const [selected, setSelected] = useState<RelayDetail | null>(null);
  const [newName, setNewName] = useState("");
  const [newProvider, setNewProvider] = useState("None");
  const [msg, setMsg] = useState<string | null>(null);

  const refresh = async () => setList(await api.relays.list());
  useEffect(() => { refresh(); }, []);

  const select = async (id: number) => { setMsg(null); setSelected(await api.relays.get(id)); };

  const addRelay = async () => {
    if (!newName.trim()) return;
    const { id } = await api.relays.create(newName.trim(), newProvider);
    setNewName("");
    await refresh();
    await select(id);
  };

  return (
    <>
      <h1 className="page-title">Relays</h1>

      <div style={{ display: "flex", gap: 22, alignItems: "flex-start", flexWrap: "wrap" }}>
        <div className="panel" style={{ flex: "1 1 360px" }}>
          <h2>Configured relays</h2>
          <table>
            <thead><tr><th>Name</th><th>Provider</th><th>Default</th><th>Status</th></tr></thead>
            <tbody>
              {list.map((r) => (
                <tr key={r.id} style={{ cursor: "pointer", background: selected?.id === r.id ? "var(--panel-2)" : undefined }} onClick={() => select(r.id)}>
                  <td>{r.isDefault ? "★ " : ""}{r.name}</td>
                  <td>{r.provider}</td>
                  <td>{r.isDefault ? <span className="badge ok">default</span> : ""}</td>
                  <td>{r.enabled ? "Enabled" : <span className="muted">Disabled</span>}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <div style={{ marginTop: 14, display: "flex", gap: 8 }}>
            <input placeholder="New relay name" value={newName} onChange={(e) => setNewName(e.target.value)} style={{ flex: 1 }} />
            <select value={newProvider} onChange={(e) => setNewProvider(e.target.value)}>
              {Object.keys(PROVIDER_FIELDS).map((p) => <option key={p} value={p}>{p}</option>)}
            </select>
            <button onClick={addRelay}>Add</button>
          </div>
        </div>

        {selected && (
          <RelayEditor
            key={selected.id}
            relay={selected}
            onChanged={async () => { await refresh(); await select(selected.id); }}
            onDeleted={async () => { setSelected(null); await refresh(); }}
            setMsg={setMsg}
          />
        )}
      </div>
      {msg && <p style={{ marginTop: 16 }}><span className={msg.startsWith("Error") ? "badge error" : "badge ok"}>{msg}</span></p>}
    </>
  );
}

function RelayEditor({ relay, onChanged, onDeleted, setMsg }: {
  relay: RelayDetail;
  onChanged: () => Promise<void>;
  onDeleted: () => Promise<void>;
  setMsg: (m: string | null) => void;
}) {
  const [name, setName] = useState(relay.name);
  const [provider, setProvider] = useState(relay.provider);
  const [enabled, setEnabled] = useState(relay.enabled);
  const [values, setValues] = useState<Record<string, string>>(() => {
    const v: Record<string, string> = {};
    for (const f of relay.fields) v[f.name] = f.secret ? "" : f.value;
    return v;
  });
  const [testTo, setTestTo] = useState("");
  const [testResult, setTestResult] = useState<TestResult | null>(null);
  const [busy, setBusy] = useState(false);

  const fields = PROVIDER_FIELDS[provider] ?? [];
  // Surface the implicit "Unconfigured" state in the dropdown until a real provider is chosen.
  const options = relay.providers.includes(provider) ? relay.providers : [provider, ...relay.providers];
  const secretHasValue = (n: string) => relay.provider === provider && (relay.fields.find((f) => f.name === n)?.hasValue ?? false);

  const save = async () => {
    setBusy(true); setMsg(null);
    try {
      await api.relays.update(relay.id, { name, provider, enabled, maxConcurrency: relay.maxConcurrency, settings: values });
      setMsg("Saved.");
      await onChanged();
    } catch (e) { setMsg(`Error: ${(e as Error).message}`); }
    finally { setBusy(false); }
  };

  const act = async (fn: () => Promise<unknown>, ok: string) => {
    setBusy(true); setMsg(null);
    try { await fn(); setMsg(ok); await onChanged(); }
    catch (e) { setMsg(`Error: ${(e as Error).message}`); }
    finally { setBusy(false); }
  };

  return (
    <div className="panel" style={{ flex: "1 1 380px" }}>
      <h2>Edit · {relay.name}{relay.isDefault ? " (default)" : ""}</h2>

      <label className="muted" style={{ fontSize: 12 }}>Name</label>
      <input style={{ width: "100%", marginBottom: 12 }} value={name} onChange={(e) => setName(e.target.value)} />

      <label className="muted" style={{ fontSize: 12 }}>Provider</label>
      <select style={{ width: "100%", marginBottom: 12 }} value={provider} onChange={(e) => { setProvider(e.target.value); setValues({}); }}>
        {options.map((p) => <option key={p} value={p}>{p}</option>)}
      </select>
      {provider === "Unconfigured" && (
        <p className="muted" style={{ marginTop: -4, marginBottom: 12, fontSize: 12 }}>
          This relay has no provider yet — mail to it will fail until you choose one (or pick “None” for local dev).
        </p>
      )}
      {provider === "None" && (
        <p className="muted" style={{ marginTop: -4, marginBottom: 12, fontSize: 12 }}>
          Local dev mode — never delivers externally. Messages are captured to <code>spool/captured/</code> so you can inspect them.
        </p>
      )}

      {fields.map((f) => (
        <div key={f.name} style={{ marginBottom: 10 }}>
          <label className="muted" style={{ display: "block", fontSize: 12, marginBottom: 4 }}>{f.name}{f.required ? " *" : ""}</label>
          <input
            style={{ width: "100%" }}
            type={f.secret ? "password" : "text"}
            placeholder={f.secret && secretHasValue(f.name) ? "•••••••• (unchanged)" : ""}
            value={values[f.name] ?? ""}
            onChange={(e) => setValues({ ...values, [f.name]: e.target.value })}
          />
        </div>
      ))}

      <label style={{ display: "flex", gap: 8, alignItems: "center", margin: "8px 0 14px" }}>
        <input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} style={{ width: "auto" }} /> Enabled
      </label>

      <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
        <button onClick={save} disabled={busy}>Save</button>
        {!relay.isDefault && <button onClick={() => act(() => api.relays.setDefault(relay.id), "Set as default.")} disabled={busy}>Set as default</button>}
        {!relay.isDefault && <button onClick={() => act(() => api.relays.remove(relay.id), "Deleted.").then(onDeleted)} disabled={busy}>Delete</button>}
      </div>

      <div style={{ marginTop: 16, borderTop: "1px solid var(--border)", paddingTop: 14 }}>
        <label className="muted" style={{ fontSize: 12 }}>Send test email</label>
        <div style={{ display: "flex", gap: 8, marginTop: 6 }}>
          <input style={{ flex: 1 }} placeholder="recipient@example.com" value={testTo} onChange={(e) => setTestTo(e.target.value)} />
          <button disabled={busy || !testTo} onClick={async () => {
            setBusy(true); setTestResult(null);
            try { setTestResult(await api.relays.test(relay.id, testTo)); }
            catch (e) { setTestResult({ ok: false, error: (e as Error).message }); }
            finally { setBusy(false); }
          }}>Test</button>
        </div>
        {testResult && (
          <div style={{ marginTop: 10 }}>
            {testResult.ok
              ? <span className="badge ok">Sent via {testResult.provider}</span>
              : <span className="badge error">Failed: {testResult.error}</span>}
          </div>
        )}
      </div>
    </div>
  );
}
