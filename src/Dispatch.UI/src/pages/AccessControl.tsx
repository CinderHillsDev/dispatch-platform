import { useEffect, useState } from "react";
import { api, type SystemConfig } from "../lib/api";

// Basic CIDR validation (IPv4 a.b.c.d/0-32, or an IPv6-ish addr/0-128) to stop mistyped entries.
const IPV4_CIDR = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})\/(\d|[12]\d|3[0-2])$/;
const IPV6_CIDR = /^[0-9a-fA-F:]+\/(\d|[1-9]\d|1[01]\d|12[0-8])$/;
function validCidr(v: string): boolean {
  if (IPV4_CIDR.test(v)) return v.split("/")[0].split(".").every((o) => Number(o) <= 255);
  return IPV6_CIDR.test(v);
}

type TargetId = "smtp" | "api";
type Target = {
  id: TargetId;
  label: string;
  intro: string;
  warnWhenOpen: boolean;
  save: (cidrs: string[]) => Promise<unknown>;
};

export function AccessControl() {
  const [cfg, setCfg] = useState<SystemConfig | null>(null);
  const [active, setActive] = useState<TargetId>("smtp");
  // Working copy of each list, keyed by target — switching the selector just swaps which one is shown.
  const [lists, setLists] = useState<Record<TargetId, string[]>>({ smtp: [], api: [] });
  const [saved, setSaved] = useState<Record<TargetId, string[]>>({ smtp: [], api: [] });
  const [entry, setEntry] = useState("");
  const [err, setErr] = useState<string | null>(null);
  const [msg, setMsg] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    api.settings.config().then((c) => {
      setCfg(c);
      const init = { smtp: c.listener.allowedCidrs, api: c.api.allowedCidrs };
      setLists(init);
      setSaved(init);
    }).catch(() => setCfg(null));
  }, []);

  if (!cfg) return <div className="center">Loading…</div>;

  const targets: Record<TargetId, Target> = {
    smtp: {
      id: "smtp",
      label: "SMTP listener",
      intro: "IP ranges allowed to send mail over SMTP.",
      warnWhenOpen: true,
      save: (cidrs) => api.settings.putListener({ allowedCidrs: cidrs }),
    },
    api: {
      id: "api",
      label: "HTTP API",
      intro: "IP ranges allowed to call the ingestion API (also protected by API keys).",
      warnWhenOpen: false,
      save: (cidrs) => api.settings.putApi({ allowedCidrs: cidrs }),
    },
  };

  const t = targets[active];
  const list = lists[active];
  const dirty = JSON.stringify(list) !== JSON.stringify(saved[active]);
  const open = list.length === 0;

  const setList = (next: string[]) => setLists((l) => ({ ...l, [active]: next }));
  const switchTo = (id: TargetId) => { setActive(id); setEntry(""); setErr(null); setMsg(null); };

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
    try { await t.save(list); setSaved((s) => ({ ...s, [active]: list })); setMsg("Saved."); }
    catch (e) { setMsg(`Error: ${(e as Error).message}`); }
    finally { setBusy(false); }
  };

  return (
    <>
      <h1 className="page-title">Access Control</h1>
      <p className="muted" style={{ marginTop: -10, marginBottom: 18 }}>
        Source-IP allow-lists decide who may connect to the SMTP listener and the HTTP API. An empty list
        means <strong>allow from anywhere</strong> — restrict these to avoid an open relay. Changes apply
        immediately on save.
      </p>

      <div className="panel" style={{ maxWidth: 640 }}>
        {/* Selector: which listener does the list below apply to? */}
        <div className="tabbar" style={{ display: "flex", gap: 6, marginBottom: 14 }}>
          {(Object.values(targets)).map((target) => {
            const isActive = target.id === active;
            const targetDirty = JSON.stringify(lists[target.id]) !== JSON.stringify(saved[target.id]);
            return (
              <button
                key={target.id}
                onClick={() => switchTo(target.id)}
                style={{
                  background: "transparent",
                  border: "none",
                  borderBottom: `2px solid ${isActive ? "var(--blue)" : "transparent"}`,
                  borderRadius: 0,
                  padding: "6px 10px",
                  color: isActive ? "var(--text)" : "var(--muted)",
                  cursor: "pointer",
                }}
              >
                {target.label}{targetDirty ? " •" : ""}
              </button>
            );
          })}
        </div>

        <p className="muted" style={{ fontSize: 13, marginTop: -4 }}>{t.intro}</p>

        <p style={{ fontSize: 13 }}>
          {open
            ? <span className={t.warnWhenOpen ? "badge error" : "badge denied"}>{t.warnWhenOpen ? "⚠ open to everyone" : "allows anywhere"}</span>
            : <span className="badge ok">{list.length} range{list.length === 1 ? "" : "s"} allowed</span>}
          <span className="muted" style={{ marginLeft: 8 }}>
            {open
              ? (t.warnWhenOpen ? "Anyone who can reach this port can relay mail. Add ranges to lock it down." : "Every source IP is allowed.")
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
          <button onClick={save} disabled={busy || !dirty}>Save {t.label}</button>
          {dirty && !msg && <span className="muted" style={{ fontSize: 12 }}>Unsaved changes</span>}
          {msg && <span className={msg.startsWith("Error") ? "badge error" : "badge ok"}>{msg}</span>}
        </div>
      </div>
    </>
  );
}
