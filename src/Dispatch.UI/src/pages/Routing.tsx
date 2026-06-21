import { useEffect, useState } from "react";
import { api, type RelayListItem, type RuleItem, type SimulateResult } from "../lib/api";
import { Modal } from "../Modal";
import { ActionsMenu } from "../ActionsMenu";

export function Routing() {
  const [rules, setRules] = useState<RuleItem[]>([]);
  const [relays, setRelays] = useState<RelayListItem[]>([]);
  const [adding, setAdding] = useState(false);
  const [editing, setEditing] = useState<RuleItem | null>(null);
  const [simFrom, setSimFrom] = useState("");
  const [simTo, setSimTo] = useState("");
  const [sim, setSim] = useState<SimulateResult | null>(null);
  const [err, setErr] = useState<string | null>(null);

  const refresh = async () => {
    const [rs, rl] = await Promise.all([api.rules.list(), api.relays.list()]);
    setRules(rs); setRelays(rl);
  };
  useEffect(() => { refresh(); }, []);

  const move = async (index: number, dir: -1 | 1) => {
    const next = [...rules];
    const j = index + dir;
    if (j < 0 || j >= next.length) return;
    [next[index], next[j]] = [next[j], next[index]];
    setRules(next);
    await api.rules.reorder(next.map((r) => r.id));
    await refresh();
  };

  const del = async (r: RuleItem) => {
    if (!confirm(`Delete routing rule “${r.name}”?`)) return;
    try { await api.rules.remove(r.id); await refresh(); }
    catch (e) { setErr((e as Error).message); }
  };

  const toggle = async (r: RuleItem) => {
    try {
      await api.rules.update(r.id, {
        name: r.name, recipientPattern: r.recipientPattern, senderPattern: r.senderPattern,
        relayId: r.relayId, enabled: !r.enabled,
      });
      await refresh();
    } catch (e) { setErr((e as Error).message); }
  };

  const catchAll = relays.find((r) => r.isDefault);

  const simulate = async () => {
    setErr(null);
    try { setSim(await api.rules.simulate(simFrom, simTo)); }
    catch (e) { setErr((e as Error).message); }
  };

  return (
    <div style={{ maxWidth: 960 }}>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 12 }}>
        <h1 className="page-title" style={{ margin: 0 }}>Routing Rules</h1>
        <button onClick={() => setAdding(true)} disabled={relays.length === 0}>+ Add rule</button>
      </div>
      <p className="muted" style={{ fontSize: 13, margin: "8px 0 18px" }}>
        Rules are checked in order (#1 first); the first match wins. Mail that matches no rule falls through
        to the catch-all at the bottom.
      </p>

      <div className="panel" style={{ padding: 0 }}>
        <table>
          <thead><tr><th>#</th><th>Name</th><th>Recipient</th><th>Sender</th><th>Relay</th><th>Enabled</th><th></th></tr></thead>
          <tbody>
            {rules.map((r, i) => (
              <tr key={r.id}>
                <td>{i + 1}</td>
                <td>{r.name}</td>
                <td>{r.recipientPattern ?? <span className="muted">(any)</span>}</td>
                <td>{r.senderPattern ?? <span className="muted">(any)</span>}</td>
                <td>{r.relayName}</td>
                <td>{r.enabled ? "✓" : "—"}</td>
                <td style={{ textAlign: "right" }}>
                  <ActionsMenu items={[
                    { label: "Edit", onClick: () => setEditing(r) },
                    { label: r.enabled ? "Disable" : "Enable", onClick: () => toggle(r) },
                    { label: "Move up", onClick: () => move(i, -1), disabled: i === 0 },
                    { label: "Move down", onClick: () => move(i, 1), disabled: i === rules.length - 1 },
                    { label: "Delete", danger: true, onClick: () => del(r) },
                  ]} />
                </td>
              </tr>
            ))}
            {/* The catch-all is always present (the engine's fallback when no rule matches). Shown as a
                built-in, non-editable row so the routing picture is complete. */}
            <tr style={{ background: "var(--panel-2)" }}>
              <td className="muted">100</td>
              <td>Catch-all <span className="badge ok">default</span></td>
              <td className="muted">(any)</td>
              <td className="muted">(any)</td>
              <td>{catchAll ? catchAll.name : <span className="muted">— no relay configured —</span>}</td>
              <td>✓</td>
              <td style={{ textAlign: "right" }}><span className="muted" style={{ fontSize: 11 }}>built-in</span></td>
            </tr>
          </tbody>
        </table>
      </div>

      {adding && <RuleModal relays={relays} onClose={() => setAdding(false)} onSaved={refresh} />}
      {editing && <RuleModal relays={relays} rule={editing} onClose={() => setEditing(null)} onSaved={refresh} />}

      <div className="panel" style={{ maxWidth: 620 }}>
        <h2>Simulate</h2>
        <p className="muted" style={{ fontSize: 13, marginTop: -6 }}>Check which relay a given sender/recipient would route to.</p>
        <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
          <input placeholder="from@sender.com" value={simFrom} onChange={(e) => setSimFrom(e.target.value)} />
          <input placeholder="to@recipient.com" value={simTo} onChange={(e) => setSimTo(e.target.value)} />
          <button onClick={simulate} disabled={!simFrom || !simTo}>Simulate</button>
        </div>
        {sim && (
          <div style={{ marginTop: 12 }}>
            {sim.matched
              ? <span className="badge ok">Matched rule “{sim.matchedRuleName}” → {sim.relayName} ({sim.provider})</span>
              : <span className="badge denied">No rule matched → catch-all relay {sim.relayName} ({sim.provider})</span>}
          </div>
        )}
      </div>

      {err && <p><span className="badge error">{err}</span></p>}
    </div>
  );
}

