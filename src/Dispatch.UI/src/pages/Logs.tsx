import { useCallback, useEffect, useRef, useState } from "react";
import { api, type AuditEntry } from "../lib/api";
import { HeaderFilter, FilterChoices } from "../HeaderFilter";

const KINDS: [string, string | null][] = [["All", null], ["Audit", "audit"], ["Relay", "relay"], ["System", "system"]];
const CATEGORIES = ["Auth", "ApiKey", "Config", "Access", "SmtpAuth", "Relay", "System"];
const SEVERITIES = ["Info", "Notice", "Warning", "Error"];

interface Filters { kind: string | null; category: string; severity: string; search: string; }
const EMPTY: Filters = { kind: null, category: "", severity: "", search: "" };

function toParams(f: Filters, cursor: { at: string; id: number } | null): URLSearchParams {
  const p = new URLSearchParams();
  p.set("limit", "50");
  if (f.kind) p.set("kind", f.kind);
  if (f.category) p.set("category", f.category);
  if (f.severity) p.set("severity", f.severity);
  if (f.search) p.set("search", f.search);
  if (cursor) { p.set("cursorAt", cursor.at); p.set("cursorId", String(cursor.id)); }
  return p;
}

const sevBadge = (s: string) =>
  s === "Error" ? "badge error" : s === "Warning" ? "badge denied" : s === "Notice" ? "badge" : "badge ok";

const kindLabel = (k: string) => k.charAt(0).toUpperCase() + k.slice(1);

export function Logs() {
  const [filters, setFilters] = useState<Filters>(EMPTY);
  const [rows, setRows] = useState<AuditEntry[]>([]);
  const [cursor, setCursor] = useState<{ at: string; id: number } | null>(null);
  const [done, setDone] = useState(false);
  const [loading, setLoading] = useState(false);

  const set = <K extends keyof Filters>(k: K, v: Filters[K]) => setFilters((f) => ({ ...f, [k]: v }));
  const anyActive = !!(filters.kind || filters.category || filters.severity || filters.search);

  const loadFirst = useCallback(async () => {
    setLoading(true);
    try {
      const page = await api.audit(toParams(filters, null));
      setRows(page.rows); setCursor(page.nextCursor); setDone(page.nextCursor === null);
    } finally { setLoading(false); }
  }, [filters]);

  // Debounced reload on filter change (search typing).
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(() => {
    if (timer.current) clearTimeout(timer.current);
    timer.current = setTimeout(() => { loadFirst(); }, 250);
    return () => { if (timer.current) clearTimeout(timer.current); };
  }, [loadFirst]);

  const loadMore = async () => {
    if (!cursor) return;
    setLoading(true);
    try {
      const page = await api.audit(toParams(filters, cursor));
      setRows((prev) => [...prev, ...page.rows]);
      setCursor(page.nextCursor); setDone(page.nextCursor === null);
    } finally { setLoading(false); }
  };

  return (
    <>
      <h1 className="page-title">Logs</h1>
      <p className="muted" style={{ marginTop: -10, marginBottom: 14 }}>
        Audit trail, relay/delivery problems, and system events.
      </p>

      {/* Quick kind filter + search + clear */}
      <div style={{ display: "flex", alignItems: "center", gap: 10, marginBottom: 14, flexWrap: "wrap" }}>
        <div style={{ display: "inline-flex", border: "1px solid var(--border)", borderRadius: 8, overflow: "hidden" }}>
          {KINDS.map(([label, k]) => {
            const on = (filters.kind ?? null) === k;
            return (
              <button key={label} type="button" onClick={() => set("kind", k)}
                style={{ border: "none", borderRadius: 0, padding: "6px 14px", fontSize: 13, background: on ? "var(--blue)" : "transparent", color: on ? "#fff" : "var(--muted)", fontWeight: on ? 600 : 400 }}>
                {label}
              </button>
            );
          })}
        </div>
        <input placeholder="Search event, detail, actor…" value={filters.search}
          onChange={(e) => set("search", e.target.value)} style={{ flex: "1 1 220px", minWidth: 180 }} />
        {anyActive && <button type="button" onClick={() => setFilters(EMPTY)}>Clear filters</button>}
        <button type="button" onClick={() => loadFirst()} disabled={loading} title="Refresh">↻</button>
      </div>

      <div className="panel" style={{ padding: 0, overflowX: "auto" }}>
        <table>
          <thead>
            <tr>
              <th>Time</th>
              <th>Kind</th>
              <HeaderFilter label="Category" active={!!filters.category} onClear={() => set("category", "")}>
                <FilterChoices options={[["Any", null], ...CATEGORIES.map((c) => [c, c] as [string, string | null])]}
                  selected={filters.category || null} onSelect={(v) => set("category", v ?? "")} />
              </HeaderFilter>
              <th>Event</th>
              <HeaderFilter label="Severity" active={!!filters.severity} onClear={() => set("severity", "")}>
                <FilterChoices options={[["Any", null], ...SEVERITIES.map((c) => [c, c] as [string, string | null])]}
                  selected={filters.severity || null} onSelect={(v) => set("severity", v ?? "")} />
              </HeaderFilter>
              <th>Source</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.id}>
                <td style={{ whiteSpace: "nowrap" }}>{new Date(r.loggedAt).toLocaleString()}</td>
                <td><span className="badge" style={{ textTransform: "capitalize" }}>{kindLabel(r.kind)}</span></td>
                <td style={{ whiteSpace: "nowrap" }}>{r.category}</td>
                <td>
                  <div>{r.event}</div>
                  {r.detail && <div className="muted" style={{ fontSize: 12, whiteSpace: "pre-wrap", wordBreak: "break-word" }}>{r.detail}</div>}
                </td>
                <td><span className={sevBadge(r.severity)}>{r.severity}</span></td>
                <td style={{ whiteSpace: "nowrap" }} className="muted">{r.sourceIp ?? "-"}{r.actor ? ` · ${r.actor}` : ""}</td>
              </tr>
            ))}
          </tbody>
        </table>
        {rows.length === 0 && !loading && <div className="center">No log entries match these filters.</div>}
        {loading && rows.length === 0 && <div className="center">Loading…</div>}
      </div>

      {!done && rows.length > 0 && (
        <button onClick={loadMore} disabled={loading} style={{ marginTop: 12 }}>Load more</button>
      )}
    </>
  );
}
