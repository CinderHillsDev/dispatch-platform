import { useEffect, useState } from "react";
import { api, type LicenseStatus } from "../lib/api";

// System -> License: shows this install's node-lock Machine ID (which the customer sends when purchasing),
// the current license state, and a box to paste a key. Verification is fully offline - the product never
// calls home. An unlicensed install runs during a 30-day first-run grace, then stops accepting/relaying new
// mail until a valid key is entered (the spool and this dashboard stay up so a key recovers it).

const STATE_LABEL: Record<string, { text: string; badge: string }> = {
  licensed: { text: "Licensed", badge: "ok" },
  grace: { text: "Unlicensed - in grace period", badge: "warn" },
  unlicensed: { text: "Unlicensed", badge: "error" },
  expired: { text: "License expired", badge: "error" },
  revoked: { text: "License revoked", badge: "error" },
  invalid: { text: "License not valid for this machine", badge: "error" },
};

export function License() {
  const [status, setStatus] = useState<LicenseStatus | null>(null);
  const [keyInput, setKeyInput] = useState("");
  const [saving, setSaving] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  const refresh = async () => setStatus(await api.license.get());
  useEffect(() => { refresh(); }, []);

  const save = async () => {
    setErr(null);
    if (!keyInput.trim()) { setErr("Paste a license key first."); return; }
    setSaving(true);
    try {
      const s = await api.license.set(keyInput.trim());
      setStatus(s);
      setKeyInput("");
    } catch (e) {
      setErr((e as Error).message);
    } finally {
      setSaving(false);
    }
  };

  const copyMachineId = async () => {
    if (!status) return;
    await navigator.clipboard.writeText(status.machineId);
    setCopied(true);
    setTimeout(() => setCopied(false), 1500);
  };

  if (!status) return <div style={{ maxWidth: 680 }}><h1 className="page-title">License</h1><p className="muted">Loading…</p></div>;

  const label = STATE_LABEL[status.state] ?? STATE_LABEL.unlicensed;

  return (
    <div style={{ maxWidth: 680 }}>
      <h1 className="page-title" style={{ margin: 0 }}>License</h1>
      <p className="muted" style={{ fontSize: 13, margin: "8px 0 18px" }}>
        Dispatch is licensed offline - the product never calls home. A license key is <strong>node-locked</strong>{" "}
        to this install's Machine ID, so it only works here.
      </p>

      <div className="panel" style={{ maxWidth: 680, padding: 16, marginBottom: 16 }}>
        <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 12 }}>
          <strong>Status</strong>
          <span className={`badge ${label.badge}`}>{label.text}</span>
        </div>

        {status.enforcementActive && (
          <p style={{ margin: "12px 0 0", fontSize: 13 }}>
            <span className="badge error">Enforcement active</span>{" "}
            New mail is being refused/paused. Enter a valid license key below to resume. Queued mail is not lost.
          </p>
        )}
        {status.state === "grace" && (
          <p className="muted" style={{ margin: "12px 0 0", fontSize: 13 }}>
            {status.graceDaysRemaining} day{status.graceDaysRemaining === 1 ? "" : "s"} left in the grace period
            (ends {new Date(status.graceEndsUtc).toLocaleDateString()}). Enter a key to license this install.
          </p>
        )}

        {status.hasKey && (
          <table style={{ marginTop: 14 }}>
            <tbody>
              <tr><td className="muted" style={{ width: 140 }}>License ID</td><td><code>{status.licenseId}</code></td></tr>
              <tr><td className="muted">Type</td><td>{status.perpetual ? "Perpetual" : "Time-based"}</td></tr>
              <tr>
                <td className="muted">Expires</td>
                <td className={status.expired ? "" : undefined}>
                  {status.expiresAt ? new Date(status.expiresAt).toLocaleDateString() : "Never"}
                  {status.expired && <> <span className="badge error">expired</span></>}
                  {status.revoked && <> <span className="badge error">revoked</span></>}
                </td>
              </tr>
            </tbody>
          </table>
        )}
      </div>

      <div className="panel" style={{ maxWidth: 680, padding: 16, marginBottom: 16 }}>
        <strong>Machine ID</strong>
        <p className="muted" style={{ fontSize: 13, margin: "6px 0 10px" }}>
          Send this when you purchase or renew - your key is issued for exactly this value. It changes if you
          reinstall.
        </p>
        <div style={{ display: "flex", gap: 8, alignItems: "center" }}>
          <code style={{ flex: 1, padding: "8px 10px", background: "var(--panel-2, #1113)", borderRadius: 6, wordBreak: "break-all" }}>
            {status.machineId}
          </code>
          <button onClick={copyMachineId}>{copied ? "Copied" : "Copy"}</button>
        </div>
      </div>

      <div className="panel" style={{ maxWidth: 680, padding: 16 }}>
        <strong>Enter a license key</strong>
        <textarea
          value={keyInput}
          onChange={(e) => setKeyInput(e.target.value)}
          placeholder="XXXXX-XXXXX-XXXXX-…"
          rows={4}
          style={{ width: "100%", marginTop: 10, fontFamily: "monospace", fontSize: 13 }}
        />
        <div style={{ display: "flex", gap: 10, alignItems: "center", marginTop: 10 }}>
          <button onClick={save} disabled={saving}>{saving ? "Validating…" : "Save license key"}</button>
          {err && <span className="badge error">{err}</span>}
        </div>
      </div>
    </div>
  );
}
