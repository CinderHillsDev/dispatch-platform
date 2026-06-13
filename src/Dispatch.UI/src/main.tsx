import React from "react";
import ReactDOM from "react-dom/client";
import { createBrowserRouter, RouterProvider, NavLink, Outlet } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { Dashboard } from "./pages/Dashboard";
import { Messages } from "./pages/Messages";
import { Relays } from "./pages/Relays";
import { Routing } from "./pages/Routing";
import { LocalInbox } from "./pages/LocalInbox";
import { Failed } from "./pages/Failed";
import { SmtpAuth } from "./pages/SmtpAuth";
import { Settings } from "./pages/Settings";
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
          <NavLink to="/inbox">Local Inbox</NavLink>
          <NavLink to="/settings">Settings</NavLink>
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
      { path: "/inbox", element: <LocalInbox /> },
      { path: "/settings", element: <Settings /> },
    ],
  },
]);

const queryClient = new QueryClient({ defaultOptions: { queries: { refetchOnWindowFocus: false } } });

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <QueryClientProvider client={queryClient}>
      <AuthGate>
        <RouterProvider router={router} />
      </AuthGate>
    </QueryClientProvider>
  </React.StrictMode>,
);
