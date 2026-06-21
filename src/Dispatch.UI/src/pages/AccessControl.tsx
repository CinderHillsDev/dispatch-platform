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
      <p className="muted" style={{ marginTop: -10, marginBottom: 18 }}>
        Source-IP allow-lists decide who may connect to the SMTP listener and the HTTP API. An empty list
        means <strong>allow from anywhere</strong> — restrict these to avoid an open relay. Changes apply
        immediately on save.
      </p>

      <AclSection
        title="SMTP listener"
        intro="IP ranges allowed to send mail over SMTP."
        warnWhenOpen
        initial={cfg.listener.allowedCidrs}
        onSave={(cidrs) => api.settings.putListener({ allowedCidrs: cidrs })}
      />

      <AclSection
        title="HTTP API"
        intro="IP ranges allowed to call the ingestion API (also protected by API keys)."
        initial={cfg.api.allowedCidrs}
        onSave={(cidrs) => api.settings.putApi({ allowedCidrs: cidrs })}
      />
    </>
  );
}

function AclSection({ title, intro, initial, onSave, warnWhenOpen }: {
  title: string; intro: string; initial: string[]; warnWhenOpen?: boolean;
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
  const preset = (cidrs: string[]) => { setErr(null); setMsg(null); setList(cidrs); };
  const save = async () => {
    setBusy(true); setMsg(null);
    try { await onSave(list); setSaved(list); setMsg("Saved."); }
    catch (e) { setMsg(`Error: ${(e as Error).message}`); }
    finally { setBusy(false); }
  };

  return (
    <div className="panel" style={{ maxWidth: 640 }}>
      <h2>{title}</h2>
      <p className="muted" style={{ fontSize: 13, marginTop: -6 }}>{intro}</p>

      <p style={{ fontSize: 13 }}>
        {open
          ? <span className={warnWhenOpen ? "badge error" : "badge denied"}>{warnWhenOpen ? "⚠ open to everyone" : "allows anywhere"}</span>
          : <span className="badge ok">{list.length} range{list.length === 1 ? "" : "s"} allowed</span>}
        <span className="muted" style={{ marginLeft: 8 }}>
          {open
            ? (warnWhenOpen ? "Anyone who can reach this port can relay mail. Add ranges to lock it down." : "Every source IP is allowed.")
            : "Only the ranges below may connect."}
        </span>
      </p>

      <table>
        <tbody>
          {list.map((c) => (
            <tr key={c}>
              <td><code>{c}</code></td>
              <td style={{ textAlign: "right" }}><button onClick={() => { setList(list.filter((x) => x !== c)); setMsg(null); }}>Remove</button></td>
            </tr>
          ))}
          {open && <tr><td className="muted">No restrictions — allowing all source IPs.</td></tr>}
        </tbody>
      </table>

      <div style={{ display: "flex", gap: 8, marginTop: 10, flexWrap: "wrap" }}>
        <input placeholder="Add CIDR, e.g. 10.0.0.0/8" value={entry}
          onChange={(e) => setEntry(e.target.value)} onKeyDown={(e) => { if (e.key === "Enter") add(); }} style={{ minWidth: 200 }} />
        <button onClick={add}>Add</button>
      </div>
      <div style={{ display: "flex", gap: 8, marginTop: 8, flexWrap: "wrap" }}>
        <span className="muted" style={{ fontSize: 12, alignSelf: "center" }}>Presets:</span>
        <button onClick={() => preset(["127.0.0.1/32", "::1/128"])}>Loopback only</button>
        <button onClick={() => preset(["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7"])}>Private ranges</button>
        <button onClick={() => preset([])}>Allow all</button>
      </div>

      {err && <p style={{ color: "var(--red)", fontSize: 13 }}>{err}</p>}
      <div style={{ display: "flex", gap: 10, alignItems: "center", marginTop: 12 }}>
        <button onClick={save} disabled={busy || !dirty}>Save</button>
        {dirty && !msg && <span className="muted" style={{ fontSize: 12 }}>Unsaved changes</span>}
        {msg && <span className={msg.startsWith("Error") ? "badge error" : "badge ok"}>{msg}</span>}
      </div>
    </div>
  );
}
