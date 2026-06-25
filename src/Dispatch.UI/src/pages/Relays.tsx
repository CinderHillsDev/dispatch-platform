import { useEffect, useRef, useState } from "react";
import { api, type RelayDetail, type RelayListItem, type TestResult, type TestRunLine } from "../lib/api";
import { createTestProviderConnection } from "../lib/signalr";
import { PROVIDER_FIELDS, PROVIDER_LABELS, PROVIDER_ORDER, PROVIDER_DOCS } from "../lib/providers";
import { ProviderFieldsInput } from "../ProviderFields";
import { Modal } from "../Modal";
import { ActionsMenu } from "../ActionsMenu";

// One-click SMTP presets — pick a provider and host/port/TLS are filled in; just add credentials. Almost
// every provider offers SMTP, so this covers the long tail without a native integration for each.
const SMTP_PRESETS: Record<string, { Host: string; Port: string; TlsMode: string }> = {
  "Amazon SES (us-east-1)": { Host: "email-smtp.us-east-1.amazonaws.com", Port: "587", TlsMode: "StartTls" },
  "Brevo": { Host: "smtp-relay.brevo.com", Port: "587", TlsMode: "StartTls" },
  "Gmail / Google Workspace": { Host: "smtp.gmail.com", Port: "587", TlsMode: "StartTls" },
  "Mailjet": { Host: "in-v3.mailjet.com", Port: "587", TlsMode: "StartTls" },
  "Microsoft 365": { Host: "smtp.office365.com", Port: "587", TlsMode: "StartTls" },
  "Postmark": { Host: "smtp.postmarkapp.com", Port: "587", TlsMode: "StartTls" },
  "Resend": { Host: "smtp.resend.com", Port: "465", TlsMode: "SslOnConnect" },
  "SendGrid": { Host: "smtp.sendgrid.net", Port: "587", TlsMode: "StartTls" },
  "SMTP2GO": { Host: "mail.smtp2go.com", Port: "587", TlsMode: "StartTls" },
  "SparkPost": { Host: "smtp.sparkpostmail.com", Port: "587", TlsMode: "StartTls" },
};

export function Relays() {
  const [list, setList] = useState<RelayListItem[]>([]);
  const [selected, setSelected] = useState<RelayDetail | null>(null);
  const [adding, setAdding] = useState(false);
  const [testing, setTesting] = useState<RelayListItem | null>(null);
  const [msg, setMsg] = useState<string | null>(null);

  const refresh = async () => setList(await api.relays.list());
  useEffect(() => { refresh(); }, []);

  const select = async (id: number) => { setMsg(null); setSelected(await api.relays.get(id)); };

  const del = async (r: RelayListItem) => {
    if (!confirm(`Delete relay “${r.name}”? This can't be undone.`)) return;
    try { await api.relays.remove(r.id); await refresh(); setMsg("Relay deleted."); }
    catch (e) { setMsg(`Error: ${(e as Error).message}`); }
  };

  return (
    <div style={{ maxWidth: 960 }}>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 12 }}>
        <h1 className="page-title" style={{ margin: 0 }}>Relays</h1>
        <button onClick={() => setAdding(true)}>+ Add relay</button>
      </div>
      <p className="muted" style={{ fontSize: 13, margin: "8px 0 18px" }}>
        A relay is an upstream provider Dispatch delivers through. The <strong>catch-all</strong> relay (chosen
        on the <strong>Routing</strong> page) handles any mail that no rule matches.
      </p>

      <div className="panel" style={{ padding: 0 }}>
        <table>
          <thead><tr><th>Name</th><th>Provider</th><th>Status</th><th></th></tr></thead>
          <tbody>
            {list.map((r) => (
              <tr key={r.id}>
                <td>{r.name}</td>
                <td>{PROVIDER_LABELS[r.provider] ?? r.provider}</td>
                <td>{r.enabled ? "Enabled" : <span className="muted">Disabled</span>}</td>
                <td style={{ textAlign: "right" }}>
                  <ActionsMenu items={[
                    { label: "Edit", onClick: () => select(r.id) },
                    { label: "Send test", onClick: () => setTesting(r) },
                    { label: "Delete", danger: true, disabled: r.isDefault, onClick: () => del(r) },
                  ]} />
                </td>
              </tr>
            ))}
            {list.length === 0 && <tr><td colSpan={4} className="center">No relays yet — click “Add relay”.</td></tr>}
          </tbody>
        </table>
      </div>

      {adding && <AddRelayModal onClose={() => setAdding(false)} onAdded={refresh} />}
      {testing && <TestRelayModal relay={testing} onClose={() => setTesting(null)} />}

      {selected && (
        <Modal
          title={`Edit · ${selected.name}${selected.isDefault ? " (catch-all)" : ""}`}
          onClose={() => setSelected(null)}
        >
          <RelayEditor
            key={selected.id}
            relay={selected}
            onChanged={async () => { await refresh(); await select(selected.id); }}
            onDeleted={async () => { setSelected(null); await refresh(); }}
            setMsg={setMsg}
          />
        </Modal>
      )}
      {msg && <p style={{ marginTop: 16 }}><span className={msg.startsWith("Error") ? "badge error" : "badge ok"}>{msg}</span></p>}
    </div>
  );
}

