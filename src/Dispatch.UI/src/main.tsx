import React, { useEffect, useState, type ReactNode } from "react";
import ReactDOM from "react-dom/client";
import { createBrowserRouter, RouterProvider, NavLink, Outlet } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { api } from "./lib/api";
import { FirstRunWizard } from "./FirstRunWizard";
import { Dashboard } from "./pages/Dashboard";
import { Messages } from "./pages/Messages";
import { Relays } from "./pages/Relays";
import { Routing } from "./pages/Routing";
import { LocalInbox } from "./pages/LocalInbox";
import { Failed } from "./pages/Failed";
import { SmtpAuth } from "./pages/SmtpAuth";
import { ApiKeys } from "./pages/ApiKeys";
import { Settings } from "./pages/Settings";
import { System } from "./pages/System";
import { AuthGate, logout } from "./auth";
import "./index.css";

function Layout() {
  return (
    <div className="app">
      <aside className="sidebar">
        <div className="brand"><span className="dot" /> Dispatch</div>
        <nav className="nav">
          <NavLink to="/" end>Dashboard</NavLink>
          <NavLink to="/messages">Message Log</NavLink>
          <NavLink to="/failed">Failed</NavLink>
          <NavLink to="/relays">Relays</NavLink>
          <NavLink to="/routing">Routing</NavLink>
          <NavLink to="/smtp-auth">SMTP Auth</NavLink>
          <NavLink to="/api-keys">API Keys</NavLink>
          <NavLink to="/inbox">Local Inbox</NavLink>
          <NavLink to="/settings">Settings</NavLink>
          <NavLink to="/system">System</NavLink>
        </nav>
        <button onClick={logout} style={{ marginTop: 18, width: "100%" }}>Sign out</button>
      </aside>
      <main className="main"><Outlet /></main>
    </div>
  );
}

const router = createBrowserRouter([
  {
    element: <Layout />,
    children: [
      { path: "/", element: <Dashboard /> },
      { path: "/messages", element: <Messages /> },
      { path: "/failed", element: <Failed /> },
      { path: "/relays", element: <Relays /> },
      { path: "/routing", element: <Routing /> },
      { path: "/smtp-auth", element: <SmtpAuth /> },
      { path: "/api-keys", element: <ApiKeys /> },
      { path: "/inbox", element: <LocalInbox /> },
      { path: "/settings", element: <Settings /> },
      { path: "/system", element: <System /> },
    ],
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
