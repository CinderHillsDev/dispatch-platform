import { useCallback, useEffect, useRef, useState } from "react";
import { api, type UpdateInfo } from "../lib/api";

// Settings -> Updates: upload a signed upgrade package and watch the platform updater apply it, step by step.
// The same package works on every install (the host picks its own arch); only actionable where the box is
// self-managed (appliance / Linux / Windows). Nothing is hidden: a real upload progress bar, a per-step
// tracker, and a live log of every status message the service and updater emit (the dashboard briefly
// restarts mid-apply, so we keep polling across the disconnect).

// The ordered steps shown in the tracker, mapped to the server-side UpdateState they correspond to.
const STEPS = [
  { key: "upload", label: "Upload package" },
  { key: "verify", label: "Verify signature & compatibility" },
  { key: "extract", label: "Unpack & check payload" },
  { key: "stage", label: "Stage new version" },
  { key: "apply", label: "Apply update" },
  { key: "restart", label: "Restart service" },
  { key: "done", label: "Complete" },
] as const;

// Where each server state lands on the tracker (the "upload" step is driven by the browser, not the server).
const STATE_INDEX: Record<string, number> = {
  Receiving: 0, Verifying: 1, Extracting: 2, Staged: 3, Applying: 4, Restarting: 5, Succeeded: 6,
};
const IN_FLIGHT = ["Verifying", "Extracting", "Staged", "Applying", "Restarting"];
const TERMINAL = ["Succeeded", "Failed", "RolledBack"];

const fmtMB = (b: number) => `${(b / (1024 * 1024)).toFixed(1)} MB`;
const clock = () => new Date().toLocaleTimeString();

