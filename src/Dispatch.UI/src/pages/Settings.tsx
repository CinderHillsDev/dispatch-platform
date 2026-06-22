import { useEffect, useState, type ReactNode } from "react";
import { api, type AppSettings, type SystemConfig, type PurgeRun } from "../lib/api";
import { Modal } from "../Modal";

type Tab = "connections" | "delivery" | "storage";

export function Settings() {
  const [settings, setSettings] = useState<AppSettings | null>(null);
  const [sysConfig, setSysConfig] = useState<SystemConfig | null>(null);
  const [tab, setTab] = useState<Tab>("connections");

  useEffect(() => { api.settings.get().then(setSettings); }, []);
  useEffect(() => { api.settings.config().then(setSysConfig).catch(() => setSysConfig(null)); }, []);

  if (!settings) return <div className="center">Loading…</div>;

  const tabs: { id: Tab; label: string }[] = [
    { id: "connections", label: "Connections" },
    { id: "delivery", label: "Delivery & logging" },
    { id: "storage", label: "Storage & retention" },
  ];

  return (
    <>
      <h1 className="page-title">Settings</h1>

      <div style={{ display: "flex", gap: 4, borderBottom: "1px solid var(--border)", marginBottom: 18 }}>
        {tabs.map((t) => (
          <button
            key={t.id}
            onClick={() => setTab(t.id)}
            style={{
              background: "transparent",
              border: "none",
              borderBottom: tab === t.id ? "2px solid var(--blue)" : "2px solid transparent",
              borderRadius: 0,
              color: tab === t.id ? "var(--text)" : "var(--muted)",
              fontWeight: tab === t.id ? 600 : 400,
              padding: "8px 14px",
            }}
          >
            {t.label}
          </button>
        ))}
      </div>

      {tab === "connections" && (sysConfig
        ? <ConnectionsTab initial={sysConfig} />
        : <div className="muted">Connection settings unavailable.</div>)}

      {tab === "delivery" && (
        <SubTabbed items={[
          { id: "logging", label: "Message logging", render: () => <LoggingPanel initial={settings.logging} /> },
          { id: "retry", label: "Retry policy", render: () => <RetryPanel initial={settings.retry} /> },
        ]} />
      )}

      {tab === "storage" && (
        <SubTabbed items={[
          { id: "retention", label: "Retention", render: () => <RetentionPanel initial={settings.retention} /> },
          ...(sysConfig ? [{ id: "spool", label: "Spool", render: () => <SpoolPanel initial={sysConfig} /> }] : []),
          { id: "maintenance", label: "Maintenance", render: () => <PurgePanel /> },
        ]} />
      )}
    </>
  );
}

// ---- Subtab layout ----------------------------------------------------------------------------

// A second-level tab bar (pill style, to read as subordinate to the underline top tabs). Shows one
// panel at a time so each Settings page is a single screen instead of a stack of scrolling cards.
function SubTabbed({ items }: { items: { id: string; label: string; render: () => ReactNode }[] }) {
  const [active, setActive] = useState(items[0]?.id);
  const current = items.find((i) => i.id === active) ?? items[0];
  return (
    <>
      <div style={{ display: "flex", gap: 6, flexWrap: "wrap", marginBottom: 2 }}>
        {items.map((it) => {
          const on = it.id === current?.id;
          return (
            <button
              key={it.id}
              onClick={() => setActive(it.id)}
              style={{
                background: on ? "var(--panel-2)" : "transparent",
                border: `1px solid ${on ? "var(--border)" : "transparent"}`,
                borderRadius: 8,
                color: on ? "var(--text)" : "var(--muted)",
                fontSize: 13,
                padding: "6px 12px",
              }}
            >
              {it.label}
            </button>
          );
        })}
      </div>
      {current?.render()}
    </>
  );
}

// ---- Delivery & logging -----------------------------------------------------------------------

