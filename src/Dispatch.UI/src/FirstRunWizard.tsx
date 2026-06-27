import { useEffect, useState, type CSSProperties, type ReactNode } from "react";
import { api, type TestResult } from "./lib/api";
import { PROVIDER_FIELDS, PROVIDER_LABELS, PROVIDER_ORDER } from "./lib/providers";
import { ProviderFieldsInput } from "./ProviderFields";
import { TestResultView } from "./TestResultView";
import { validCidr } from "./lib/cidr";

type Step = "welcome" | "provider" | "test" | "routing" | "access" | "done";

export function FirstRunWizard({ onDone }: { onDone: () => void }) {
  const [step, setStep] = useState<Step>("welcome");
  const [catchAll, setCatchAll] = useState<{ id: number; provider: string } | null>(null);

  const steps: { id: Step; label: string }[] = [
    { id: "welcome", label: "Welcome" },
    { id: "provider", label: "Provider" },
    { id: "test", label: "Test" },
    { id: "routing", label: "Routing" },
    { id: "access", label: "Access" },
  ];
  const activeIdx = steps.findIndex((s) => s.id === step);

  return (
    <div style={overlay}>
      <div style={card}>
        <div style={{ display: "flex", alignItems: "center", gap: 10, marginBottom: 4 }}>
          <span style={{ width: 10, height: 10, borderRadius: 999, background: "var(--blue)" }} />
          <strong style={{ fontSize: 18 }}>Dispatch setup</strong>
          <button onClick={onDone} style={{ marginLeft: "auto", background: "transparent", border: "none", color: "var(--muted)" }}>
            Skip setup
          </button>
        </div>

        {step !== "done" && (
          <div style={{ display: "flex", gap: 6, margin: "14px 0 20px" }}>
            {steps.map((s, i) => (
              <div key={s.id} style={{ flex: 1, textAlign: "center" }}>
                <div style={{ height: 3, borderRadius: 2, background: i <= activeIdx ? "var(--blue)" : "var(--border)" }} />
                <div style={{ fontSize: 11, marginTop: 6, color: i === activeIdx ? "var(--text)" : "var(--muted)" }}>{s.label}</div>
              </div>
            ))}
          </div>
        )}

        {step === "welcome" && <Welcome onNext={() => setStep("provider")} />}
        {step === "provider" && (
          <ProviderStep
            onBack={() => setStep("welcome")}
            onDone={(id, provider) => { setCatchAll({ id, provider }); setStep(provider === "Local" ? "routing" : "test"); }}
          />
        )}
        {step === "test" && catchAll && (
          <TestStep relayId={catchAll.id} onBack={() => setStep("provider")} onNext={() => setStep("routing")} />
        )}
        {step === "routing" && catchAll && (
          <RoutingStep catchAllProvider={catchAll.provider} onBack={() => setStep("test")} onNext={() => setStep("access")} />
        )}
        {step === "access" && <AccessStep onBack={() => setStep("routing")} onNext={() => setStep("done")} />}
        {step === "done" && <Done onFinish={onDone} />}
      </div>
    </div>
  );
}

// ---- Steps -----------------------------------------------------------------------------------

function Welcome({ onNext }: { onNext: () => void }) {
  return (
    <>
      <h2 style={h2}>Welcome 👋</h2>
      <p style={p}>
        Dispatch is your own SMTP relay: point your apps and devices at it, and it forwards every message
        to an email provider — with a durable queue and this dashboard to watch it all.
      </p>
      <p style={p}>Let's connect your first provider. It takes about a minute.</p>
      <Nav right={<button onClick={onNext}>Get started →</button>} />
    </>
  );
}

