import { useEffect, useState } from "react";
import { api, type SystemConfig } from "../lib/api";

// Basic CIDR validation (IPv4 a.b.c.d/0-32, or an IPv6-ish addr/0-128) to stop mistyped entries.
const IPV4_CIDR = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})\/(\d|[12]\d|3[0-2])$/;
const IPV6_CIDR = /^[0-9a-fA-F:]+\/(\d|[1-9]\d|1[01]\d|12[0-8])$/;
function validCidr(v: string): boolean {
  if (IPV4_CIDR.test(v)) return v.split("/")[0].split(".").every((o) => Number(o) <= 255);
  return IPV6_CIDR.test(v);
}

export function AccessControl() {
  const [cfg, setCfg] = useState<SystemConfig | null>(null);
  useEffect(() => { api.settings.config().then(setCfg).catch(() => setCfg(null)); }, []);
  if (!cfg) return <div className="center">Loading…</div>;

  return (
    <>
      <h1 className="page-title">Access Control</h1>
      <p className="muted" style={{ marginTop: -10, marginBottom: 22, maxWidth: 680 }}>
        Control which source IPs may connect, by network range (CIDR). A list with no ranges accepts
        connections from <strong>anywhere</strong>; add ranges to restrict it. Changes apply as soon as you save.
      </p>

      <AclSection
        title="SMTP listener"
        intro="Hosts allowed to submit mail over SMTP."
        risky
        initial={cfg.listener.allowedCidrs}
        onSave={(cidrs) => api.settings.putListener({ allowedCidrs: cidrs })}
      />

      <AclSection
        title="HTTP API"
        intro="Hosts allowed to call the ingestion API. Requests still need a valid API key."
        initial={cfg.api.allowedCidrs}
        onSave={(cidrs) => api.settings.putApi({ allowedCidrs: cidrs })}
      />
    </>
  );
}

function AclSection({ title, intro, initial, onSave, risky }: {
  title: string; intro: string; initial: string[]; risky?: boolean;
  onSave: (cidrs: string[]) => Promise<unknown>;
}) {
  const [saved, setSaved] = useState<string[]>(initial);
  const [list, setList] = useState<string[]>(initial);
  const [entry, setEntry] = useState("");
  const [err, setErr] = useState<string | null>(null);
  const [msg, setMsg] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const dirty = JSON.stringify(list) !== JSON.stringify(saved);
  const open = list.length === 0;

  const add = () => {
    const v = entry.trim();
    if (!v) return;
    if (!validCidr(v)) { setErr("Enter a valid CIDR, e.g. 10.0.0.0/8 or 192.168.1.5/32."); return; }
    if (list.includes(v)) { setErr("That range is already in the list."); return; }
    setErr(null); setMsg(null); setList([...list, v]); setEntry("");
  };
  const remove = (c: string) => { setMsg(null); setList(list.filter((x) => x !== c)); };
  const save = async () => {
    setBusy(true); setMsg(null);
    try { await onSave(list); setSaved(list); setMsg("Saved."); }
    catch (e) { setMsg(`Error: ${(e as Error).message}`); }
    finally { setBusy(false); }
  };

  // Status pill: green when restricted, red/amber when open (red only for the listener, where open = relay risk).
  const pill = open
    ? { cls: risky ? "badge error" : "badge denied", text: "Open to all sources" }
    : { cls: "badge ok", text: `Restricted · ${list.length} range${list.length === 1 ? "" : "s"}` };
  const explain = open
    ? (risky
        ? "Any host that can reach this port can relay mail. Add ranges below to lock it down."
        : "Any host can reach the API (a valid API key is still required).")
    : "Only the ranges below may connect.";

  return (
    <div className="panel" style={{ maxWidth: 680 }}>
      <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", gap: 12 }}>
        <h2 style={{ margin: 0, textTransform: "none", letterSpacing: 0, fontSize: 16, color: "var(--text)" }}>{title}</h2>
        <span className={pill.cls}>{pill.text}</span>
      </div>
      <p className="muted" style={{ fontSize: 13, margin: "6px 0 2px" }}>{intro}</p>
      <p className="muted" style={{ fontSize: 13, margin: "0 0 14px" }}>{explain}</p>

      {/* CIDR chips */}
      <div style={{ display: "flex", flexWrap: "wrap", gap: 8, minHeight: 34 }}>
        {open && (
          <span style={{ fontSize: 13, color: "var(--muted)", alignSelf: "center", fontStyle: "italic" }}>
            No ranges — accepting all source IPs.
          </span>
        )}
        {list.map((c) => (
          <span key={c} style={{
            display: "inline-flex", alignItems: "center", gap: 8,
            background: "var(--panel-2)", border: "1px solid var(--border)",
            borderRadius: 999, padding: "5px 6px 5px 12px", fontSize: 13,
          }}>
            <code style={{ background: "none", padding: 0 }}>{c}</code>
            <button onClick={() => remove(c)} title="Remove" style={{
              border: "none", background: "transparent", padding: "0 4px",
              lineHeight: 1, fontSize: 15, color: "var(--muted)", cursor: "pointer",
            }}>×</button>
          </span>
        ))}
      </div>

      {/* Add row */}
      <div style={{ display: "flex", gap: 8, marginTop: 14 }}>
        <input
          placeholder="Add a range, e.g. 10.0.0.0/8 or 203.0.113.7/32"
          value={entry}
          onChange={(e) => { setEntry(e.target.value); if (err) setErr(null); }}
          onKeyDown={(e) => { if (e.key === "Enter") add(); }}
          style={{ flex: 1 }}
        />
        <button onClick={add} disabled={!entry.trim()}>Add</button>
      </div>
      {err && <p style={{ color: "var(--red)", fontSize: 13, margin: "8px 0 0" }}>{err}</p>}

      {/* Save row */}
      <div style={{ display: "flex", gap: 10, alignItems: "center", marginTop: 16, paddingTop: 14, borderTop: "1px solid var(--border)" }}>
        <button onClick={save} disabled={busy || !dirty}>Save changes</button>
        {dirty && !msg && <span className="muted" style={{ fontSize: 12 }}>Unsaved changes</span>}
        {msg && <span className={msg.startsWith("Error") ? "badge error" : "badge ok"}>{msg}</span>}
      </div>
    </div>
  );
}
