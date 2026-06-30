import { useEffect, useState } from "react";
import { api, type SystemInfo } from "../lib/api";

function uptime(seconds: number): string {
  const d = Math.floor(seconds / 86400);
  const h = Math.floor((seconds % 86400) / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  const parts = [];
  if (d) parts.push(`${d}d`);
  if (h || d) parts.push(`${h}h`);
  parts.push(`${m}m`);
  return parts.join(" ");
}

export function System() {
  const [info, setInfo] = useState<SystemInfo | null>(null);
  const [msg, setMsg] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => { api.system.about().then(setInfo).catch(() => setInfo(null)); }, []);

  const restart = async () => {
    if (!window.confirm("Restart the Dispatch service? In-flight mail is drained first; the dashboard will be briefly unavailable.")) return;
    setBusy(true); setMsg(null);
    try {
      await api.system.restart();
      setMsg("Restart requested - draining queue, then the service will restart.");
    } catch (e) {
      setMsg(`Error: ${(e as Error).message}`);
    } finally {
      setBusy(false);
    }
  };

  return (
    <>
      <h1 className="page-title">About</h1>

      <div className="panel" style={{ maxWidth: 620 }}>
        <h2>Service</h2>
        {!info && <div className="center">Loading…</div>}
        {info && (
          <dl className="kv" style={{ display: "grid", gridTemplateColumns: "160px 1fr", gap: "6px 12px", margin: 0, fontSize: 13 }}>
            <dt className="muted">Version</dt><dd style={{ margin: 0 }}>{info.version}</dd>
            <dt className="muted">Uptime</dt><dd style={{ margin: 0 }}>{uptime(info.uptimeSeconds)}</dd>
            <dt className="muted">Started</dt><dd style={{ margin: 0 }}>{new Date(info.startedAtUtc).toLocaleString()}</dd>
            <dt className="muted">Runtime</dt><dd style={{ margin: 0 }}>{info.framework}</dd>
            <dt className="muted">OS</dt><dd style={{ margin: 0, wordBreak: "break-word" }}>{info.os}</dd>
            <dt className="muted">Log file</dt>
            <dd style={{ margin: 0, wordBreak: "break-word" }}>
              <code>{info.logDirectory}</code>{" "}
              <a href={api.system.logDownloadUrl} download>download current log</a>
            </dd>
          </dl>
        )}
      </div>

      <ChangePasswordPanel />

      <div className="panel" style={{ maxWidth: 620 }}>
        <h2>Restart</h2>
        <p className="muted" style={{ fontSize: 13, marginTop: -6 }}>
          Graceful restart - drains the in-flight spool (up to 60s), then exits so the service manager
          (systemd / Windows Service) starts the new process. Settings that apply "on restart" take effect then.
        </p>
        <div style={{ display: "flex", gap: 10, alignItems: "center" }}>
          <button onClick={restart} disabled={busy}>Restart service</button>
          {msg && <span className={msg.startsWith("Error") ? "badge error" : "badge ok"}>{msg}</span>}
        </div>
      </div>

      <div className="panel" style={{ maxWidth: 620 }}>
        <h2>About</h2>
        <p className="muted" style={{ fontSize: 13, margin: 0 }}>
          Dispatch SMTP Relay - AGPL-3.0 + Commons Clause.{" "}
          <a href="https://github.com/chrismuench/Dispatch-SMTP-Relay" target="_blank" rel="noreferrer">GitHub</a>
        </p>
      </div>
    </>
  );
}

function ChangePasswordPanel() {
  const [pw, setPw] = useState("");
  const [confirm, setConfirm] = useState("");
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<string | null>(null);

  const save = async () => {
    if (pw !== confirm) { setMsg("Error: Passwords do not match."); return; }
    setBusy(true); setMsg(null);
    try { await api.setPassword(pw); setMsg("Password changed."); setPw(""); setConfirm(""); }
    catch (e) { setMsg(`Error: ${(e as Error).message}`); }
    finally { setBusy(false); }
  };

  return (
    <div className="panel" style={{ maxWidth: 620 }}>
      <h2>Admin password</h2>
      <p className="muted" style={{ fontSize: 13, marginTop: -6 }}>
        Change the dashboard sign-in password. Minimum 12 characters with an uppercase letter, a lowercase letter and a digit.
      </p>
      <label style={{ display: "block", margin: "8px 0" }}>
        <div style={{ fontSize: 13 }}>New password</div>
        <input type="password" autoComplete="new-password" value={pw} onChange={(e) => setPw(e.target.value)} style={{ width: 320 }} />
      </label>
      <label style={{ display: "block", margin: "8px 0" }}>
        <div style={{ fontSize: 13 }}>Confirm new password</div>
        <input type="password" autoComplete="new-password" value={confirm} onChange={(e) => setConfirm(e.target.value)} style={{ width: 320 }} />
      </label>
      <div style={{ display: "flex", gap: 10, alignItems: "center", marginTop: 12 }}>
        <button onClick={save} disabled={busy || !pw || !confirm}>Change password</button>
        {msg && <span className={msg.startsWith("Error") ? "badge error" : "badge ok"}>{msg}</span>}
      </div>
    </div>
  );
}
