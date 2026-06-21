import { useEffect, useState, type ReactNode } from "react";
import { api, type AppSettings, type SystemConfig, type PurgeRun } from "../lib/api";

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

      {tab === "delivery" && <DeliveryTab settings={settings} onChange={setSettings} />}

      {tab === "storage" && (
        <>
          <RetentionPanel settings={settings} onChange={setSettings} />
          {sysConfig && <SpoolPanel initial={sysConfig} />}
          <PurgePanel />
        </>
      )}
    </>
  );
}

// ---- Delivery & logging tab -------------------------------------------------------------------

function DeliveryTab({ settings, onChange }: { settings: AppSettings; onChange: (s: AppSettings) => void }) {
  const [delaysText, setDelaysText] = useState(settings.retry.retryDelaysSeconds.join(", "));
  const logRows: { key: keyof AppSettings["logging"]; label: string; help: string }[] = [
    { key: "delivered", label: "Log delivered messages", help: "Record a log entry for each delivered message. Counters are always recorded regardless." },
    { key: "retrying", label: "Log retry attempts", help: "Record a log entry each time a message is retried." },
    { key: "denied", label: "Log denied connections", help: "Record a log entry when a connection or request is refused." },
  ];

  return (
    <>
      <SavePanel title="Message logging"
        intro="Suppressing log entries reduces database growth on high-volume relays. Dashboard counters and throughput are unaffected."
        onSave={() => api.settings.saveLogging(settings.logging)}>
        {logRows.map((r) => (
          <label key={r.key} style={{ display: "flex", gap: 10, alignItems: "flex-start", margin: "12px 0" }}>
            <input type="checkbox" checked={settings.logging[r.key]} style={{ width: "auto", marginTop: 3 }}
              onChange={() => onChange({ ...settings, logging: { ...settings.logging, [r.key]: !settings.logging[r.key] } })} />
            <span><div>{r.label}</div><div className="muted" style={{ fontSize: 12 }}>{r.help}</div></span>
          </label>
        ))}
      </SavePanel>

      <SavePanel title="Retry policy"
        intro="Back-off for transient delivery failures. The last delay repeats for any further attempts."
        onSave={() => api.settings.saveRetry({ ...settings.retry, retryDelaysSeconds: csvNums(delaysText) })}>
        <label style={{ display: "block", margin: "12px 0" }}>
          <div>Max retries</div>
          <div className="muted" style={{ fontSize: 12, marginBottom: 4 }}>Delivery attempts before a message is marked failed.</div>
          <input type="number" min={0} value={settings.retry.maxRetries} style={{ width: 120 }}
            onChange={(e) => onChange({ ...settings, retry: { ...settings.retry, maxRetries: Math.max(0, Number(e.target.value)) } })} />
        </label>
        <label style={{ display: "block", margin: "12px 0" }}>
          <div>Retry delays (seconds)</div>
          <div className="muted" style={{ fontSize: 12, marginBottom: 4 }}>Comma-separated, one per attempt — e.g. 30, 300, 1800.</div>
          <input type="text" value={delaysText} onChange={(e) => setDelaysText(e.target.value)} style={{ width: 280 }} />
        </label>
      </SavePanel>
    </>
  );
}

// ---- Storage & retention tab ------------------------------------------------------------------

function RetentionPanel({ settings, onChange }: { settings: AppSettings; onChange: (s: AppSettings) => void }) {
  const rows: { key: keyof AppSettings["retention"]; label: string; help: string; step?: number }[] = [
    { key: "logDeliveredRetentionDays", label: "Delivered log retention (days)", help: "Delivered-message log entries older than this are removed." },
    { key: "logFailedRetentionDays", label: "Failed log retention (days)", help: "Failed-message log entries older than this are removed." },
    { key: "spoolFailedRetentionDays", label: "Retry-queue retention (days)", help: "Messages held in the retry queue (failed, awaiting retry/delete) older than this are removed." },
    { key: "capturedRetentionDays", label: "Captured (local inbox) retention (days)", help: "Captured messages (Local mode) on disk older than this are deleted." },
    { key: "sizeTriggerGb", label: "Size-pressure trigger (GB)", help: "When the database reaches this size, the oldest entries are removed.", step: 0.1 },
    { key: "sizeTargetGb", label: "Size-pressure target (GB)", help: "Size-pressure cleanup runs until the database drops below this.", step: 0.1 },
  ];
  return (
    <SavePanel title="Retention"
      intro="How long log entries and on-disk messages are kept before they're automatically removed."
      onSave={() => api.settings.saveRetention(settings.retention)}>
      {rows.map((r) => (
        <label key={r.key} style={{ display: "block", margin: "12px 0" }}>
          <div>{r.label}</div>
          <div className="muted" style={{ fontSize: 12, marginBottom: 4 }}>{r.help}</div>
          <input type="number" min={0} step={r.step ?? 1} value={settings.retention[r.key]} style={{ width: 120 }}
            onChange={(e) => onChange({ ...settings, retention: { ...settings.retention, [r.key]: Math.max(0, Number(e.target.value)) } })} />
        </label>
      ))}
    </SavePanel>
  );
}

