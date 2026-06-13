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

      {sysConfig && (
        <div className="panel" style={{ maxWidth: 620, marginTop: 18 }}>
          <h2>System configuration</h2>
          <p className="muted" style={{ fontSize: 13, marginTop: -6 }}>
            Listener, HTTP API and Web UI settings. Read-only here — these are applied at startup, so changes
            require a service restart. (Retry, retention and logging above take effect live.)
          </p>
          <dl className="kv" style={{ display: "grid", gridTemplateColumns: "180px 1fr", gap: "6px 12px", margin: 0, fontSize: 13 }}>
            <Row label="SMTP ports">{sysConfig.listener.ports.join(", ")}</Row>
            <Row label="SMTP server name">{sysConfig.listener.serverName}</Row>
            <Row label="SMTP allow-list">{sysConfig.listener.allowedCidrs.join(", ")}</Row>
            <Row label="SMTP require AUTH">{sysConfig.listener.requireAuth ? "yes" : "no"}</Row>
            <Row label="SMTP STARTTLS">{sysConfig.listener.tlsEnabled ? `enabled (${sysConfig.listener.tlsCertPath})` : "disabled"}</Row>
            <Row label="SMTP max size">{sysConfig.listener.maxMessageBytes > 0 ? `${sysConfig.listener.maxMessageBytes} bytes` : "no limit"}</Row>
            <Row label="API port">{sysConfig.api.port}</Row>
            <Row label="API allow-list">{sysConfig.api.allowedCidrs.join(", ")}</Row>
            <Row label="API rate limit / key">{sysConfig.api.rateLimitPerKey}/min</Row>
            <Row label="API max size">{sysConfig.api.maxMessageBytes > 0 ? `${sysConfig.api.maxMessageBytes} bytes` : "no limit"}</Row>
            <Row label="Web UI port">{sysConfig.webui.port}</Row>
            <Row label="Web UI HTTPS">{sysConfig.webui.requireHttps ? "required" : "not required"}</Row>
          </dl>
        </div>
      )}
    </>
  );
}

function Row({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <>
      <dt className="muted">{label}</dt>
      <dd style={{ margin: 0, wordBreak: "break-word" }}>{children}</dd>
    </>
  );
}
