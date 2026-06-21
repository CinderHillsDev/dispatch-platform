import { useEffect, useState } from "react";
import { api, type SmtpCredential } from "../lib/api";

export function SmtpAuth() {
  const [creds, setCreds] = useState<SmtpCredential[]>([]);
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [err, setErr] = useState<string | null>(null);

  const refresh = async () => setCreds(await api.smtpCreds.list());
  useEffect(() => { refresh(); }, []);

  const add = async () => {
    setErr(null);
    if (!username.trim() || !password) { setErr("Username and password are required."); return; }
    try { await api.smtpCreds.add(username.trim(), password); setUsername(""); setPassword(""); await refresh(); }
    catch (e) { setErr((e as Error).message); }
  };

  return (
    <>
      <h1 className="page-title">SMTP Authentication</h1>
      <p className="muted" style={{ marginTop: -10, marginBottom: 18 }}>
        Username/password logins that your apps and devices use to authenticate when sending mail to the
        SMTP listener. They're only enforced when <strong>Require SMTP AUTH</strong> is enabled under{" "}
        <strong>Settings → SMTP listener</strong>; otherwise the listener accepts mail from any allowed
        source IP without a login. Passwords are stored bcrypt-hashed and can't be retrieved — only reset.
      </p>

      <div className="panel" style={{ maxWidth: 640, padding: 0, overflow: "hidden" }}>
        <table>
          <thead><tr><th>Username</th><th>Created</th><th>Last used</th><th></th></tr></thead>
          <tbody>
            {creds.map((c) => (
              <tr key={c.id}>
                <td>{c.username}</td>
                <td>{new Date(c.createdAt).toLocaleDateString()}</td>
                <td>{c.lastUsedAt ? new Date(c.lastUsedAt).toLocaleString() : <span className="muted">Never</span>}</td>
                <td><button onClick={async () => { await api.smtpCreds.remove(c.username); await refresh(); }}>✕</button></td>
              </tr>
            ))}
            {creds.length === 0 && <tr><td colSpan={4} className="center">No SMTP credentials configured.</td></tr>}
          </tbody>
        </table>
      </div>

      <div className="panel" style={{ maxWidth: 640 }}>
        <h2>Add credential</h2>
        <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
          <input placeholder="username" value={username} onChange={(e) => setUsername(e.target.value)} />
          <input type="password" placeholder="password" value={password} onChange={(e) => setPassword(e.target.value)} />
          <button onClick={add}>Add</button>
        </div>
        {err && <p style={{ marginTop: 10 }}><span className="badge error">{err}</span></p>}
      </div>
    </>
  );
}
