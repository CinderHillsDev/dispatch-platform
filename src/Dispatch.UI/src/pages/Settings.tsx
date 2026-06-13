import { useEffect, useState } from "react";
import { api, type AppSettings, type SystemConfig } from "../lib/api";

export function Settings() {
  const [settings, setSettings] = useState<AppSettings | null>(null);
  const [sysConfig, setSysConfig] = useState<SystemConfig | null>(null);
  const [delaysText, setDelaysText] = useState("");
  const [msg, setMsg] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    api.settings.get().then((s) => {
      setSettings(s);
      setDelaysText(s.retry.retryDelaysSeconds.join(", "));
    });
  }, []);
  useEffect(() => { api.settings.config().then(setSysConfig).catch(() => setSysConfig(null)); }, []);

  const toggle = (key: keyof AppSettings["logging"]) => {
    if (!settings) return;
    setSettings({ ...settings, logging: { ...settings.logging, [key]: !settings.logging[key] } });
  };

  const setRetry = (key: keyof AppSettings["retry"], value: number) => {
    if (!settings) return;
    setSettings({ ...settings, retry: { ...settings.retry, [key]: value } });
  };

  const setRetention = (key: keyof AppSettings["retention"], value: number) => {
    if (!settings) return;
    setSettings({ ...settings, retention: { ...settings.retention, [key]: value } });
  };

  const save = async () => {
    if (!settings) return;
    setBusy(true);
    setMsg(null);
    try {
      const delays = delaysText
        .split(",")
        .map((s) => Number(s.trim()))
        .filter((n) => Number.isFinite(n) && n >= 0);
      const retry = { ...settings.retry, retryDelaysSeconds: delays };
      await api.settings.saveLogging(settings.logging);
      await api.settings.saveRetry(retry);
      await api.settings.saveRetention(settings.retention);
      setSettings({ ...settings, retry });
      setMsg("Saved.");
    } catch (e) {
      setMsg(`Error: ${(e as Error).message}`);
    } finally {
      setBusy(false);
    }
  };

  if (!settings) return <div className="center">Loading…</div>;

  const logRows: { key: keyof AppSettings["logging"]; label: string; help: string }[] = [
    { key: "delivered", label: "Log delivered messages", help: "Write a relay_log row for each delivery. Counters are always recorded regardless." },
    { key: "retrying", label: "Log retry attempts", help: "Write a relay_log row each time a message is retried." },
    { key: "denied", label: "Log denied connections", help: "Write a relay_log row when a connection/request is refused." },
  ];

  const retentionRows: { key: keyof AppSettings["retention"]; label: string; help: string; step?: number }[] = [
    { key: "logDeliveredRetentionDays", label: "Delivered log retention (days)", help: "relay_log rows for delivered messages older than this are purged." },
    { key: "logFailedRetentionDays", label: "Failed log retention (days)", help: "relay_log rows for failed messages older than this are purged." },
    { key: "spoolFailedRetentionDays", label: "Failed spool retention (days)", help: "Files in spool/failed/ older than this are deleted." },
    { key: "capturedRetentionDays", label: "Captured (local inbox) retention (days)", help: "Files in spool/captured/ older than this are deleted." },
    { key: "sizeTriggerGb", label: "Size-pressure trigger (GB)", help: "When the database reaches this size, the oldest rows are purged.", step: 0.1 },
    { key: "sizeTargetGb", label: "Size-pressure target (GB)", help: "Size-pressure purge runs until the database drops below this.", step: 0.1 },
  ];

  return (
    <>
      <h1 className="page-title">Settings</h1>

      <div className="panel" style={{ maxWidth: 620 }}>
        <h2>Message log</h2>
        <p className="muted" style={{ fontSize: 13, marginTop: -6 }}>
          Suppressing log rows reduces database growth on high-volume relays. Dashboard counters and
          throughput are unaffected — they come from the always-written aggregates.
        </p>
        {logRows.map((r) => (
          <label key={r.key} style={{ display: "flex", gap: 10, alignItems: "flex-start", margin: "12px 0" }}>
            <input type="checkbox" checked={settings.logging[r.key]} onChange={() => toggle(r.key)} style={{ width: "auto", marginTop: 3 }} />
            <span>
              <div>{r.label}</div>
              <div className="muted" style={{ fontSize: 12 }}>{r.help}</div>
            </span>
          </label>
        ))}
      </div>

      <div className="panel" style={{ maxWidth: 620 }}>
        <h2>Retry</h2>
        <p className="muted" style={{ fontSize: 13, marginTop: -6 }}>
          Back-off policy for transient delivery failures. The last delay repeats for any further attempts.
        </p>
        <label style={{ display: "block", margin: "12px 0" }}>
          <div>Max retries</div>
          <div className="muted" style={{ fontSize: 12, marginBottom: 4 }}>Attempts before a message is moved to spool/failed/.</div>
          <input
            type="number"
            min={0}
            value={settings.retry.maxRetries}
            onChange={(e) => setRetry("maxRetries", Math.max(0, Number(e.target.value)))}
            style={{ width: 120 }}
          />
        </label>
        <label style={{ display: "block", margin: "12px 0" }}>
          <div>Retry delays (seconds)</div>
          <div className="muted" style={{ fontSize: 12, marginBottom: 4 }}>Comma-separated, one per attempt — e.g. 30, 300, 1800.</div>
          <input type="text" value={delaysText} onChange={(e) => setDelaysText(e.target.value)} style={{ width: 280 }} />
        </label>
      </div>

      <div className="panel" style={{ maxWidth: 620 }}>
        <h2>Retention</h2>
        <p className="muted" style={{ fontSize: 13, marginTop: -6 }}>
          How long log rows and spool files are kept before the purge worker removes them.
        </p>
        {retentionRows.map((r) => (
          <label key={r.key} style={{ display: "block", margin: "12px 0" }}>
            <div>{r.label}</div>
            <div className="muted" style={{ fontSize: 12, marginBottom: 4 }}>{r.help}</div>
            <input
              type="number"
              min={0}
              step={r.step ?? 1}
              value={settings.retention[r.key]}
              onChange={(e) => setRetention(r.key, Math.max(0, Number(e.target.value)))}
              style={{ width: 120 }}
            />
          </label>
        ))}
      </div>

      <div style={{ display: "flex", gap: 10, alignItems: "center", maxWidth: 620 }}>
        <button onClick={save} disabled={busy}>Save</button>
        {msg && <span className={msg.startsWith("Error") ? "badge error" : "badge ok"}>{msg}</span>}
      </div>

      {sysConfig && <SystemConfigEditor initial={sysConfig} />}
    </>
  );
}