function ProviderStep({ onDone, onBack }: { onDone: (id: number, provider: string) => void; onBack: () => void }) {
  const [provider, setProvider] = useState("Mailgun");
  const [values, setValues] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const fields = PROVIDER_FIELDS[provider] ?? [];

  const missing = fields.filter((f) => f.required && !values[f.name]?.trim()).map((f) => f.name);

  const create = async () => {
    setBusy(true); setErr(null);
    try {
      const name = PROVIDER_LABELS[provider] ?? provider;
      const { id } = await api.relays.create(name, provider);
      await api.relays.update(id, { name, provider, enabled: true, maxConcurrency: 4, settings: values });
      onDone(id, provider);
    } catch (e) { setErr((e as Error).message); }
    finally { setBusy(false); }
  };

  return (
    <>
      <h2 style={h2}>Connect your email provider</h2>
      <p style={p}>This becomes your <strong>catch-all</strong> relay — every message goes through it unless a routing rule says otherwise.</p>

      <label style={lbl}>Provider</label>
      <select value={provider} onChange={(e) => { setProvider(e.target.value); setValues({}); }} style={{ width: "100%" }}>
        {PROVIDER_ORDER.map((p) => <option key={p} value={p}>{PROVIDER_LABELS[p] ?? p}</option>)}
      </select>

      <ProviderFieldsInput fields={fields} values={values} onChange={setValues} />
      {provider === "Local" && <p style={{ ...p, fontSize: 13 }}>Local mode captures mail to the dashboard and never delivers externally — handy for testing.</p>}

      {err && <p style={{ color: "var(--red)", fontSize: 13 }}>{err}</p>}
      <Nav
        left={<button onClick={onBack} style={ghostBtn}>← Back</button>}
        right={<button onClick={create} disabled={busy || missing.length > 0}>{busy ? "Saving…" : "Continue →"}</button>}
      />
      {missing.length > 0 && <p style={{ fontSize: 12, color: "var(--muted)", textAlign: "right", margin: "6px 0 0" }}>Required: {missing.join(", ")}</p>}
    </>
  );
}

function TestStep({ relayId, onBack, onNext }: { relayId: number; onBack: () => void; onNext: () => void }) {
  const [to, setTo] = useState("");
  const [from, setFrom] = useState("");
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<TestResult | null>(null);

  const send = async () => {
    setBusy(true); setResult(null);
    try { setResult(await api.relays.test(relayId, to.trim(), from.trim())); }
    catch (e) { setResult({ ok: false, error: (e as Error).message }); }
    finally { setBusy(false); }
  };

  return (
    <>
      <h2 style={h2}>Send a test email</h2>
      <p style={p}>Optional, but worth it — confirm your credentials actually deliver before you rely on them.</p>
      <label style={lbl}>From address</label>
      <input type="email" placeholder="sender@your-verified-domain.com" value={from} onChange={(e) => setFrom(e.target.value)} style={{ width: "100%" }} />
      <p style={{ ...p, fontSize: 12, margin: "4px 0 0" }}>Most providers only accept mail from a domain you've <strong>verified</strong> with them.</p>
      <label style={lbl}>Send a test to</label>
      <input type="email" placeholder="you@example.com" value={to} onChange={(e) => setTo(e.target.value)} style={{ width: "100%" }} />
      <div style={{ margin: "10px 0" }}>
        <button onClick={send} disabled={busy || !to.trim()}>{busy ? "Sending…" : "Send test"}</button>
      </div>
      {result && <TestResultView result={result} />}
      <Nav
        left={<button onClick={onBack} style={ghostBtn}>← Back</button>}
        right={<button onClick={onNext}>{result?.ok ? "Continue →" : "Skip →"}</button>}
      />
    </>
  );
}

