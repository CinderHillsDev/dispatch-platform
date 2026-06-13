import { useEffect, useState } from "react";
import { api, type AppSettings } from "../lib/api";

export function Settings() {
  const [settings, setSettings] = useState<AppSettings | null>(null);
  const [msg, setMsg] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => { api.settings.get().then(setSettings); }, []);

  const toggle = (key: keyof AppSettings["logging"]) => {
    if (!settings) return;
    setSettings({ ...settings, logging: { ...settings.logging, [key]: !settings.logging[key] } });
  };

  const save = async () => {
    if (!settings) return;
    setBusy(true); setMsg(null);
    try { await api.settings.saveLogging(settings.logging); setMsg("Saved."); }
    catch (e) { setMsg(`Error: ${(e as Error).message}`); }
    finally { setBusy(false); }
  };

  if (!settings) return <div className="center">Loading…</div>;

  const rows: { key: keyof AppSettings["logging"]; label: string; help: string }[] = [
    { key: "delivered", label: "Log delivered messages", help: "Write a relay_log row for each delivery. Counters are always recorded regardless." },
    { key: "retrying", label: "Log retry attempts", help: "Write a relay_log row each time a message is retried." },
    { key: "denied", label: "Log denied connections", help: "Write a relay_log row when a connection/request is refused." },
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
        {rows.map((r) => (
          <label key={r.key} style={{ display: "flex", gap: 10, alignItems: "flex-start", margin: "12px 0" }}>
            <input type="checkbox" checked={settings.logging[r.key]} onChange={() => toggle(r.key)} style={{ width: "auto", marginTop: 3 }} />
            <span>
              <div>{r.label}</div>
              <div className="muted" style={{ fontSize: 12 }}>{r.help}</div>
            </span>
          </label>
        ))}
        <div style={{ display: "flex", gap: 10, alignItems: "center", marginTop: 8 }}>
          <button onClick={save} disabled={busy}>Save</button>
          {msg && <span className={msg.startsWith("Error") ? "badge error" : "badge ok"}>{msg}</span>}
        </div>
      </div>
    </>
  );
}
