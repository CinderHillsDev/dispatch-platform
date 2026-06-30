import { useEffect, useState } from "react";
import { api, type RelayDetail, type RelayListItem, type TestResult } from "../lib/api";
import { PROVIDER_FIELDS, PROVIDER_LABELS, PROVIDER_ORDER, PROVIDER_DOCS, PROVIDER_BRAND, azureMailFromSuggestions } from "../lib/providers";
import { ProviderFieldsInput } from "../ProviderFields";
import { TestResultView } from "../TestResultView";
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
  const [testFrom, setTestFrom] = useState("");
  const [testResult, setTestResult] = useState<TestResult | null>(null);

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
      // Azure rejects any sender that isn't a verified MailFrom — default the test From to the first one defined.
      const sug = provider === "AzureCommunication" ? azureMailFromSuggestions(values["MailFrom"]) : [];
      if (sug.length > 0) setTestFrom(sug[0]);
      setStep("test");
    } catch (e) { setErr((e as Error).message); }
    finally { setBusy(false); }
  };

  const sendTest = async () => {
    if (!createdId) return;
    setBusy(true); setTestResult(null);
    try {
      setTestResult(await api.relays.test(createdId, testTo.trim(), testFrom.trim()));
    } catch (e) { setTestResult({ ok: false, error: (e as Error).message }); }
    finally { setBusy(false); }
  };

  const title = step === "provider" ? "Add relay · choose a provider"
    : step === "credentials" ? `Add relay · ${PROVIDER_LABELS[provider!] ?? provider}`
    : "Add relay · test";

  return (
    <Modal title={title} onClose={onClose}>
      {step === "provider" && (
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(150px, 1fr))", gap: 8 }}>
          {PROVIDER_ORDER.map((p) => {
            const b = PROVIDER_BRAND[p];
            return (
              <button key={p} onClick={() => pick(p)}
                style={{ display: "flex", alignItems: "center", gap: 10, textAlign: "left", padding: "10px 12px", border: "1px solid var(--border)", borderRadius: 8, background: "var(--panel-2)" }}>
                {b && (
                  <span style={{
                    flexShrink: 0, width: 30, height: 30, borderRadius: 7, background: b.bg, color: b.fg ?? "#fff",
                    display: "flex", alignItems: "center", justifyContent: "center", fontSize: 10, fontWeight: 700, letterSpacing: ".02em",
                  }}>{b.mark}</span>
                )}
                <span style={{ fontSize: 13, lineHeight: 1.2 }}>{PROVIDER_LABELS[p] ?? p}</span>
              </button>
            );
          })}
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
          <Lbl label="From address">
            <FromField suggestions={provider === "AzureCommunication" ? azureMailFromSuggestions(values["MailFrom"]) : []}
              value={testFrom} onChange={setTestFrom} />
          </Lbl>
          <p className="muted" style={{ fontSize: 12, margin: "-4px 0 0" }}>
            {provider === "AzureCommunication" && azureMailFromSuggestions(values["MailFrom"]).length > 0
              ? <>Azure only accepts mail from a <strong>verified MailFrom</strong> — pick one of the senders you defined.</>
              : <>Most providers only accept mail from a domain you've <strong>verified</strong> with them — use an address on that domain or the test will be rejected.</>}
          </p>
          <Lbl label="Send a test to">
            <input type="email" placeholder="you@example.com" value={testTo} onChange={(e) => setTestTo(e.target.value)} style={{ width: "100%" }} />
          </Lbl>
          <div>
            <button onClick={sendTest} disabled={busy || !testTo.trim()}>{busy ? "Sending…" : "Send test"}</button>
          </div>
          {testResult && <TestResultView result={testResult} />}
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
  const [from, setFrom] = useState("");
  const [fromSuggestions, setFromSuggestions] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<TestResult | null>(null);

  // Azure rejects any sender that isn't a verified MailFrom — load the relay's defined senders so the From
  // becomes a dropdown of valid choices (the list page doesn't carry settings, so fetch the detail).
  useEffect(() => {
    if (relay.provider !== "AzureCommunication") return;
    let cancelled = false;
    api.relays.get(relay.id).then((d) => {
      if (cancelled) return;
      const allowed = d.fields.find((f) => f.name === "MailFrom")?.value;
      const sug = azureMailFromSuggestions(allowed);
      setFromSuggestions(sug);
      if (sug.length > 0) setFrom(sug[0]);
    }).catch(() => {});
    return () => { cancelled = true; };
  }, [relay.id, relay.provider]);

  const send = async () => {
    setBusy(true); setResult(null);
    try { setResult(await api.relays.test(relay.id, to.trim(), from.trim())); }
    catch (e) { setResult({ ok: false, error: (e as Error).message }); }
    finally { setBusy(false); }
  };

  return (
    <Modal title={`Send test · ${relay.name}`} onClose={onClose}>
      <div style={{ display: "grid", gap: 12 }}>
        <p className="muted" style={{ fontSize: 13, margin: 0 }}>Sends a real test message through this relay's saved credentials.</p>
        <Lbl label="From address">
          <FromField suggestions={fromSuggestions} value={from} onChange={setFrom} />
        </Lbl>
        <p className="muted" style={{ fontSize: 12, margin: "-6px 0 0" }}>
          {fromSuggestions.length > 0
            ? <>Azure only accepts mail from a <strong>verified MailFrom</strong> — pick one of the senders you defined.</>
            : <>Most providers only accept mail from a domain you've <strong>verified</strong> with them — use an address on that domain.</>}
        </p>
        <Lbl label="Send test to"><input type="email" value={to} onChange={(e) => setTo(e.target.value)} placeholder="you@example.com" style={{ width: "100%" }} /></Lbl>
        <div style={{ display: "flex", gap: 8, justifyContent: "flex-end" }}>
          <button onClick={onClose}>Close</button>
          <button onClick={send} disabled={busy || !to.trim()}>{busy ? "Sending…" : "Send test"}</button>
        </div>
        {result && <TestResultView result={result} />}
      </div>
    </Modal>
  );
}

// The "From" input for a test send. For Azure with verified MailFroms defined, it becomes a dropdown of those
// addresses (Azure rejects any other sender) — otherwise a free-text email box. `suggestions` drives the choice.
function FromField({ suggestions, value, onChange }: {
  suggestions: string[]; value: string; onChange: (v: string) => void;
}) {
  if (suggestions.length > 0) {
    return (
      <select value={value} onChange={(e) => onChange(e.target.value)} style={{ width: "100%" }}>
        {suggestions.map((s) => <option key={s} value={s}>{s}</option>)}
      </select>
    );
  }
  return (
    <input type="email" placeholder="sender@your-verified-domain.com" value={value}
      onChange={(e) => onChange(e.target.value)} style={{ width: "100%" }} />
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

function RelayEditor({ relay, onChanged, setMsg }: {
  relay: RelayDetail;
  onChanged: () => Promise<void>;
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
          <label className="muted" style={{ display: "block", fontSize: 12, marginBottom: 4 }}>{f.label ?? f.name}{f.required ? " *" : ""}</label>
          {f.options
            ? (
              <select style={{ width: "100%" }} value={values[f.name] ?? ""} onChange={(e) => setValues({ ...values, [f.name]: e.target.value })}>
                <option value="">{f.required ? "— select —" : "(default)"}</option>
                {f.options.map((o) => <option key={o} value={o}>{o}</option>)}
              </select>
            )
            : (
              <input
                style={{ width: "100%" }}
                type={f.secret ? "password" : "text"}
                placeholder={f.secret && secretHasValue(f.name) ? "•••••••• (unchanged)" : f.placeholder}
                value={values[f.name] ?? ""}
                onChange={(e) => setValues({ ...values, [f.name]: e.target.value })}
                // Provider credentials, not the user's login — suppress browser/password-manager save+autofill.
                autoComplete={f.secret ? "new-password" : "off"}
                spellCheck={false}
                data-1p-ignore
                data-lpignore="true"
                data-bwignore
                data-form-type="other"
              />
            )}
          {f.help && <div className="muted" style={{ fontSize: 11, marginTop: 4 }}>{f.help}</div>}
        </div>
      ))}

      <label style={{ display: "flex", gap: 8, alignItems: "center", margin: "8px 0 14px" }}>
        <input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} style={{ width: "auto" }} /> Enabled
      </label>

      <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
        <button onClick={save} disabled={busy}>Save</button>
      </div>
    </>
  );
}