function RoutingStep({ catchAllProvider, onBack, onNext }: { catchAllProvider: string; onBack: () => void; onNext: () => void }) {
  const [adding, setAdding] = useState(false);
  const [added, setAdded] = useState<{ domain: string; provider: string }[]>([]);
  const [domain, setDomain] = useState("");
  const [provider, setProvider] = useState("SendGrid");
  const [values, setValues] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const fields = PROVIDER_FIELDS[provider] ?? [];
  const missing = !domain.trim() || fields.some((f) => f.required && !values[f.name]?.trim());

  const addRule = async () => {
    setBusy(true); setErr(null);
    try {
      const label = `${PROVIDER_LABELS[provider] ?? provider} — ${domain.trim()}`;
      const { id } = await api.relays.create(label, provider);
      await api.relays.update(id, { name: label, provider, enabled: true, maxConcurrency: 4, settings: values });
      await api.rules.create({ name: `Mail to ${domain.trim()}`, recipientPattern: domain.trim(), senderPattern: null, relayId: id });
      setAdded([...added, { domain: domain.trim(), provider }]);
      setAdding(false); setDomain(""); setValues({});
    } catch (e) { setErr((e as Error).message); }
    finally { setBusy(false); }
  };

  return (
    <>
      <h2 style={h2}>Routing rules <span style={{ fontWeight: 400, color: "var(--muted)" }}>(optional)</span></h2>
      <p style={p}>
        Right now <strong>all mail</strong> goes through your catch-all ({PROVIDER_LABELS[catchAllProvider] ?? catchAllProvider}).
        Most setups stop here. If you want mail to a specific domain to use a <em>different</em> provider, add a rule —
        otherwise just finish.
      </p>

      {added.length > 0 && (
        <ul style={{ margin: "0 0 12px", paddingLeft: 18, fontSize: 13 }}>
          {added.map((a, i) => <li key={i}>Mail to <strong>{a.domain}</strong> → {PROVIDER_LABELS[a.provider] ?? a.provider}</li>)}
        </ul>
      )}

      {!adding && <button onClick={() => setAdding(true)} style={ghostBtn}>+ Add a routing rule</button>}

      {adding && (
        <div className="panel" style={{ marginTop: 12 }}>
          <label style={lbl}>Send mail addressed to (domain or pattern, e.g. *.acme.com)</label>
          <input value={domain} onChange={(e) => setDomain(e.target.value)} placeholder="acme.com" style={{ width: "100%" }} />
          <label style={lbl}>…through this provider</label>
          <select value={provider} onChange={(e) => { setProvider(e.target.value); setValues({}); }} style={{ width: "100%" }}>
            {PROVIDER_ORDER.map((p) => <option key={p} value={p}>{PROVIDER_LABELS[p] ?? p}</option>)}
          </select>
          <ProviderFieldsInput fields={fields} values={values} onChange={setValues} />
          {err && <p style={{ color: "var(--red)", fontSize: 13 }}>{err}</p>}
          <div style={{ display: "flex", gap: 8, marginTop: 10 }}>
            <button onClick={addRule} disabled={busy || missing}>{busy ? "Adding…" : "Add rule"}</button>
            <button onClick={() => setAdding(false)} style={ghostBtn}>Cancel</button>
          </div>
        </div>
      )}

      <Nav
        left={<button onClick={onBack} style={ghostBtn}>← Back</button>}
        right={<button onClick={onNext}>{added.length ? "Continue →" : "Skip →"}</button>}
      />
    </>
  );
}

function AccessStep({ onBack, onNext }: { onBack: () => void; onNext: () => void }) {
  const [loaded, setLoaded] = useState(false);
  const [smtp, setSmtp] = useState<string[]>([]);
  const [http, setHttp] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);

  useEffect(() => {
    api.settings.config()
      .then((c) => { setSmtp(c.listener.allowedCidrs); setHttp(c.api.allowedCidrs); })
      .catch(() => {})
      .finally(() => setLoaded(true));
  }, []);

  const save = async () => {
    setBusy(true); setErr(null);
    try {
      await api.settings.putListener({ allowedCidrs: smtp });
      await api.settings.putApi({ allowedCidrs: http });
      onNext();
    } catch (e) { setErr((e as Error).message); }
    finally { setBusy(false); }
  };

  if (!loaded) return <p style={p}>Loading current access settings…</p>;

  const anyEmpty = smtp.length === 0 || http.length === 0;

  return (
    <>
      <h2 style={h2}>Who can connect?</h2>
      <p style={p}>
        Dispatch is <strong>closed by default</strong>: only the network ranges (CIDRs) you list here may
        connect — everything else is refused, so you never run an open relay by accident. We've pre-filled
        your private/loopback networks; adjust them to match where your apps and devices live.
      </p>
      <p style={{ ...p, color: "var(--amber)", fontSize: 13 }}>
        ⚠ Leave a list empty and <strong>nothing</strong> will be able to reach that endpoint. To allow any
        source, add <code>0.0.0.0/0</code> (and <code>::/0</code>) on purpose.
      </p>

      <CidrList label="SMTP listener" intro="Hosts allowed to submit mail over SMTP." list={smtp} onChange={setSmtp} />
      <CidrList label="HTTP API" intro="Hosts allowed to call the ingestion API (also key-protected)." list={http} onChange={setHttp} />

      {anyEmpty && (
        <p style={{ fontSize: 12, color: "var(--amber)", margin: "10px 0 0" }}>
          One or more lists is empty — that endpoint will reject all connections until you add a range.
        </p>
      )}
      {err && <p style={{ color: "var(--red)", fontSize: 13 }}>{err}</p>}
      <Nav
        left={<button onClick={onBack} style={ghostBtn}>← Back</button>}
        right={<button onClick={save} disabled={busy}>{busy ? "Saving…" : "Save & continue →"}</button>}
      />
    </>
  );
}