// "Add relay" dialog: pick a provider and fill its credentials in one step (like the first-run wizard),
// instead of the old free-text-name + dropdown row. Creates the relay and saves its settings.
// Stepped add-relay wizard: choose provider → enter credentials (with help link + region dropdowns) →
// optional send-test.
function AddRelayModal({ onClose, onAdded }: { onClose: () => void; onAdded: () => Promise<void> }) {
  const [step, setStep] = useState<"provider" | "credentials" | "test">("provider");
  const [provider, setProvider] = useState<string | null>(null);
  const [name, setName] = useState("");
  const [values, setValues] = useState<Record<string, string>>({});
  const [createdId, setCreatedId] = useState<number | null>(null);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [testTo, setTestTo] = useState("");
  const [testResult, setTestResult] = useState<{ ok: boolean; detail: string } | null>(null);

  const fields = provider ? (PROVIDER_FIELDS[provider] ?? []) : [];
  const missing = fields.filter((f) => f.required && !values[f.name]?.trim()).map((f) => f.name);

  const pick = (p: string) => { setProvider(p); setValues({}); setName(""); setErr(null); setStep("credentials"); };

  const create = async () => {
    if (!provider) return;
    setBusy(true); setErr(null);
    try {
      const finalName = name.trim() || (PROVIDER_LABELS[provider] ?? provider);
      const { id } = await api.relays.create(finalName, provider);
      await api.relays.update(id, { name: finalName, provider, enabled: true, maxConcurrency: 4, settings: values });
      setCreatedId(id);
      await onAdded();
      setStep("test");
    } catch (e) { setErr((e as Error).message); }
    finally { setBusy(false); }
  };

  const sendTest = async () => {
    if (!createdId) return;
    setBusy(true); setTestResult(null);
    try {
      const r = await api.relays.test(createdId, testTo.trim());
      setTestResult({ ok: r.ok, detail: r.ok ? (r.detail ?? "Delivered to the provider.") : (r.error ?? "Failed.") });
    } catch (e) { setTestResult({ ok: false, detail: (e as Error).message }); }
    finally { setBusy(false); }
  };

  const title = step === "provider" ? "Add relay · choose a provider"
    : step === "credentials" ? `Add relay · ${PROVIDER_LABELS[provider!] ?? provider}`
    : "Add relay · test";

  return (
    <Modal title={title} onClose={onClose}>
      {step === "provider" && (
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(150px, 1fr))", gap: 8 }}>
          {PROVIDER_ORDER.map((p) => (
            <button key={p} onClick={() => pick(p)}
              style={{ textAlign: "left", padding: "12px 14px", border: "1px solid var(--border)", borderRadius: 8, background: "var(--panel-2)" }}>
              {PROVIDER_LABELS[p] ?? p}
            </button>
          ))}
        </div>
      )}

      {step === "credentials" && provider && (
        <div style={{ display: "grid", gap: 6 }}>
          <Lbl label="Name (optional)">
            <input value={name} onChange={(e) => setName(e.target.value)} placeholder={PROVIDER_LABELS[provider] ?? provider} style={{ width: "100%" }} />
          </Lbl>
          <ProviderFieldsInput fields={fields} values={values} onChange={setValues} />
          {PROVIDER_DOCS[provider] && (
            <a href={PROVIDER_DOCS[provider]} target="_blank" rel="noreferrer" style={{ fontSize: 12 }}>
              Where do I find these? ↗
            </a>
          )}
          {err && <p style={{ color: "var(--red)", fontSize: 13, margin: 0 }}>{err}</p>}
          <div style={{ display: "flex", gap: 8, justifyContent: "space-between", alignItems: "center", marginTop: 8 }}>
            <button onClick={() => setStep("provider")} style={{ background: "transparent" }}>← Back</button>
            <button onClick={create} disabled={busy || missing.length > 0}>{busy ? "Creating…" : "Create relay →"}</button>
          </div>
          {missing.length > 0 && <p className="muted" style={{ fontSize: 12, textAlign: "right", margin: 0 }}>Required: {missing.join(", ")}</p>}
        </div>
      )}

      {step === "test" && (
        <div style={{ display: "grid", gap: 10 }}>
          <p className="muted" style={{ fontSize: 13, margin: 0 }}>
            Relay created 🎉 Send a quick test to confirm it delivers — or just finish.
          </p>
          <Lbl label="Send a test to">
            <input type="email" placeholder="you@example.com" value={testTo} onChange={(e) => setTestTo(e.target.value)} style={{ width: "100%" }} />
          </Lbl>
          <div style={{ display: "flex", gap: 8, alignItems: "center" }}>
            <button onClick={sendTest} disabled={busy || !testTo.trim()}>{busy ? "Sending…" : "Send test"}</button>
            {testResult && <span className={testResult.ok ? "badge ok" : "badge error"}>{testResult.ok ? "Delivered" : "Failed"}</span>}
          </div>
          {testResult && <p className="muted" style={{ fontSize: 13, margin: 0, wordBreak: "break-word" }}>{testResult.detail}</p>}
          <div style={{ display: "flex", justifyContent: "flex-end", marginTop: 6 }}>
            <button onClick={onClose}>Finish</button>
          </div>
        </div>
      )}
    </Modal>
  );
}

