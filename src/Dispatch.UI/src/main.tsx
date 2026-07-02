import React, { useEffect, useState, type ReactNode } from "react";
import ReactDOM from "react-dom/client";
import { createBrowserRouter, RouterProvider, NavLink, Link, Outlet, useRouteError } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { api, type LicenseStatus } from "./lib/api";
import { FirstRunWizard } from "./FirstRunWizard";
import { Dashboard } from "./pages/Dashboard";
import { Messages } from "./pages/Messages";
import { Reports } from "./pages/Reports";
import { Relays } from "./pages/Relays";
import { Routing } from "./pages/Routing";
import { LocalInbox } from "./pages/LocalInbox";
import { Failed } from "./pages/Failed";
import { SmtpAuth } from "./pages/SmtpAuth";
import { ApiKeys } from "./pages/ApiKeys";
import { Settings } from "./pages/Settings";
import { Updates } from "./pages/Updates";
import { License } from "./pages/License";
import { System } from "./pages/System";
import { Logs } from "./pages/Logs";
import { AccessControl } from "./pages/AccessControl";
import { AuthGate, logout } from "./auth";
import "./index.css";

// One-time "you just upgraded" banner, shown after a version change on the first authenticated load.
// The server records from -> to at startup; dismissing clears it so it appears only once.
function UpgradeBanner() {
  const [notice, setNotice] = useState<{ from: string; to: string } | null>(null);
  useEffect(() => { api.updates.status().then((s) => setNotice(s.upgradeNotice)).catch(() => {}); }, []);
  if (!notice) return null;
  const dismiss = () => { setNotice(null); api.updates.dismissNotice().catch(() => {}); };
  return (
    <div style={{
      display: "flex", alignItems: "center", gap: 12, margin: "0 0 18px", padding: "12px 16px",
      background: "rgba(34,197,94,.12)", border: "1px solid rgba(34,197,94,.4)", borderRadius: 8,
    }}>
      <span style={{ fontSize: 20 }}>🎉</span>
      <div style={{ flex: 1 }}>
        <strong>Upgrade complete</strong> - you're now running Dispatch <strong>{notice.to}</strong>
        {notice.from ? <> (upgraded from {notice.from})</> : null}. Enjoy the new version!
      </div>
      <button onClick={dismiss}>Dismiss</button>
    </div>
  );
}

// Site-wide license banner: shows on every page whenever the install isn't fully licensed. Amber during
// the first-run grace (a countdown), red once enforcement is active (mail refused/paused) or the key is
// expired/revoked. Hidden when licensed. Polls occasionally so a pasted key clears it without a reload.
function LicenseBanner() {
  const [s, setS] = useState<LicenseStatus | null>(null);
  useEffect(() => {
    const load = () => api.license.get().then(setS).catch(() => {});
    load();
    const t = setInterval(load, 60_000);
    return () => clearInterval(t);
  }, []);
  if (!s || s.licensed) return null;

  const enforcing = s.enforcementActive;
  const color = enforcing ? "#f85149" : "#d29922";
  const days = s.graceDaysRemaining;
  const reason = s.revoked
    ? "This license has been revoked."
    : s.expired
    ? "Your license has expired."
    : s.inGracePeriod
    ? `Unlicensed - ${days} day${days === 1 ? "" : "s"} left in the grace period.`
    : "This install is unlicensed.";
  const tail = enforcing
    ? " New mail is being refused and relaying is paused until a valid license key is entered."
    : " Enter a license key to keep relaying.";

  return (
    <div style={{
      display: "flex", alignItems: "center", gap: 12, margin: "0 0 18px", padding: "12px 16px",
      background: enforcing ? "rgba(248,81,73,.12)" : "rgba(210,153,34,.12)",
      border: `1px solid ${color}`, borderRadius: 8, color,
    }}>
      <span style={{ fontSize: 18 }}>{enforcing ? "⛔" : "⚠"}</span>
      <div style={{ flex: 1 }}>
        <strong>{enforcing ? "Not licensed" : "License required"}</strong> - {reason}{tail}
      </div>
      <Link to="/license"><button>Manage license</button></Link>
    </div>
  );
}