const csvNums = (s: string) => s.split(",").map((x) => Number(x.trim())).filter((n) => Number.isFinite(n));
const csvStrs = (s: string) => s.split(",").map((x) => x.trim()).filter(Boolean);

function SystemConfigEditor({ initial }: { initial: SystemConfig }) {
  const [listener, setListener] = useState(initial.listener);
  const [apiCfg, setApiCfg] = useState(initial.api);
  const [webui, setWebui] = useState(initial.webui);
  const [spool, setSpool] = useState(initial.spool);
  const [portsText, setPortsText] = useState(initial.listener.ports.join(", "));
  const [lCidrText, setLCidrText] = useState(initial.listener.allowedCidrs.join(", "));
  const [aCidrText, setACidrText] = useState(initial.api.allowedCidrs.join(", "));
  const [msg, setMsg] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function save(fn: () => Promise<unknown>) {
    setBusy(true); setMsg(null);
    try { await fn(); setMsg("Saved. Live settings apply now; ports/TLS/spool apply after a restart."); }
    catch (e) { setMsg(`Error: ${(e as Error).message}`); }
    finally { setBusy(false); }
  }

  return (
    <div className="panel" style={{ maxWidth: 620, marginTop: 18 }}>
      <h2>System configuration</h2>
      <p className="muted" style={{ fontSize: 13, marginTop: -6 }}>
        Stored in the SQL config table (spec §12). Allow-lists, message-size limits and the API rate limit
        apply live; ports, TLS, server name, require-AUTH and spool settings apply on the next service restart.
      </p>

      <h3>SMTP listener</h3>
      <Txt label="Ports (restart) — comma-separated, e.g. 25, 587, 2525" value={portsText} onChange={setPortsText} />
      <Txt label="Server name (restart)" value={listener.serverName} onChange={(v) => setListener({ ...listener, serverName: v })} />
      <Txt label="Allow-list (live) — comma-separated CIDRs" value={lCidrText} onChange={setLCidrText} />
      <Num label="Max message bytes (live)" value={listener.maxMessageBytes} onChange={(v) => setListener({ ...listener, maxMessageBytes: v })} />
      <Chk label="Require SMTP AUTH (restart)" checked={listener.requireAuth} onChange={(v) => setListener({ ...listener, requireAuth: v })} />
      <Txt label="STARTTLS cert path (restart)" value={listener.tlsCertPath} onChange={(v) => setListener({ ...listener, tlsCertPath: v })} />
      <button disabled={busy} onClick={() => save(() => api.settings.putListener({
        ports: csvNums(portsText), serverName: listener.serverName, allowedCidrs: csvStrs(lCidrText),
        maxMessageBytes: listener.maxMessageBytes, requireAuth: listener.requireAuth, tlsCertPath: listener.tlsCertPath,
      }))}>Save listener</button>

      <h3 style={{ marginTop: 18 }}>HTTP API</h3>
      <Num label="Port (restart)" value={apiCfg.port} onChange={(v) => setApiCfg({ ...apiCfg, port: v })} />
      <Txt label="Allow-list (live) — comma-separated CIDRs" value={aCidrText} onChange={setACidrText} />
      <Num label="Max message bytes (live)" value={apiCfg.maxMessageBytes} onChange={(v) => setApiCfg({ ...apiCfg, maxMessageBytes: v })} />
      <Num label="Rate limit / key per min (live)" value={apiCfg.rateLimitPerKey} onChange={(v) => setApiCfg({ ...apiCfg, rateLimitPerKey: v })} />
      <button disabled={busy} onClick={() => save(() => api.settings.putApi({
        port: apiCfg.port, allowedCidrs: csvStrs(aCidrText), maxMessageBytes: apiCfg.maxMessageBytes, rateLimitPerKey: apiCfg.rateLimitPerKey,
      }))}>Save API</button>

      <h3 style={{ marginTop: 18 }}>Web UI</h3>
      <Num label="Port (restart)" value={webui.port} onChange={(v) => setWebui({ ...webui, port: v })} />
      <Chk label="Require HTTPS (restart)" checked={webui.requireHttps} onChange={(v) => setWebui({ ...webui, requireHttps: v })} />
      <button disabled={busy} onClick={() => save(() => api.settings.putWebui({ port: webui.port, requireHttps: webui.requireHttps }))}>Save Web UI</button>

      <h3 style={{ marginTop: 18 }}>Spool (restart)</h3>
      <Txt label="Directory" value={spool.directory} onChange={(v) => setSpool({ ...spool, directory: v })} />
      <Num label="Worker count" value={spool.workerCount} onChange={(v) => setSpool({ ...spool, workerCount: v })} />
      <button disabled={busy} onClick={() => save(() => api.settings.putSpool({ directory: spool.directory, workerCount: spool.workerCount }))}>Save spool</button>

      {msg && <div style={{ marginTop: 12 }}><span className={msg.startsWith("Error") ? "badge error" : "badge ok"}>{msg}</span></div>}
    </div>
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

function Chk({ label, checked, onChange }: { label: string; checked: boolean; onChange: (v: boolean) => void }) {
  return (
    <label style={{ display: "flex", gap: 8, alignItems: "center", margin: "8px 0", fontSize: 13 }}>
      <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} style={{ width: "auto" }} />
      {label}
    </label>
  );
}
