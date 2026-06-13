import { useEffect, useState } from "react";
import { api, type RelayConfig, type TestResult } from "../lib/api";

export function Settings() {
  const [cfg, setCfg] = useState<RelayConfig | null>(null);
  const [provider, setProvider] = useState("");
  const [values, setValues] = useState<Record<string, string>>({});
  const [saveMsg, setSaveMsg] = useState<string | null>(null);
  const [testTo, setTestTo] = useState("");
  const [testResult, setTestResult] = useState<TestResult | null>(null);
  const [busy, setBusy] = useState(false);

  const loadProvider = (c: RelayConfig, p: string) => {
    setProvider(p);
    const vals: Record<string, string> = {};
    for (const f of c.fields) vals[f.name] = f.secret ? "" : f.value;
    setValues(vals);
  };

  useEffect(() => {
    api.relay().then((c) => { setCfg(c); loadProvider(c, c.provider); });
  }, []);

  // When the provider dropdown changes, refetch field schema for that provider by saving locally:
  // we keep the field list from the server for the current provider; switching just resets inputs.
  const onProviderChange = (p: string) => {
    setProvider(p);
    setValues({});
    setSaveMsg(null);
  };

  const save = async () => {
    setBusy(true); setSaveMsg(null);
    try {
      await api.saveRelay(provider, values);
      const fresh = await api.relay();
      setCfg(fresh); loadProvider(fresh, fresh.provider);
      setSaveMsg("Saved.");
    } catch (e) {
      setSaveMsg(`Error: ${(e as Error).message}`);
    } finally { setBusy(false); }
  };

  const sendTest = async () => {
    setBusy(true); setTestResult(null);
    try { setTestResult(await api.testRelay(testTo)); }
    catch (e) { setTestResult({ ok: false, error: (e as Error).message }); }
    finally { setBusy(false); }
  };

  if (!cfg) return <div className="center">Loading…</div>;

  // Show the field list only when the selected provider matches the server's (so we have its schema);
  // otherwise prompt the user to save to load that provider's fields.
  const fields = provider === cfg.provider ? cfg.fields : [];
  const switching = provider !== cfg.provider;

  return (
    <>
      <h1 className="page-title">Settings — Relay Provider</h1>

      <div className="panel" style={{ maxWidth: 560 }}>
        <h2>Provider</h2>
        <select value={provider} onChange={(e) => onProviderChange(e.target.value)}>
          {cfg.providers.map((p) => <option key={p} value={p}>{p}</option>)}
        </select>

        {switching && (
          <p className="muted" style={{ marginTop: 14 }}>
            Switching provider to <b>{provider}</b>. Click Save to apply, then enter its credentials.
          </p>
        )}

        {fields.map((f) => (
          <div key={f.name} style={{ marginTop: 14 }}>
            <label className="muted" style={{ display: "block", marginBottom: 4, fontSize: 12 }}>
              {f.name}{f.required ? " *" : ""}
            </label>
            <input
              style={{ width: "100%" }}
              type={f.secret ? "password" : "text"}
              placeholder={f.secret && f.hasValue ? "•••••••• (unchanged)" : ""}
              value={values[f.name] ?? ""}
              onChange={(e) => setValues({ ...values, [f.name]: e.target.value })}
            />
          </div>
        ))}

        <div style={{ marginTop: 18, display: "flex", gap: 10, alignItems: "center" }}>
          <button onClick={save} disabled={busy}>Save settings</button>
          {saveMsg && <span className={saveMsg.startsWith("Error") ? "badge error" : "badge ok"}>{saveMsg}</span>}
        </div>
      </div>

      <div className="panel" style={{ maxWidth: 560 }}>
        <h2>Send test email</h2>
        <div style={{ display: "flex", gap: 10 }}>
          <input
            style={{ flex: 1 }}
            placeholder="recipient@example.com"
            value={testTo}
            onChange={(e) => setTestTo(e.target.value)}
          />
          <button onClick={sendTest} disabled={busy || !testTo}>Send test</button>
        </div>
        {testResult && (
          <div style={{ marginTop: 14 }}>
            {testResult.ok
              ? <span className="badge ok">Sent via {testResult.provider}{testResult.providerMessageId ? ` · ${testResult.providerMessageId}` : ""}</span>
              : <span className="badge error">Failed: {testResult.error}</span>}
          </div>
        )}
      </div>
    </>
  );
}