export function Updates() {
  const [info, setInfo] = useState<UpdateInfo | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const fileRef = useRef<HTMLInputElement>(null);

  // Run state (a run = one click of "Upload & apply", from upload through the final status).
  const [started, setStarted] = useState(false);
  const [busy, setBusy] = useState(false);
  const [uploadPct, setUploadPct] = useState(0);
  const [maxIdx, setMaxIdx] = useState(0);
  const [log, setLog] = useState<string[]>([]);
  const [reconnecting, setReconnecting] = useState(false);

  // Refs read inside the polling closure (which is created once).
  const busyRef = useRef(false);
  const engagedRef = useRef(false); // seen an in-flight/terminal state that belongs to THIS run
  const lastMsgRef = useRef<string>("");
  busyRef.current = busy;

  const append = useCallback((line: string) => {
    setLog((l) => [...l, `${clock()}  ${line}`].slice(-60));
  }, []);

  // Absorb a status poll: advance the tracker, log new messages, end the run on a terminal state.
  const absorb = useCallback((i: UpdateInfo) => {
    setInfo(i);
    setReconnecting(false);
    if (!busyRef.current) return;
    const s = i.state;
    if (IN_FLIGHT.includes(s)) { engagedRef.current = true; setMaxIdx((m) => Math.max(m, STATE_INDEX[s] ?? 0)); }
    if (i.message && i.message !== lastMsgRef.current) { lastMsgRef.current = i.message; append(i.message); }
    if (engagedRef.current && TERMINAL.includes(s)) {
      if (s === "Succeeded") setMaxIdx(6);
      setBusy(false);
    }
  }, [append]);

  // One poll loop for the life of the page: fast while a run is in flight, slow when idle, and tolerant of
  // the dashboard restart the apply triggers (failed polls just mark "reconnecting" and keep trying).
  useEffect(() => {
    let alive = true;
    let timer: ReturnType<typeof setTimeout>;
    const tick = async () => {
      try { const i = await api.updates.status(); if (alive) absorb(i); }
      catch { if (alive && busyRef.current) setReconnecting(true); }
      if (alive) timer = setTimeout(tick, busyRef.current ? 1200 : 5000);
    };
    tick();
    return () => { alive = false; clearTimeout(timer); };
  }, [absorb]);

  const upload = async () => {
    const file = fileRef.current?.files?.[0];
    if (!file) { setErr("Choose an upgrade package (.tar.gz) first."); return; }
    if (!window.confirm(`Apply ${file.name}? The service will restart, the dashboard will be briefly unavailable, and you'll be signed out for the new version.`)) return;

    // Reset run state.
    setErr(null); setStarted(true); setBusy(true); setUploadPct(0); setMaxIdx(0); setLog([]);
    setReconnecting(false); engagedRef.current = false; lastMsgRef.current = "";
    append(`Uploading ${file.name} (${fmtMB(file.size)})...`);

    try {
      const r = await api.updates.upload(file, (loaded, total) => setUploadPct(Math.round((loaded / total) * 100)));
      setUploadPct(100);
      engagedRef.current = true;
      append(`Upload complete. ${r.message}`);
      if (fileRef.current) fileRef.current.value = "";
      // The poll loop now drives the remaining steps (apply -> restart -> done) across the restart.
    } catch (e) {
      // A rejected package (bad signature/checksum/too large) or a network error ends the run here; the
      // server also records a Failed status which the poll will pick up.
      append(`Error: ${(e as Error).message}`);
      setErr((e as Error).message);
      setBusy(false);
    }
  };

  if (err && !info) return <><h1 className="page-title">Updates</h1><div className="panel"><span className="badge error">Error: {err}</span></div></>;
  if (!info) return <><h1 className="page-title">Updates</h1><div className="center">Loading...</div></>;

  const state = info.state;
  const isSuccess = started && state === "Succeeded";
  const isError = started && !busy && (state === "Failed" || state === "RolledBack");
  const activeIdx = busy ? (uploadPct < 100 ? 0 : (STATE_INDEX[state] ?? 1)) : -1;

  const stepStatus = (i: number): "done" | "active" | "failed" | "pending" => {
    if (!started) return "pending";
    if (isError) return i < maxIdx ? "done" : i === maxIdx ? "failed" : "pending";
    if (isSuccess) return "done";
    if (i < activeIdx) return "done";
    if (i === activeIdx && busy) return "active";
    return "pending";
  };
  const icon = { done: "✓", active: "…", failed: "✕", pending: "○" };
  const color = { done: "#22c55e", active: "#0ea5e9", failed: "#dc2626", pending: "#9ca3af" };

  const lastBadge = ({ Succeeded: "ok", Failed: "error", RolledBack: "error" } as Record<string, string>)[state] ?? "denied";

  return (
    <>
      <h1 className="page-title">Updates</h1>

      <div className="panel" style={{ maxWidth: 680 }}>
        <h2>Current version</h2>
        <table>
          <tbody>
            <tr><td className="muted">Version</td><td>{info.currentVersion}</td></tr>
            <tr><td className="muted">Platform</td><td>{info.arch}</td></tr>
            {!started && state !== "Idle" && (
              <tr><td className="muted">Last update</td>
                <td><span className={`badge ${lastBadge}`}>{state}</span>{info.message ? ` - ${info.message}` : ""}</td></tr>
            )}
          </tbody>
        </table>
      </div>

      {!info.selfManaged ? (
        <div className="panel" style={{ maxWidth: 680 }}>
          <h2>Apply an upgrade</h2>
          <p className="muted" style={{ fontSize: 13 }}>
            In-app updates aren't available on this install type. Update via your platform's normal method
            (Docker: pull a new image; otherwise re-run the installer).
          </p>
        </div>
      ) : (
        <div className="panel" style={{ maxWidth: 680 }}>
          <h2>Apply an upgrade package</h2>
          <p className="muted" style={{ fontSize: 13, marginTop: -6 }}>
            Upload a signed Dispatch upgrade package (<code>dispatch-upgrade-&lt;version&gt;.tar.gz</code>). It's
            verified (signature + checksum), unpacked, then the service swaps to the new version and restarts -
            automatically rolling back if the new version fails to start. The dashboard blinks during the restart
            and signs you out for the new version.
          </p>

          <div style={{ display: "flex", gap: 10, alignItems: "center", flexWrap: "wrap" }}>
            <input ref={fileRef} type="file" accept=".gz,.tgz,.tar.gz" disabled={busy} />
            <button onClick={upload} disabled={busy}>{busy ? "Applying..." : "Upload & apply"}</button>
          </div>

          {started && (
            <div style={{ marginTop: 18 }}>
              {/* Upload progress bar (real bytes-sent percentage). */}
              <div style={{ display: "flex", justifyContent: "space-between", fontSize: 12 }}>
                <span className="muted">{uploadPct < 100 ? "Uploading" : "Uploaded"}</span>
                <span className="muted">{uploadPct}%</span>
              </div>
              <div style={{ height: 8, background: "rgba(148,163,184,.2)", borderRadius: 4, overflow: "hidden", marginTop: 4 }}>
                <div style={{ height: "100%", width: `${uploadPct}%`, background: isError ? "#dc2626" : "#0ea5e9", transition: "width .3s" }} />
              </div>

              {/* Step tracker. */}
              <ul style={{ listStyle: "none", padding: 0, margin: "16px 0 0" }}>
                {STEPS.map((st, i) => {
                  const s = stepStatus(i);
                  return (
                    <li key={st.key} style={{ display: "flex", alignItems: "center", gap: 10, padding: "3px 0", opacity: s === "pending" ? 0.55 : 1 }}>
                      <span style={{ width: 18, textAlign: "center", color: color[s], fontWeight: 700 }}>{icon[s]}</span>
                      <span style={{ color: s === "active" ? "#0ea5e9" : undefined }}>{st.label}</span>
                      {s === "active" && st.key !== "upload" && <span className="muted" style={{ fontSize: 12 }}>in progress...</span>}
                    </li>
                  );
                })}
              </ul>

              {reconnecting && <p className="muted" style={{ fontSize: 12, marginTop: 8 }}>Service restarting - reconnecting to the dashboard...</p>}
              {isSuccess && <p style={{ marginTop: 10 }}><span className="badge ok">Updated to {info.stagedVersion ?? info.currentVersion}</span> - sign in again to continue.</p>}
              {isError && <p style={{ marginTop: 10 }}><span className="badge error">{state}</span> {info.message}</p>}

              {/* Live log - every message the service + updater emit, nothing hidden. */}
              {log.length > 0 && (
                <>
                  <div className="muted" style={{ fontSize: 12, margin: "14px 0 4px" }}>Activity log</div>
                  <pre style={{
                    margin: 0, padding: 10, maxHeight: 200, overflow: "auto", fontSize: 12, lineHeight: 1.5,
                    background: "rgba(148,163,184,.08)", borderRadius: 6, whiteSpace: "pre-wrap", wordBreak: "break-word",
                  }}>{log.join("\n")}</pre>
                </>
              )}
            </div>
          )}
        </div>
      )}
    </>
  );
}