// Add/edit routing-rule dialog — a labelled form instead of the old free-text row.
function RuleModal({ relays, rule, onClose, onSaved }: {
  relays: RelayListItem[]; rule?: RuleItem; onClose: () => void; onSaved: () => Promise<void>;
}) {
  const [name, setName] = useState(rule?.name ?? "");
  const [recipient, setRecipient] = useState(rule?.recipientPattern ?? "");
  const [sender, setSender] = useState(rule?.senderPattern ?? "");
  const [relayId, setRelayId] = useState<number | null>(rule?.relayId ?? relays[0]?.id ?? null);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);

  const add = async () => {
    if (!name.trim()) { setErr("Name is required."); return; }
    if (relayId === null) { setErr("Choose a target relay."); return; }
    if (!recipient.trim() && !sender.trim()) { setErr("Enter a recipient and/or sender pattern."); return; }
    setBusy(true); setErr(null);
    const body = { name: name.trim(), recipientPattern: recipient.trim() || null, senderPattern: sender.trim() || null, relayId };
    try {
      if (rule) await api.rules.update(rule.id, { ...body, enabled: rule.enabled });
      else await api.rules.create(body);
      await onSaved();
      onClose();
    } catch (e) { setErr((e as Error).message); }
    finally { setBusy(false); }
  };

  return (
    <Modal title={rule ? "Edit routing rule" : "Add routing rule"} onClose={onClose}>
      <div style={{ display: "grid", gap: 12 }}>
        <Lbl label="Rule name"><input value={name} onChange={(e) => setName(e.target.value)} style={{ width: "100%" }} /></Lbl>
        <Lbl label="Recipient pattern (optional)"><input placeholder="e.g. acme.com or *.acme.com" value={recipient} onChange={(e) => setRecipient(e.target.value)} style={{ width: "100%" }} /></Lbl>
        <Lbl label="Sender pattern (optional)"><input placeholder="e.g. app.myco.com" value={sender} onChange={(e) => setSender(e.target.value)} style={{ width: "100%" }} /></Lbl>
        <Lbl label="Send matching mail through">
          <select value={relayId ?? ""} onChange={(e) => setRelayId(Number(e.target.value))} style={{ width: "100%" }}>
            {relays.map((r) => <option key={r.id} value={r.id}>{r.name}{r.isDefault ? " (catch-all)" : ""}</option>)}
          </select>
        </Lbl>
        <p className="muted" style={{ fontSize: 12, margin: 0 }}>Match by recipient and/or sender domain — at least one is required.</p>
        {err && <p style={{ color: "var(--red)", fontSize: 13, margin: 0 }}>{err}</p>}
        <div style={{ display: "flex", gap: 8, justifyContent: "flex-end", marginTop: 4 }}>
          <button onClick={onClose}>Cancel</button>
          <button onClick={add} disabled={busy}>{busy ? "Saving…" : rule ? "Save changes" : "Add rule"}</button>
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