// Quick "Send test" against a saved relay's stored credentials (the editor's streaming test covers
// unsaved edits; this is the one-click version from the row menu).
function TestRelayModal({ relay, onClose }: { relay: RelayListItem; onClose: () => void }) {
  const [to, setTo] = useState("");
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<TestResult | null>(null);

  const send = async () => {
    setBusy(true); setResult(null);
    try { setResult(await api.relays.test(relay.id, to.trim())); }
    catch (e) { setResult({ ok: false, error: (e as Error).message }); }
    finally { setBusy(false); }
  };

  return (
    <Modal title={`Send test · ${relay.name}`} onClose={onClose}>
      <div style={{ display: "grid", gap: 12 }}>
        <p className="muted" style={{ fontSize: 13, margin: 0 }}>Sends a real test message through this relay's saved credentials.</p>
        <Lbl label="Send test to"><input type="email" value={to} onChange={(e) => setTo(e.target.value)} placeholder="you@example.com" style={{ width: "100%" }} /></Lbl>
        <div style={{ display: "flex", gap: 8, justifyContent: "flex-end" }}>
          <button onClick={onClose}>Close</button>
          <button onClick={send} disabled={busy || !to.trim()}>{busy ? "Sending…" : "Send test"}</button>
        </div>
        {result && (
          <p style={{ margin: 0 }}>
            <span className={result.ok ? "badge ok" : "badge error"}>{result.ok ? "Delivered" : "Failed"}</span>
            <span className="muted" style={{ marginLeft: 8, fontSize: 13 }}>{result.ok ? (result.detail ?? "Sent to the provider.") : (result.error ?? "Failed.")}</span>
          </p>
        )}
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

  // Live streaming provider test (spec §11) — tests the CURRENTLY ENTERED credentials, no save required.
  const [testLines, setTestLines] = useState<TestRunLine[]>([]);
  const [testStatus, setTestStatus] = useState<"" | "Running" | "Success" | "Failed">("");
  const [testStartedAt, setTestStartedAt] = useState(0);
  const testConn = useRef<ReturnType<typeof createTestProviderConnection> | null>(null);

  useEffect(() => () => { testConn.current?.stop(); }, []);

  const runStreamingTest = async () => {
    if (!testTo) return;
    setBusy(true);
    setTestLines([]);
    setTestStatus("Running");
    setTestStartedAt(Date.now());
    try {
      await testConn.current?.stop();
      const conn = createTestProviderConnection((line) => {
        setTestLines((prev) => [...prev, { ts: line.ts, level: line.level, message: line.message }]);
        if (line.level === "Success") setTestStatus("Success");
        if (line.level === "Failed") setTestStatus("Failed");
      });
      testConn.current = conn;
      await conn.start();

      const start = await api.config.testProvider(provider, values, testTo);
      await conn.invoke("Join", start.runId);

      // Poll as a fallback so the terminal status is reached even if a line is missed.
      const poll = setInterval(async () => {
        try {
          const run = await api.config.testProviderRun(start.runId);
          if (run.status !== "Running") {
            clearInterval(poll);
            setTestLines(run.lines);
            setTestStatus(run.status === "Success" ? "Success" : "Failed");
          }
        } catch { clearInterval(poll); }
      }, 750);
    } catch (e) {
      setTestLines((prev) => [...prev, { ts: new Date().toISOString(), level: "Failed", message: (e as Error).message }]);
      setTestStatus("Failed");
    } finally {
      setBusy(false);
    }
  };

  const elapsed = (ts: string) => testStartedAt ? `+${Math.max(0, (new Date(ts).getTime() - testStartedAt) / 1000).toFixed(3)}s` : "";

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
    <>

      <label className="muted" style={{ fontSize: 12 }}>Name</label>
      <input style={{ width: "100%", marginBottom: 12 }} value={name} onChange={(e) => setName(e.target.value)} />

      <label className="muted" style={{ fontSize: 12 }}>Provider</label>
      <select style={{ width: "100%", marginBottom: 12 }} value={provider} onChange={(e) => { setProvider(e.target.value); setValues({}); }}>
        {options.map((p) => <option key={p} value={p}>{p}</option>)}
      </select>
      {provider === "Unconfigured" && (
        <p className="muted" style={{ marginTop: -4, marginBottom: 12, fontSize: 12 }}>
          This relay has no provider yet — mail to it will fail until you choose one (or pick “Local” for development).
        </p>
      )}
      {provider === "Local" && (
        <p className="muted" style={{ marginTop: -4, marginBottom: 12, fontSize: 12 }}>
          Local / developer mode — never delivers externally. Captured messages appear in the Local Inbox.
        </p>
      )}

      {provider === "Smtp" && (
        <div style={{ marginBottom: 10 }}>
          <label className="muted" style={{ display: "block", fontSize: 12, marginBottom: 4 }}>Preset (optional)</label>
          <select
            style={{ width: "100%" }}
            defaultValue=""
            onChange={(e) => {
              const p = SMTP_PRESETS[e.target.value];
              if (p) setValues({ ...values, Host: p.Host, Port: p.Port, TlsMode: p.TlsMode });
            }}
          >
            <option value="">— pick a provider to fill host/port —</option>
            {Object.keys(SMTP_PRESETS).map((k) => <option key={k} value={k}>{k}</option>)}
          </select>
        </div>
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
        {!relay.isDefault && <button onClick={() => act(() => api.relays.remove(relay.id), "Deleted.").then(onDeleted)} disabled={busy}>Delete</button>}
      </div>

      <div style={{ marginTop: 16, borderTop: "1px solid var(--border)", paddingTop: 14 }}>
        <label className="muted" style={{ fontSize: 12 }}>Send test email</label>
        <p className="muted" style={{ marginTop: 2, marginBottom: 6, fontSize: 11 }}>
          Tests the credentials entered above — they do not need to be saved first.
        </p>
        <div style={{ display: "flex", gap: 8, marginTop: 6 }}>
          <input style={{ flex: 1 }} placeholder="recipient@example.com" value={testTo} onChange={(e) => setTestTo(e.target.value)} />
          <button disabled={busy || !testTo || testStatus === "Running"} onClick={runStreamingTest}>Send Test Email</button>
          <button disabled={busy || !testTo} onClick={async () => {
            setBusy(true); setTestResult(null);
            try { setTestResult(await api.relays.test(relay.id, testTo)); }
            catch (e) { setTestResult({ ok: false, error: (e as Error).message }); }
            finally { setBusy(false); }
          }}>Quick Test</button>
        </div>
        {testResult && (
          <div style={{ marginTop: 10 }}>
            {testResult.ok
              ? <span className="badge ok">Sent via {testResult.provider}</span>
              : <span className="badge error">Failed: {testResult.error}</span>}
          </div>
        )}

        {(testStatus || testLines.length > 0) && (
          <div style={{ marginTop: 12 }}>
            <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", marginBottom: 6 }}>
              <span className="muted" style={{ fontSize: 12 }}>Test Log</span>
              <button disabled={testStatus === "Running"} onClick={() => { setTestLines([]); setTestStatus(""); }} style={{ padding: "2px 8px", fontSize: 11 }}>Clear</button>
            </div>
            <div style={{
              fontFamily: "monospace", fontSize: 12, background: "var(--bg, #111)", border: "1px solid var(--border)",
              borderRadius: 4, padding: 8, maxHeight: 220, overflowY: "auto", whiteSpace: "pre-wrap",
            }}>
              {testLines.length === 0 && <div className="muted">Starting…</div>}
              {testLines.map((l, i) => (
                <div key={i} style={{ color: l.level === "Success" ? "#3fb950" : l.level === "Failed" || l.level === "Error" ? "#f85149" : undefined }}>
                  <span className="muted">{elapsed(l.ts)} </span>
                  <strong>{l.level === "Success" ? "✓ SUCCESS" : l.level === "Failed" ? "✗ FAILED" : l.level.toUpperCase()}</strong>{"  "}{l.message}
                </div>
              ))}
            </div>
          </div>
        )}
      </div>
    </>
  );
}