function LoggingPanel({ initial }: { initial: AppSettings["logging"] }) {
  const [logging, setLogging] = useState(initial);
  const logRows: { key: keyof AppSettings["logging"]; label: string; help: string }[] = [
    { key: "delivered", label: "Log delivered messages", help: "Record a log entry for each delivered message. Counters are always recorded regardless." },
    { key: "retrying", label: "Log retry attempts", help: "Record a log entry each time a message is retried." },
    { key: "denied", label: "Log denied connections", help: "Record a log entry when a connection or request is refused." },
  ];
  return (
    <SavePanel title="Message logging" value={logging}
      intro="Suppressing log entries reduces database growth on high-volume relays. Dashboard counters and throughput are unaffected."
      onSave={() => api.settings.saveLogging(logging)}>
      {logRows.map((r) => (
        <label key={r.key} style={{ display: "flex", gap: 10, alignItems: "flex-start", margin: "12px 0" }}>
          <input type="checkbox" checked={logging[r.key]} style={{ width: "auto", marginTop: 3 }}
            onChange={() => setLogging({ ...logging, [r.key]: !logging[r.key] })} />
          <span><div>{r.label}</div><div className="muted" style={{ fontSize: 12 }}>{r.help}</div></span>
        </label>
      ))}
    </SavePanel>
  );
}

function RetryPanel({ initial }: { initial: AppSettings["retry"] }) {
  const [retry, setRetry] = useState(initial);
  const [delaysText, setDelaysText] = useState(initial.retryDelaysSeconds.join(", "));
  return (
    <SavePanel title="Retry policy" value={{ retry, delaysText }}
      intro="Back-off for transient delivery failures. The last delay repeats for any further attempts."
      onSave={() => api.settings.saveRetry({ ...retry, retryDelaysSeconds: csvNums(delaysText) })}>
      <label style={{ display: "block", margin: "12px 0" }}>
        <div>Max retries</div>
        <div className="muted" style={{ fontSize: 12, marginBottom: 4 }}>Delivery attempts before a message is marked failed.</div>
        <input type="number" min={0} value={retry.maxRetries} style={{ width: 120 }}
          onChange={(e) => setRetry({ ...retry, maxRetries: Math.max(0, Number(e.target.value)) })} />
      </label>
      <label style={{ display: "block", margin: "12px 0" }}>
        <div>Retry delays (seconds)</div>
        <div className="muted" style={{ fontSize: 12, marginBottom: 4 }}>Comma-separated, one per attempt — e.g. 30, 300, 1800.</div>
        <input type="text" value={delaysText} onChange={(e) => setDelaysText(e.target.value)} style={{ width: 280 }} />
      </label>
    </SavePanel>
  );
}

// ---- Storage & retention ----------------------------------------------------------------------

function RetentionPanel({ initial }: { initial: AppSettings["retention"] }) {
  const [retention, setRetention] = useState(initial);
  const rows: { key: keyof AppSettings["retention"]; label: string; help: string; step?: number }[] = [
    { key: "logDeliveredRetentionDays", label: "Delivered log retention (days)", help: "Delivered-message log entries older than this are removed." },
    { key: "logFailedRetentionDays", label: "Failed log retention (days)", help: "Failed-message log entries older than this are removed." },
    { key: "spoolFailedRetentionDays", label: "Retry-queue retention (days)", help: "Messages held in the retry queue (failed, awaiting retry/delete) older than this are removed." },
    { key: "capturedRetentionDays", label: "Captured (local inbox) retention (days)", help: "Captured messages (Local mode) on disk older than this are deleted." },
    { key: "auditRetentionDays", label: "Audit log retention (days)", help: "System Logs entries older than this are removed (0 = keep forever)." },
    { key: "auditSecurityRetentionDays", label: "Security event retention (days)", help: "Noisier security events (access denials, SMTP auth failures) are removed sooner (0 = keep forever)." },
    { key: "sizeTriggerGb", label: "Max database size (GB)", help: "When the database reaches this size, the oldest log entries are removed automatically (down to ~0.5 GB below). SQL Server Express caps at 10 GB.", step: 0.1 },
  ];
  return (
    <SavePanel title="Retention" value={retention}
      intro="How long log entries and on-disk messages are kept before they're automatically removed."
      onSave={() => api.settings.saveRetention(retention)}>
      {rows.map((r) => (
        <label key={r.key} style={{ display: "block", margin: "12px 0" }}>
          <div>{r.label}</div>
          <div className="muted" style={{ fontSize: 12, marginBottom: 4 }}>{r.help}</div>
          <input type="number" min={0} step={r.step ?? 1} value={retention[r.key]} style={{ width: 120 }}
            onChange={(e) => setRetention({ ...retention, [r.key]: Math.max(0, Number(e.target.value)) })} />
        </label>
      ))}
    </SavePanel>
  );
}

