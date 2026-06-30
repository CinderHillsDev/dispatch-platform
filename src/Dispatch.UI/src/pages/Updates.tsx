import { useEffect, useRef, useState } from "react";
import { api, type UpdateInfo } from "../lib/api";

// Settings -> Updates: upload a signed upgrade package and watch the platform updater apply it. The same
// package works on every install (the host picks its own arch); only shown as actionable where the box is
// self-managed (appliance / Linux / Windows). The dashboard briefly restarts mid-apply, so we poll status.
export function Updates() {
  const [info, setInfo] = useState<UpdateInfo | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [msg, setMsg] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const fileRef = useRef<HTMLInputElement>(null);

  const load = () => api.updates.status().then(setInfo).catch((e) => setErr((e as Error).message));
  useEffect(() => {
    load();
    // Poll while an update is in flight (and across the dashboard restart it triggers).
    const t = setInterval(load, 4000);
    return () => clearInterval(t);
  }, []);

  const applying = info != null && (info.state === "Staged" || info.state === "Applying");

  const upload = async () => {
    const file = fileRef.current?.files?.[0];
    if (!file) { setMsg("Choose an upgrade package (.tar.gz) first."); return; }
    if (!window.confirm(`Apply ${file.name}? The service will restart and the dashboard will be briefly unavailable.`)) return;
    setBusy(true); setMsg(null); setErr(null);
    try {
      const r = await api.updates.upload(file);
      setMsg(r.message);
      if (fileRef.current) fileRef.current.value = "";
      await load();
    } catch (e) { setMsg(`Error: ${(e as Error).message}`); }
    finally { setBusy(false); }
  };

  if (err && !info) return <><h1 className="page-title">Updates</h1><div className="panel"><span className="badge error">Error: {err}</span></div></>;
  if (!info) return <><h1 className="page-title">Updates</h1><div className="center">Loading...</div></>;

  const stateBadge = ({ Succeeded: "ok", Failed: "error", RolledBack: "error" } as Record<string, string>)[info.state] ?? "denied";

  return (
    <>
      <h1 className="page-title">Updates</h1>

      <div className="panel" style={{ maxWidth: 640 }}>
        <h2>Current version</h2>
        <table>
          <tbody>
            <tr><td className="muted">Version</td><td>{info.currentVersion}</td></tr>
            <tr><td className="muted">Platform</td><td>{info.arch}</td></tr>
            {info.state !== "Idle" && (
              <tr><td className="muted">Last update</td>
                <td><span className={`badge ${stateBadge}`}>{info.state}</span>{info.message ? ` - ${info.message}` : ""}</td></tr>
            )}
          </tbody>
        </table>
      </div>

      {info.selfManaged ? (
        <div className="panel" style={{ maxWidth: 640 }}>
          <h2>Apply an upgrade package</h2>
          <p className="muted" style={{ fontSize: 13, marginTop: -6 }}>
            Upload a signed Dispatch upgrade package (<code>dispatch-upgrade-&lt;version&gt;.tar.gz</code>). It's
            verified (signature + checksum), then the service swaps to the new version and restarts - automatically
            rolling back if the new version fails to start. The dashboard will blink during the restart.
          </p>
          <div style={{ display: "flex", gap: 10, alignItems: "center", flexWrap: "wrap" }}>
            <input ref={fileRef} type="file" accept=".gz,.tgz,.tar.gz" disabled={busy || applying} />
            <button onClick={upload} disabled={busy || applying}>{applying ? "Applying..." : "Upload & apply"}</button>
            {msg && <span className={msg.startsWith("Error") ? "badge error" : "badge ok"}>{msg}</span>}
          </div>
          {applying && <p className="muted" style={{ fontSize: 12, marginTop: 10 }}>Update in progress - the dashboard may briefly disconnect; this page refreshes automatically.</p>}
        </div>
      ) : (
        <div className="panel" style={{ maxWidth: 640 }}>
          <h2>Apply an upgrade</h2>
          <p className="muted" style={{ fontSize: 13 }}>
            In-app updates aren't available on this install type. Update via your platform's normal method
            (Docker: pull a new image; otherwise re-run the installer).
          </p>
        </div>
      )}
    </>
  );
}
