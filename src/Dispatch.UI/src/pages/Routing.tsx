import { useEffect, useState } from "react";
import { api, type RelayListItem, type RuleItem, type SimulateResult } from "../lib/api";

export function Routing() {
  const [rules, setRules] = useState<RuleItem[]>([]);
  const [relays, setRelays] = useState<RelayListItem[]>([]);
  const [name, setName] = useState("");
  const [recipient, setRecipient] = useState("");
  const [sender, setSender] = useState("");
  const [relayId, setRelayId] = useState<number | null>(null);
  const [simFrom, setSimFrom] = useState("");
  const [simTo, setSimTo] = useState("");
  const [sim, setSim] = useState<SimulateResult | null>(null);
  const [err, setErr] = useState<string | null>(null);

  const refresh = async () => {
    const [rs, rl] = await Promise.all([api.rules.list(), api.relays.list()]);
    setRules(rs); setRelays(rl);
    if (relayId === null && rl.length) setRelayId(rl[0].id);
  };
  useEffect(() => { refresh(); }, []);

  const add = async () => {
    setErr(null);
    if (!name.trim() || relayId === null) return;
    if (!recipient.trim() && !sender.trim()) { setErr("Enter a recipient and/or sender pattern."); return; }
    try {
      await api.rules.create({ name: name.trim(), recipientPattern: recipient.trim() || null, senderPattern: sender.trim() || null, relayId });
      setName(""); setRecipient(""); setSender("");
      await refresh();
    } catch (e) { setErr((e as Error).message); }
  };

  const move = async (index: number, dir: -1 | 1) => {
    const next = [...rules];
    const j = index + dir;
    if (j < 0 || j >= next.length) return;
    [next[index], next[j]] = [next[j], next[index]];
    setRules(next);
    await api.rules.reorder(next.map((r) => r.id));
    await refresh();
  };

  const simulate = async () => {
    setErr(null);
    try { setSim(await api.rules.simulate(simFrom, simTo)); }
    catch (e) { setErr((e as Error).message); }
  };

  return (
    <>
      <h1 className="page-title">Routing Rules</h1>

      <div className="panel">
        <h2>Rules — evaluated top to bottom; first match wins</h2>
        <table>
          <thead><tr><th>#</th><th>Name</th><th>Recipient</th><th>Sender</th><th>Relay</th><th>Enabled</th><th></th></tr></thead>
          <tbody>
            {rules.map((r, i) => (
              <tr key={r.id}>
                <td>{r.priority}</td>
                <td>{r.name}</td>
                <td>{r.recipientPattern ?? <span className="muted">(any)</span>}</td>
                <td>{r.senderPattern ?? <span className="muted">(any)</span>}</td>
                <td>{r.relayName}</td>
                <td>{r.enabled ? "✓" : "—"}</td>
                <td style={{ display: "flex", gap: 4 }}>
                  <button onClick={() => move(i, -1)} disabled={i === 0}>↑</button>
                  <button onClick={() => move(i, 1)} disabled={i === rules.length - 1}>↓</button>
                  <button onClick={async () => { await api.rules.remove(r.id); await refresh(); }}>✕</button>
                </td>
              </tr>
            ))}
            {rules.length === 0 && <tr><td colSpan={7} className="center">No rules — all mail goes to the catch-all relay.</td></tr>}
          </tbody>
        </table>

        <div style={{ marginTop: 14, display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}>
          <input placeholder="Rule name" value={name} onChange={(e) => setName(e.target.value)} />
          <input placeholder="Recipient e.g. *.acme.com" value={recipient} onChange={(e) => setRecipient(e.target.value)} />
          <input placeholder="Sender e.g. app.myco.com" value={sender} onChange={(e) => setSender(e.target.value)} />
          <select value={relayId ?? ""} onChange={(e) => setRelayId(Number(e.target.value))}>
            {relays.map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
          </select>
          <button onClick={add}>Add rule</button>
        </div>
      </div>

      <div className="panel" style={{ maxWidth: 620 }}>
        <h2>Simulate</h2>
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
    </>
  );
}