function SpoolPanel({ initial }: { initial: SystemConfig }) {
  const [spool, setSpool] = useState(initial.spool);
  return (
    <SavePanel title="Spool" restart
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
        Run an immediate out-of-schedule cleanup using the retention thresholds above.
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

// ---- Connections tab (SMTP listener / HTTP API / Web UI) --------------------------------------

function ConnectionsTab({ initial }: { initial: SystemConfig }) {
  const [listener, setListener] = useState(initial.listener);
  const [apiCfg, setApiCfg] = useState(initial.api);
  const [webui, setWebui] = useState(initial.webui);
  const [portsText, setPortsText] = useState(initial.listener.ports.join(", "));
  const [lCidrText, setLCidrText] = useState(initial.listener.allowedCidrs.join(", "));
  const [aCidrText, setACidrText] = useState(initial.api.allowedCidrs.join(", "));

  return (
    <>
      <SavePanel title="SMTP listener" restart
        intro="The SMTP server your apps and devices send mail to. Allow-list and size limit apply live; the rest apply after a restart."
        onSave={() => api.settings.putListener({
          ports: csvNums(portsText), serverName: listener.serverName, allowedCidrs: csvStrs(lCidrText),
          maxMessageBytes: listener.maxMessageBytes, requireAuth: listener.requireAuth, tlsCertPath: listener.tlsCertPath,
        })}>
        <Txt label="Ports — comma-separated, e.g. 25, 587, 2525" value={portsText} onChange={setPortsText} />
        <Txt label="Server name" value={listener.serverName} onChange={(v) => setListener({ ...listener, serverName: v })} />
        <Txt label="Allow-list — comma-separated CIDRs" value={lCidrText} onChange={setLCidrText} />
        <Num label="Max message bytes (0 = no limit)" value={listener.maxMessageBytes} onChange={(v) => setListener({ ...listener, maxMessageBytes: v })} />
        <Chk label="Require SMTP AUTH" checked={listener.requireAuth} onChange={(v) => setListener({ ...listener, requireAuth: v })} />
        <Txt label="STARTTLS cert path" value={listener.tlsCertPath} onChange={(v) => setListener({ ...listener, tlsCertPath: v })} />
      </SavePanel>

      <SavePanel title="HTTP ingestion API" restart
        intro="The HTTP endpoint for posting messages with an API key. Allow-list, size limit and rate limit apply live; the port applies after a restart."
        onSave={() => api.settings.putApi({
          port: apiCfg.port, allowedCidrs: csvStrs(aCidrText), maxMessageBytes: apiCfg.maxMessageBytes, rateLimitPerKey: apiCfg.rateLimitPerKey,
        })}>
        <Num label="Port" value={apiCfg.port} onChange={(v) => setApiCfg({ ...apiCfg, port: v })} />
        <Txt label="Allow-list — comma-separated CIDRs" value={aCidrText} onChange={setACidrText} />
        <Num label="Max message bytes (0 = no limit)" value={apiCfg.maxMessageBytes} onChange={(v) => setApiCfg({ ...apiCfg, maxMessageBytes: v })} />
        <Num label="Rate limit / key per minute" value={apiCfg.rateLimitPerKey} onChange={(v) => setApiCfg({ ...apiCfg, rateLimitPerKey: v })} />
      </SavePanel>

      <SavePanel title="Dashboard (web UI)" restart
        intro="The admin dashboard you're using now. Applies after a restart."
        onSave={() => api.settings.putWebui({ port: webui.port, requireHttps: webui.requireHttps })}>
        <Num label="Port" value={webui.port} onChange={(v) => setWebui({ ...webui, port: v })} />
        <Chk label="Require HTTPS" checked={webui.requireHttps} onChange={(v) => setWebui({ ...webui, requireHttps: v })} />
      </SavePanel>
    </>
  );
}

// ---- Shared building blocks -------------------------------------------------------------------

function SavePanel({ title, intro, restart, onSave, children }: {
  title: string; intro: string; restart?: boolean; onSave: () => Promise<unknown>; children: ReactNode;
}) {
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<string | null>(null);
  const save = async () => {
    setBusy(true); setMsg(null);
    try { await onSave(); setMsg(restart ? "Saved — applies after the next service restart." : "Saved."); }
    catch (e) { setMsg(`Error: ${(e as Error).message}`); }
    finally { setBusy(false); }
  };
  return (
    <div className="panel" style={{ maxWidth: 620, marginTop: 18 }}>
      <h2>{title}</h2>
      <p className="muted" style={{ fontSize: 13, marginTop: -6 }}>{intro}</p>
      {children}
      <div style={{ display: "flex", gap: 10, alignItems: "center", marginTop: 14 }}>
        <button onClick={save} disabled={busy}>Save</button>
        {msg && <span className={msg.startsWith("Error") ? "badge error" : "badge ok"}>{msg}</span>}
      </div>
    </div>
  );
}

const csvNums = (s: string) => s.split(",").map((x) => Number(x.trim())).filter((n) => Number.isFinite(n) && n >= 0);
const csvStrs = (s: string) => s.split(",").map((x) => x.trim()).filter(Boolean);

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

function Chk({ label, checked, onChange }: { label: string; checked: boolean; onChange: (v: boolean) => void }) {
  return (
    <label style={{ display: "flex", gap: 8, alignItems: "center", margin: "8px 0", fontSize: 13 }}>
      <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} style={{ width: "auto" }} />
      {label}
    </label>
  );
}
