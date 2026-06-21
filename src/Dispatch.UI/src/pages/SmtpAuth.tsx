import { useEffect, useState } from "react";
import { api, type SmtpCredential } from "../lib/api";
import { Modal } from "../Modal";
import { ActionsMenu } from "../ActionsMenu";

export function SmtpAuth() {
  const [creds, setCreds] = useState<SmtpCredential[]>([]);
  const [adding, setAdding] = useState(false);
  const [resetting, setResetting] = useState<SmtpCredential | null>(null);
  const [err, setErr] = useState<string | null>(null);

  const refresh = async () => setCreds(await api.smtpCreds.list());
  useEffect(() => { refresh(); }, []);

  const del = async (c: SmtpCredential) => {
    if (!confirm(`Delete SMTP login “${c.username}”?`)) return;
    try { await api.smtpCreds.remove(c.username); await refresh(); }
    catch (e) { setErr((e as Error).message); }
  };

  return (
    <div style={{ maxWidth: 680 }}>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 12 }}>
        <h1 className="page-title" style={{ margin: 0 }}>SMTP Authentication</h1>
        <button onClick={() => setAdding(true)}>+ Add credential</button>
      </div>
      <p className="muted" style={{ fontSize: 13, margin: "8px 0 10px" }}>
        Username/password logins your apps and devices use to authenticate when sending mail to the SMTP listener.
      </p>
      <p className="muted" style={{ fontSize: 13, margin: "0 0 18px" }}>
        They're only enforced when <strong>Require SMTP AUTH</strong> is on under <strong>Settings → SMTP listener</strong>;
        otherwise the listener accepts mail from any allowed source IP without a login. Passwords are stored
        bcrypt-hashed — they can't be retrieved, only reset.
      </p>

      <div className="panel" style={{ maxWidth: 680, padding: 0 }}>
        <table>
          <thead><tr><th>Username</th><th>Created</th><th>Last used</th><th></th></tr></thead>
          <tbody>
            {creds.map((c) => (
              <tr key={c.id}>
                <td>{c.username}</td>
                <td>{new Date(c.createdAt).toLocaleDateString()}</td>
                <td>{c.lastUsedAt ? new Date(c.lastUsedAt).toLocaleString() : <span className="muted">Never</span>}</td>
                <td style={{ textAlign: "right" }}>
                  <ActionsMenu items={[
                    { label: "Reset password", onClick: () => setResetting(c) },
                    { label: "Delete", danger: true, onClick: () => del(c) },
                  ]} />
                </td>
              </tr>
            ))}
            {creds.length === 0 && <tr><td colSpan={4} className="center">No SMTP logins yet — click “Add credential”.</td></tr>}
          </tbody>
        </table>
      </div>

      {err && <p style={{ marginTop: 14 }}><span className="badge error">{err}</span></p>}

      {adding && <CredModal onClose={() => setAdding(false)} onSaved={refresh} />}
      {resetting && <CredModal cred={resetting} onClose={() => setResetting(null)} onSaved={refresh} />}
    </div>
  );
}

// Add / reset an SMTP login. The add endpoint upserts by username, so "reset password" reuses it with the
// username locked.
function CredModal({ cred, onClose, onSaved }: { cred?: SmtpCredential; onClose: () => void; onSaved: () => Promise<void> }) {
  const [username, setUsername] = useState(cred?.username ?? "");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);

  const save = async () => {
    if (!username.trim()) { setErr("Username is required."); return; }
    if (!password) { setErr("Password is required."); return; }
    setBusy(true); setErr(null);
    try { await api.smtpCreds.add(username.trim(), password); await onSaved(); onClose(); }
    catch (e) { setErr((e as Error).message); }
    finally { setBusy(false); }
  };

  return (
    <Modal title={cred ? `Reset password · ${cred.username}` : "Add SMTP credential"} onClose={onClose}>
      <div style={{ display: "grid", gap: 12 }}>
        <Lbl label="Username"><input value={username} disabled={!!cred} onChange={(e) => setUsername(e.target.value)} style={{ width: "100%" }} /></Lbl>
        <Lbl label={cred ? "New password" : "Password"}><input type="password" value={password} onChange={(e) => setPassword(e.target.value)} style={{ width: "100%" }} /></Lbl>
        {err && <p style={{ color: "var(--red)", fontSize: 13, margin: 0 }}>{err}</p>}
        <div style={{ display: "flex", gap: 8, justifyContent: "flex-end", marginTop: 4 }}>
          <button onClick={onClose}>Cancel</button>
          <button onClick={save} disabled={busy}>{busy ? "Saving…" : cred ? "Reset password" : "Add credential"}</button>
        </div>
      </div>
    </Modal>
  );
}

function Lbl({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label style={{ display: "block" }}>
      <div className="muted" style={{ fontSize: 12, marginBottom: 4 }}>{label}</div>
      {children}
    </label>
  );
}
