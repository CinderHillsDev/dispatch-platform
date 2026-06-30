import { useEffect, useState, type ReactNode } from "react";

interface Status { authenticated: boolean; needsSetup: boolean; }

export async function logout() {
  await fetch("/api/auth/logout", { method: "POST" });
  location.reload();
}

export function AuthGate({ children }: { children: ReactNode }) {
  const [state, setState] = useState<"loading" | "setup" | "login" | "ok">("loading");
  const [password, setPassword] = useState("");
  const [err, setErr] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const check = async () => {
    try {
      const s: Status = await fetch("/api/auth/status").then((r) => r.json());
      setState(s.authenticated ? "ok" : s.needsSetup ? "setup" : "login");
    } catch {
      setState("login");
    }
  };
  useEffect(() => { check(); }, []);

  const submit = async (path: string, fallbackErr: string) => {
    setBusy(true); setErr(null);
    try {
      const r = await fetch(path, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ password }) });
      if (r.ok) { setPassword(""); await check(); }
      else { const d = await r.json().catch(() => ({})); setErr(d.error ?? fallbackErr); }
    } finally { setBusy(false); }
  };

  if (state === "loading") return <div className="center">Loading…</div>;
  if (state === "ok") return <>{children}</>;

  const setup = state === "setup";
  return (
    <div style={{ minHeight: "100vh", display: "grid", placeItems: "center" }}>
      <div className="panel" style={{ width: 360 }}>
        <div className="brand" style={{ marginBottom: 18 }}><span className="dot" /> Dispatch</div>
        <h2>{setup ? "Create admin password" : "Sign in"}</h2>
        {setup && <p className="muted" style={{ fontSize: 13 }}>First-run setup - set the admin password for this dashboard.</p>}
        <input
          type="password"
          autoFocus
          style={{ width: "100%", margin: "10px 0" }}
          placeholder={setup ? "New password (min 8 chars)" : "Password"}
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          onKeyDown={(e) => { if (e.key === "Enter") submit(setup ? "/api/auth/password" : "/api/auth/login", setup ? "Failed" : "Incorrect password"); }}
        />
        <button
          style={{ width: "100%" }}
          disabled={busy || !password}
          onClick={() => submit(setup ? "/api/auth/password" : "/api/auth/login", setup ? "Failed" : "Incorrect password")}
        >
          {setup ? "Create & sign in" : "Sign in"}
        </button>
        {err && <p style={{ marginTop: 12 }}><span className="badge error">{err}</span></p>}
      </div>
    </div>
  );
}