function CidrList({ label, intro, list, onChange }: {
  label: string; intro: string; list: string[]; onChange: (next: string[]) => void;
}) {
  const [entry, setEntry] = useState("");
  const [err, setErr] = useState<string | null>(null);

  const add = () => {
    const v = entry.trim();
    if (!v) return;
    if (!validCidr(v)) { setErr("Enter a valid CIDR, e.g. 10.0.0.0/8 or 192.168.1.5/32."); return; }
    if (list.includes(v)) { setErr("That range is already in the list."); return; }
    setErr(null); onChange([...list, v]); setEntry("");
  };

  return (
    <div className="panel" style={{ marginTop: 12, marginBottom: 0 }}>
      <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between" }}>
        <strong style={{ fontSize: 14 }}>{label}</strong>
        <span className={list.length === 0 ? "badge denied" : "badge ok"}>
          {list.length === 0 ? "blocks all" : `${list.length} range${list.length === 1 ? "" : "s"}`}
        </span>
      </div>
      <p className="muted" style={{ fontSize: 12, margin: "4px 0 10px" }}>{intro}</p>
      <div style={{ display: "flex", flexWrap: "wrap", gap: 6, minHeight: 28 }}>
        {list.length === 0 && <span style={{ fontSize: 12, color: "var(--amber)", fontStyle: "italic", alignSelf: "center" }}>No ranges — all blocked.</span>}
        {list.map((c) => (
          <span key={c} style={{ display: "inline-flex", alignItems: "center", gap: 6, background: "var(--panel-2)", border: "1px solid var(--border)", borderRadius: 999, padding: "3px 4px 3px 10px", fontSize: 12 }}>
            <code style={{ background: "none", padding: 0 }}>{c}</code>
            <button onClick={() => onChange(list.filter((x) => x !== c))} title="Remove" style={{ border: "none", background: "transparent", padding: "0 3px", lineHeight: 1, fontSize: 14, color: "var(--muted)" }}>×</button>
          </span>
        ))}
      </div>
      <div style={{ display: "flex", gap: 6, marginTop: 10 }}>
        <input placeholder="Add a range, e.g. 10.0.0.0/8" value={entry}
          onChange={(e) => { setEntry(e.target.value); if (err) setErr(null); }}
          onKeyDown={(e) => { if (e.key === "Enter") add(); }} style={{ flex: 1 }} />
        <button onClick={add} disabled={!entry.trim()}>Add</button>
      </div>
      {err && <p style={{ color: "var(--red)", fontSize: 12, margin: "6px 0 0" }}>{err}</p>}
    </div>
  );
}

function Done({ onFinish }: { onFinish: () => void }) {
  return (
    <>
      <h2 style={h2}>You're all set 🎉</h2>
      <p style={p}>
        Dispatch is ready. Point your apps at the SMTP listener (ports 25 and 587 by default, or 2525 if 25
        is already in use) or the HTTP API, and watch messages flow on the dashboard. You can change anything
        later under Relays, Routing, and Settings.
      </p>
      <Nav right={<button onClick={onFinish}>Go to dashboard →</button>} />
    </>
  );
}

// ---- Shared bits -----------------------------------------------------------------------------

function Nav({ left, right }: { left?: ReactNode; right?: ReactNode }) {
  return (
    <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", marginTop: 22 }}>
      <span>{left}</span><span>{right}</span>
    </div>
  );
}

const overlay: CSSProperties = {
  position: "fixed", inset: 0, background: "rgba(0,0,0,.6)", display: "flex",
  alignItems: "center", justifyContent: "center", zIndex: 1000, padding: 20,
};
const card: CSSProperties = {
  background: "var(--panel)", border: "1px solid var(--border)", borderRadius: 14,
  padding: 24, width: 520, maxWidth: "100%", maxHeight: "90vh", overflowY: "auto",
};
const h2: CSSProperties = { fontSize: 18, margin: "0 0 10px", textTransform: "none", letterSpacing: 0, color: "var(--text)" };
const p: CSSProperties = { color: "var(--muted)", fontSize: 14, lineHeight: 1.5, margin: "0 0 12px" };
const lbl: CSSProperties = { display: "block", fontSize: 13, margin: "12px 0 4px" };
const ghostBtn: CSSProperties = { background: "transparent" };