function SpoolPanel({ initial }: { initial: SystemConfig }) {
  const [spool, setSpool] = useState(initial.spool);
  return (
    <SavePanel title="Spool" restart value={spool}
      intro="Where in-flight messages are stored, and how many delivery workers run."
      onSave={() => api.settings.putSpool({ directory: spool.directory, workerCount: spool.workerCount })}>
      <Txt label="Directory" value={spool.directory} onChange={(v) => setSpool({ ...spool, directory: v })} />
      <Num label="Worker count" value={spool.workerCount} onChange={(v) => setSpool({ ...spool, workerCount: v })} />
    </SavePanel>
  );
}

function PurgePanel() {
  const [history, setHistory] = useState<PurgeRun[]>([]);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<string | null>(null);

  const load = () => api.purge.history().then(setHistory).catch(() => setHistory([]));
  useEffect(() => { load(); }, []);

  const run = async () => {
    setBusy(true); setMsg(null);
    try {
      const r = await api.purge.run();
      setMsg(`Removed ${r.spoolFilesDeleted} on-disk messages and ${r.logRowsDeleted} log entries.`);
      await load();
    } catch (e) { setMsg(`Error: ${(e as Error).message}`); }
    finally { setBusy(false); }
  };

  return (
    <div className="panel" style={{ maxWidth: 620, marginTop: 18 }}>
      <h2>Storage maintenance</h2>
      <p className="muted" style={{ fontSize: 13, marginTop: -6 }}>
        Run an immediate out-of-schedule cleanup using the thresholds on the Retention tab.
      </p>
      <div style={{ display: "flex", gap: 10, alignItems: "center", marginBottom: 12 }}>
        <button onClick={run} disabled={busy}>Run cleanup now</button>
        {msg && <span className={msg.startsWith("Error") ? "badge error" : "badge ok"}>{msg}</span>}
      </div>
      {history.length > 0 && (
        <table>
          <thead><tr><th>When</th><th>Trigger</th><th>Messages</th><th>Log entries</th><th>DB size</th></tr></thead>
          <tbody>
            {history.map((h, i) => (
              <tr key={i}>
                <td>{new Date(h.ranAtUtc).toLocaleString()}</td>
                <td>{h.manual ? "manual" : "scheduled"}</td>
                <td>{h.spoolFilesDeleted}</td>
                <td>{h.logRowsDeleted}</td>
                <td>{h.databaseSizeBytes > 0 ? `${Math.round(h.databaseSizeBytes / 1048576)} MB` : "—"}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}

// ---- Connections (SMTP listener / TLS cert / HTTP API / dashboard) ----------------------------

function ConnectionsTab({ initial }: { initial: SystemConfig }) {
  return (
    <>
      <p className="muted" style={{ fontSize: 13, marginTop: -4, marginBottom: 14 }}>
        IP allow-lists moved to the <strong>Access Control</strong> page.
      </p>
      <SubTabbed items={[
        { id: "smtp", label: "SMTP listener", render: () => <SmtpListenerPanel initial={initial.listener} /> },
        { id: "tls", label: "TLS certificate", render: () => <TlsCertPanel initial={initial.tls?.source ?? ""} /> },
        { id: "api", label: "HTTP API", render: () => <ApiPanel initial={initial.api} tlsCertSource={initial.tls?.source ?? ""} /> },
        { id: "dashboard", label: "Dashboard", render: () => <WebUiPanel initial={initial.webui} /> },
      ]} />
    </>
  );
}

function SmtpListenerPanel({ initial }: { initial: SystemConfig["listener"] }) {
  const [listener, setListener] = useState(initial);
  const [portsText, setPortsText] = useState(initial.ports.join(", "));
  return (
    <SavePanel title="SMTP listener" restart value={{ listener, portsText }}
      intro="The SMTP server your apps and devices send mail to. Size limit applies live; the rest apply after a restart."
      onSave={() => api.settings.putListener({
        ports: csvNums(portsText), serverName: listener.serverName,
        maxMessageBytes: listener.maxMessageBytes, requireAuth: listener.requireAuth,
      })}>
      <Txt label="Ports — comma-separated, e.g. 25, 587, 2525" value={portsText} onChange={setPortsText} />
      <Txt label="Server name" value={listener.serverName} onChange={(v) => setListener({ ...listener, serverName: v })} />
      <NumMb label="Max message size (MB, 0 = no limit)" bytes={listener.maxMessageBytes} onChange={(b) => setListener({ ...listener, maxMessageBytes: b })} />
      <Chk label="Require SMTP AUTH" checked={listener.requireAuth} onChange={(v) => setListener({ ...listener, requireAuth: v })} />
    </SavePanel>
  );
}

function ApiPanel({ initial, tlsCertSource }: { initial: SystemConfig["api"]; tlsCertSource: string }) {
  const [apiCfg, setApiCfg] = useState(initial);
  const httpsNoCert = apiCfg.tlsEnabled && !tlsCertSource;
  return (
    <SavePanel title="HTTP ingestion API" restart value={apiCfg}
      intro="The endpoint for posting messages with an API key. Size limit and rate limit apply live; ports and HTTP/HTTPS toggles apply after a restart."
      onSave={() => api.settings.putApi({
        port: apiCfg.port, httpEnabled: apiCfg.httpEnabled, tlsEnabled: apiCfg.tlsEnabled, tlsPort: apiCfg.tlsPort,
        maxMessageBytes: apiCfg.maxMessageBytes, rateLimitPerKey: apiCfg.rateLimitPerKey,
      })}>
      <Chk label="Enable plain HTTP" checked={apiCfg.httpEnabled} onChange={(v) => setApiCfg({ ...apiCfg, httpEnabled: v })} />
      <Num label="HTTP port" value={apiCfg.port} onChange={(v) => setApiCfg({ ...apiCfg, port: v })} />
      <Chk label="Enable HTTPS" checked={apiCfg.tlsEnabled} onChange={(v) => setApiCfg({ ...apiCfg, tlsEnabled: v })} />
      <Num label="HTTPS port" value={apiCfg.tlsPort} onChange={(v) => setApiCfg({ ...apiCfg, tlsPort: v })} />
      {httpsNoCert && (
        <p style={{ color: "var(--amber)", fontSize: 12, margin: "2px 0 8px" }}>
          ⚠ HTTPS is on but no TLS certificate is set — configure one under the <strong>TLS certificate</strong> tab.
          Until then a temporary self-signed certificate is used.
        </p>
      )}
      {!apiCfg.httpEnabled && !apiCfg.tlsEnabled && (
        <p style={{ color: "var(--amber)", fontSize: 12, margin: "2px 0 8px" }}>⚠ Both HTTP and HTTPS are off — the ingestion API won't accept any requests.</p>
      )}
      <p className="muted" style={{ fontSize: 12, margin: "4px 0 8px" }}>HTTPS uses the shared TLS certificate (same as SMTP STARTTLS).</p>
      <NumMb label="Max message size (MB, 0 = no limit)" bytes={apiCfg.maxMessageBytes} onChange={(b) => setApiCfg({ ...apiCfg, maxMessageBytes: b })} />
      <Num label="Rate limit / key per minute" value={apiCfg.rateLimitPerKey} onChange={(v) => setApiCfg({ ...apiCfg, rateLimitPerKey: v })} />
    </SavePanel>
  );
}

function WebUiPanel({ initial }: { initial: SystemConfig["webui"] }) {
  const [webui, setWebui] = useState(initial);
  return (
    <SavePanel title="Dashboard (web UI)" restart value={webui}
      intro="The admin dashboard you're using now. Applies after a restart."
      onSave={() => api.settings.putWebui({ port: webui.port, requireHttps: webui.requireHttps })}>
      <Num label="Port" value={webui.port} onChange={(v) => setWebui({ ...webui, port: v })} />
      <Chk label="Require HTTPS" checked={webui.requireHttps} onChange={(v) => setWebui({ ...webui, requireHttps: v })} />
    </SavePanel>
  );
}

// ---- Shared building blocks -------------------------------------------------------------------

// `value` is a snapshot of the panel's form state — the Save button only appears once it differs from
// the last-saved baseline, so an unchanged panel shows no button (replacing the always-on big Save).
function SavePanel({ title, intro, restart, value, onSave, children }: {
  title: string; intro: string; restart?: boolean; value: unknown; onSave: () => Promise<unknown>; children: ReactNode;
}) {
  const serialized = JSON.stringify(value);
  const [baseline, setBaseline] = useState(serialized);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<string | null>(null);
  const dirty = serialized !== baseline;

  // Clear a stale "Saved." once the user starts editing again.
  useEffect(() => { setMsg(null); }, [serialized]);

  const save = async () => {
    setBusy(true); setMsg(null);
    try { await onSave(); setBaseline(serialized); setMsg(restart ? "Saved — applies after the next service restart." : "Saved."); }
    catch (e) { setMsg(`Error: ${(e as Error).message}`); }
    finally { setBusy(false); }
  };
  return (
    <div className="panel" style={{ maxWidth: 620, marginTop: 14 }}>
      <h2>{title}</h2>
      <p className="muted" style={{ fontSize: 13, marginTop: -6 }}>{intro}</p>
      {children}
      <div style={{ display: "flex", gap: 10, alignItems: "center", marginTop: 14, minHeight: 30 }}>
        {dirty && <button onClick={save} disabled={busy}>Save changes</button>}
        {msg && <span className={msg.startsWith("Error") ? "badge error" : "badge ok"}>{msg}</span>}
        {dirty && !msg && <span className="muted" style={{ fontSize: 12 }}>Unsaved changes</span>}
      </div>
    </div>
  );
}

// Shared TLS cert: generate a self-signed one or upload a cert + key — no file paths. Applies on restart.
function TlsCertPanel({ initial }: { initial: string }) {
  const [source, setSource] = useState(initial);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<string | null>(null);
  const [uploading, setUploading] = useState(false);

  const status = source === "generated" ? "Using a generated self-signed certificate."
    : source === "uploaded" ? "Using an uploaded certificate."
    : "Off — SMTP STARTTLS is unavailable and HTTPS API falls back to a temporary self-signed cert.";

  const run = async (fn: () => Promise<unknown>, newSource: string, done: string) => {
    setBusy(true); setMsg(null);
    try { await fn(); setSource(newSource); setMsg(done); }
    catch (e) { setMsg(`Error: ${(e as Error).message}`); }
    finally { setBusy(false); }
  };

  return (
    <div className="panel" style={{ maxWidth: 620, marginTop: 14 }}>
      <h2>TLS certificate</h2>
      <p className="muted" style={{ fontSize: 13, marginTop: -6 }}>
        One certificate secures <strong>both</strong> the SMTP listener (STARTTLS) and the HTTPS ingestion API.
        Generate a self-signed certificate or upload your own. Applies after the next service restart.
      </p>
      <p className="muted" style={{ fontSize: 12, marginTop: -2 }}>
        The dashboard you're using has its own separate HTTPS certificate.
      </p>
      <p style={{ fontSize: 13 }}>
        {source ? <span className="badge ok">{source}</span> : <span className="badge denied">off</span>}
        <span className="muted" style={{ marginLeft: 8 }}>{status}</span>
      </p>
      <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center", marginTop: 8 }}>
        <button disabled={busy} onClick={() => run(api.settings.generateTlsCert, "generated", "Generated — restart to apply.")}>Generate certificate</button>
        <button disabled={busy} onClick={() => setUploading(true)}>Upload cert + key</button>
        {source && <button disabled={busy} onClick={() => run(api.settings.removeTlsCert, "", "Removed — restart to apply.")}>Remove</button>}
        {msg && <span className={msg.startsWith("Error") ? "badge error" : "badge ok"}>{msg}</span>}
      </div>
      {uploading && <UploadCertModal onClose={() => setUploading(false)} onDone={() => { setSource("uploaded"); setMsg("Uploaded — restart to apply."); setUploading(false); }} />}
    </div>
  );
}

function UploadCertModal({ onClose, onDone }: { onClose: () => void; onDone: () => void }) {
  const [cert, setCert] = useState<File | null>(null);
  const [key, setKey] = useState<File | null>(null);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const upload = async () => {
    if (!cert || !key) { setErr("Both a certificate and a key file are required."); return; }
    setBusy(true); setErr(null);
    try { await api.settings.uploadTlsCert(cert, key); onDone(); }
    catch (e) { setErr((e as Error).message); }
    finally { setBusy(false); }
  };
  return (
    <Modal title="Upload certificate + key" onClose={onClose}>
      <div style={{ display: "grid", gap: 12 }}>
        <p className="muted" style={{ fontSize: 13, margin: 0 }}>PEM-encoded certificate and its unencrypted private key.</p>
        <Labeled label="Certificate (.pem / .crt)"><input type="file" accept=".pem,.crt,.cer" onChange={(e) => setCert(e.target.files?.[0] ?? null)} /></Labeled>
        <Labeled label="Private key (.pem / .key)"><input type="file" accept=".pem,.key" onChange={(e) => setKey(e.target.files?.[0] ?? null)} /></Labeled>
        {err && <p style={{ color: "var(--red)", fontSize: 13, margin: 0 }}>{err}</p>}
        <div style={{ display: "flex", gap: 8, justifyContent: "flex-end" }}>
          <button onClick={onClose}>Cancel</button>
          <button onClick={upload} disabled={busy}>{busy ? "Uploading…" : "Upload"}</button>
        </div>
      </div>
    </Modal>
  );
}

const csvNums = (s: string) => s.split(",").map((x) => Number(x.trim())).filter((n) => Number.isFinite(n) && n >= 0);

function Labeled({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label style={{ display: "block", margin: "8px 0" }}>
      <div className="muted" style={{ fontSize: 12, marginBottom: 4 }}>{label}</div>
      {children}
    </label>
  );
}

function Txt({ label, value, onChange }: { label: string; value: string; onChange: (v: string) => void }) {
  return (
    <label style={{ display: "block", margin: "8px 0" }}>
      <div style={{ fontSize: 13 }}>{label}</div>
      <input type="text" value={value} onChange={(e) => onChange(e.target.value)} style={{ width: 320 }} />
    </label>
  );
}

function Num({ label, value, onChange }: { label: string; value: number; onChange: (v: number) => void }) {
  return (
    <label style={{ display: "block", margin: "8px 0" }}>
      <div style={{ fontSize: 13 }}>{label}</div>
      <input type="number" value={value} min={0} onChange={(e) => onChange(Math.max(0, Number(e.target.value)))} style={{ width: 160 }} />
    </label>
  );
}

// Message size shown/edited in MB (MiB) but stored as bytes — nobody configures limits in raw bytes.
function NumMb({ label, bytes, onChange }: { label: string; bytes: number; onChange: (bytes: number) => void }) {
  const MB = 1048576;
  const mb = bytes ? +(bytes / MB).toFixed(2) : 0;
  return (
    <label style={{ display: "block", margin: "8px 0" }}>
      <div style={{ fontSize: 13 }}>{label}</div>
      <input type="number" value={mb} min={0} step={1}
        onChange={(e) => onChange(Math.max(0, Math.round(Number(e.target.value) * MB)))} style={{ width: 160 }} />
    </label>
  );
}

function Chk({ label, checked, onChange }: { label: string; checked: boolean; onChange: (v: boolean) => void }) {
  return (
    <label style={{ display: "flex", gap: 8, alignItems: "center", margin: "8px 0", fontSize: 13 }}>
      <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} style={{ width: "auto" }} />
      {label}
    </label>
  );
}