function Layout() {
  return (
    <div className="app">
      <aside className="sidebar">
        <div className="brand"><span className="dot" /> Dispatch</div>
        <nav className="nav">
          <div className="nav-group">
            <div className="nav-label">Monitor</div>
            <NavLink to="/" end>Dashboard</NavLink>
            <NavLink to="/messages">Message Log</NavLink>
            <NavLink to="/failed">Retry Queue</NavLink>
            <NavLink to="/inbox">Local Inbox</NavLink>
            <NavLink to="/reports">Reports</NavLink>
          </div>
          <div className="nav-group">
            <div className="nav-label">Delivery</div>
            <NavLink to="/relays">Relays</NavLink>
            <NavLink to="/routing">Routing</NavLink>
          </div>
          <div className="nav-group">
            <div className="nav-label">Access</div>
            <NavLink to="/access">Access Control</NavLink>
            <NavLink to="/smtp-auth">SMTP Auth</NavLink>
            <NavLink to="/api-keys">API Keys</NavLink>
          </div>
          <div className="nav-group">
            <div className="nav-label">System</div>
            <NavLink to="/settings">Settings</NavLink>
            <NavLink to="/updates">Updates</NavLink>
            <NavLink to="/license">License</NavLink>
            <NavLink to="/logs">Logs</NavLink>
            <NavLink to="/system">About</NavLink>
          </div>
        </nav>
        <button onClick={logout} style={{ marginTop: 18, width: "100%" }}>Sign out</button>
      </aside>
      <main className="main"><LicenseBanner /><UpgradeBanner /><Outlet /></main>
    </div>
  );
}

// Shown (in place of the page, inside the layout) when a route component throws - far friendlier than
// React Router's default error screen.
function RouteError() {
  const err = useRouteError();
  const message = err instanceof Error ? err.message : typeof err === "string" ? err : "An unexpected error occurred.";
  return (
    <div style={{ maxWidth: 640 }}>
      <h1 className="page-title">Something went wrong</h1>
      <p className="muted">This page hit an unexpected error. Try reloading; if it keeps happening, check the service logs.</p>
      <pre style={{ whiteSpace: "pre-wrap", background: "var(--panel-2)", border: "1px solid var(--border)", borderRadius: 8, padding: 12, fontSize: 13 }}>{message}</pre>
      <button onClick={() => location.reload()}>Reload</button>
    </div>
  );
}

const pages: { path: string; element: ReactNode }[] = [
  { path: "/", element: <Dashboard /> },
  { path: "/messages", element: <Messages /> },
  { path: "/reports", element: <Reports /> },
  { path: "/failed", element: <Failed /> },
  { path: "/relays", element: <Relays /> },
  { path: "/routing", element: <Routing /> },
  { path: "/access", element: <AccessControl /> },
  { path: "/smtp-auth", element: <SmtpAuth /> },
  { path: "/api-keys", element: <ApiKeys /> },
  { path: "/inbox", element: <LocalInbox /> },
  { path: "/settings", element: <Settings /> },
  { path: "/updates", element: <Updates /> },
  { path: "/license", element: <License /> },
  { path: "/logs", element: <Logs /> },
  { path: "/system", element: <System /> },
];

const router = createBrowserRouter([
  {
    element: <Layout />,
    errorElement: <RouteError />,   // fallback if the layout itself throws; pages use per-route errors (keeps the nav)
    children: pages.map((p) => ({ ...p, errorElement: <RouteError /> })),
  },
]);

const queryClient = new QueryClient({ defaultOptions: { queries: { refetchOnWindowFocus: false } } });

// First-run: if no provider has been configured yet, show the setup wizard instead of the dashboard.
// A relay counts as "configured" once its provider is anything other than Unconfigured.
function FirstRun({ children }: { children: ReactNode }) {
  const [state, setState] = useState<"loading" | "wizard" | "ready">("loading");
  useEffect(() => {
    api.relays.list()
      .then((relays) => setState(relays.some((r) => r.provider !== "Unconfigured") ? "ready" : "wizard"))
      .catch(() => setState("ready"));   // never lock the user out if the check fails
  }, []);
  if (state === "loading") return <div className="center">Loading…</div>;
  if (state === "wizard") return <FirstRunWizard onDone={() => setState("ready")} />;
  return <>{children}</>;
}

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <QueryClientProvider client={queryClient}>
      <AuthGate>
        <FirstRun>
          <RouterProvider router={router} />
        </FirstRun>
      </AuthGate>
    </QueryClientProvider>
  </React.StrictMode>,
);
