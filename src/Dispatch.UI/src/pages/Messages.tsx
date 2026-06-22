import { useCallback, useEffect, useRef, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { api, type MessageRow, type MessageDetail, type RelayListItem, type RuleItem, type ApiKeyItem } from "../lib/api";
import { Modal } from "../Modal";

function badgeClass(status: string) {
  if (status === "OK") return "badge ok";
  if (status === "Denied") return "badge denied";
  return "badge error";
}

// Truncating cell style (relies on the global `white-space: nowrap` on td) — keeps wide text columns
// bounded so the table fits the page without a horizontal scrollbar; full value shown via title=.
const cap = (w: number): React.CSSProperties => ({ maxWidth: w, overflow: "hidden", textOverflow: "ellipsis" });

// Compact timestamp for the log list (full timestamp is in the detail modal + the cell's title tooltip).
const fmtTime = (iso: string) => new Date(iso).toLocaleString([], { month: "numeric", day: "numeric", hour: "2-digit", minute: "2-digit" });

// Status filter chips map to the relay_log `event` column (spec §9.2). Provider tests are logged as
// real Delivered/Failed events (distinguished only by ingest source "Test"), so there's no test status.
const EVENT_OPTIONS = ["Delivered", "Failed", "Retrying", "Denied"];

interface Filters {
  events: string[];
  source: string;
  relay: string;
  rule: string;
  apiKeyId: string;
  subject: string;
  fromDomain: string;
  toDomain: string;
  tag: string;
  from: string;
  to: string;
}

const EMPTY: Filters = { events: [], source: "", relay: "", rule: "", apiKeyId: "", subject: "", fromDomain: "", toDomain: "", tag: "", from: "", to: "" };

function toParams(f: Filters): URLSearchParams {
  const p = new URLSearchParams();
  p.set("limit", "50");
  if (f.events.length) p.set("event", f.events.join(","));
  if (f.source) p.set("source", f.source);
  if (f.relay) p.set("relay", f.relay);
  if (f.rule) p.set("rule", f.rule);
  if (f.apiKeyId) p.set("apiKeyId", f.apiKeyId);
  if (f.subject) p.set("subject", f.subject);
  if (f.fromDomain) p.set("fromDomain", f.fromDomain);
  if (f.toDomain) p.set("toDomain", f.toDomain);
  if (f.tag) p.set("tag", f.tag);
  // Date inputs are local dates; treat as start/end of day in UTC for the keyset query.
  if (f.from) p.set("from", new Date(f.from + "T00:00:00").toISOString());
  if (f.to) p.set("to", new Date(f.to + "T00:00:00Z").toISOString().slice(0, 10) + "T23:59:59.999Z");
  return p;
}

export function Messages() {
  const [filters, setFilters] = useState<Filters>(EMPTY);
  const [relays, setRelays] = useState<RelayListItem[]>([]);
  const [rules, setRules] = useState<RuleItem[]>([]);
  const [keys, setKeys] = useState<ApiKeyItem[]>([]);
  const [rows, setRows] = useState<MessageRow[]>([]);
  const [cursor, setCursor] = useState<{ at: string; id: number } | null>(null);
  const [loading, setLoading] = useState(false);
  const [done, setDone] = useState(false);
  const [selected, setSelected] = useState<number | null>(null);
  const [detail, setDetail] = useState<MessageDetail | null>(null);
  const [detailLoading, setDetailLoading] = useState(false);
  const [retrying, setRetrying] = useState(false);
  const [retryMsg, setRetryMsg] = useState<string | null>(null);

  const set = <K extends keyof Filters>(key: K, value: Filters[K]) =>
    setFilters((f) => ({ ...f, [key]: value }));

  // Reset just the given columns' filters back to their empty values (per-column "Clear").
  const clearKeys = (...keys: (keyof Filters)[]) =>
    setFilters((f) => { const n = { ...f }; keys.forEach((k) => { (n[k] as Filters[typeof k]) = EMPTY[k]; }); return n; });

  useEffect(() => { api.relays.list().then(setRelays).catch(() => setRelays([])); }, []);
  useEffect(() => { api.rules.list().then(setRules).catch(() => setRules([])); }, []);
  useEffect(() => { api.keys.list().then(setKeys).catch(() => setKeys([])); }, []);

  // Status is single-select (a message is exactly one of Delivered/Failed/…): clicking a chip selects only
  // it; clicking the active chip (or "All") clears the status filter.
  const selectEvent = (ev: string | null) =>
    setFilters((f) => ({ ...f, events: ev && f.events[0] !== ev ? [ev] : [] }));

  const advancedActive = !!(filters.from || filters.to || filters.relay || filters.rule || filters.source ||
    filters.apiKeyId || filters.fromDomain || filters.toDomain || filters.tag);
  const anyActive = advancedActive || filters.events.length > 0 || !!filters.subject;

  const loadMore = useCallback(async () => {
    if (!cursor) return;
    setLoading(true);
    const params = toParams(filters);
    params.set("cursorAt", cursor.at);
    params.set("cursorId", String(cursor.id));
    try {
      const page = await api.messages(params);
      setRows((prev) => [...prev, ...page.rows]);
      setCursor(page.nextCursor);
      setDone(page.nextCursor === null);
    } finally {
      setLoading(false);
    }
  }, [filters, cursor]);

  // Fetch the first page for the current filters. reset=true clears the list first (filter changes, so
  // stale rows don't linger); reset=false just replaces it (manual/auto refresh — no flicker).
  const loadFirst = useCallback(async (reset = false) => {
    if (reset) { setRows([]); setCursor(null); setDone(false); }
    setLoading(true);
    try {
      const page = await api.messages(toParams(filters));
      setRows(page.rows); setCursor(page.nextCursor); setDone(page.nextCursor === null);
    } finally { setLoading(false); }
  }, [filters]);

  // Reload the first page whenever filters change, debounced 300 ms (spec §9.2).
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(() => {
    if (timer.current) clearTimeout(timer.current);
    timer.current = setTimeout(() => { loadFirst(true); }, 300);
    return () => { if (timer.current) clearTimeout(timer.current); };
  }, [loadFirst]);

  // Optional auto-refresh of the first page (off by default).
  const [autoMs, setAutoMs] = useState(0);
  useEffect(() => {
    if (!autoMs) return;
    const t = setInterval(() => { loadFirst(); }, autoMs);
    return () => clearInterval(t);
  }, [autoMs, loadFirst]);

  const openDetail = useCallback((id: number) => {
    if (selected === id) { setSelected(null); setDetail(null); return; }
    setSelected(id); setDetail(null); setDetailLoading(true);
    api.message(id).then(setDetail).catch(() => setDetail(null)).finally(() => setDetailLoading(false));
  }, [selected]);

  // Deep-link: /messages?spool=<id> (e.g. from the Local Inbox) opens that message's detail directly.
  const [searchParams] = useSearchParams();
  const deepLinked = useRef(false);
  useEffect(() => {
    const spool = searchParams.get("spool");
    if (!spool || deepLinked.current) return;
    deepLinked.current = true;
    api.messageIdBySpool(spool)
      .then((r) => { setSelected(r.id); setDetailLoading(true); return api.message(r.id); })
      .then(setDetail).catch(() => setDetail(null)).finally(() => setDetailLoading(false));
  }, [searchParams]);

  return (
    <>
      <div>
        <h1 className="page-title">Message Log</h1>

        {/* Filters live in the column headers below (click a header). This bar reflects/clears them and
            holds the refresh + auto-refresh controls. */}
        <div style={{ display: "flex", alignItems: "center", gap: 10, marginBottom: 14, minHeight: 30 }}>
          <div style={{ flex: 1, display: "flex", alignItems: "center", gap: 10 }}>
            {anyActive
              ? <>
                  <span className="muted" style={{ fontSize: 12 }}>Filters active</span>
                  <button type="button" onClick={() => setFilters(EMPTY)}>Clear filters</button>
                </>
              : <span className="muted" style={{ fontSize: 12 }}>Click a column header to filter.</span>}
          </div>
          <button type="button" onClick={() => loadFirst()} disabled={loading} title="Refresh now">↻ Refresh</button>
          <label className="muted" style={{ display: "flex", alignItems: "center", gap: 6, fontSize: 12 }}>
            Auto-refresh
            <select value={autoMs} onChange={(e) => setAutoMs(Number(e.target.value))}>
              <option value={0}>Never</option>
              <option value={15000}>15s</option>
              <option value={30000}>30s</option>
              <option value={60000}>60s</option>
            </select>
          </label>
        </div>

        <div className="panel" style={{ padding: 0, overflowX: "auto" }}>
          <table>
            <thead>
              <tr>
                <HeaderFilter label="Time" active={!!(filters.from || filters.to)} onClear={() => clearKeys("from", "to")}>
                  <Labeled label="Received from"><input type="date" value={filters.from} onChange={(e) => set("from", e.target.value)} style={{ width: "100%" }} /></Labeled>
                  <Labeled label="Received to"><input type="date" value={filters.to} onChange={(e) => set("to", e.target.value)} style={{ width: "100%" }} /></Labeled>
                </HeaderFilter>
                <HeaderFilter label="Status" active={filters.events.length > 0} onClear={() => clearKeys("events")}>
                  <FilterChoices
                    options={[["All", null] as [string, string | null], ...EVENT_OPTIONS.map((e) => [e, e] as [string, string | null])]}
                    selected={filters.events[0] ?? null}
                    onSelect={selectEvent}
                  />
                </HeaderFilter>
                <HeaderFilter label="From" active={!!filters.fromDomain} onClear={() => clearKeys("fromDomain")}>
                  <Labeled label="Sender domain"><input placeholder="e.g. example.com" value={filters.fromDomain} onChange={(e) => set("fromDomain", e.target.value)} style={{ width: "100%" }} /></Labeled>
                </HeaderFilter>
                <HeaderFilter label="To" active={!!filters.toDomain} onClear={() => clearKeys("toDomain")}>
                  <Labeled label="Recipient domain"><input placeholder="e.g. acme.com" value={filters.toDomain} onChange={(e) => set("toDomain", e.target.value)} style={{ width: "100%" }} /></Labeled>
                </HeaderFilter>
                <HeaderFilter label="Subject" active={!!(filters.subject || filters.tag)} onClear={() => clearKeys("subject", "tag")}>
                  <Labeled label="Subject contains"><input placeholder="search subject…" value={filters.subject} onChange={(e) => set("subject", e.target.value)} style={{ width: "100%" }} /></Labeled>
                  <Labeled label="Tag"><input placeholder="exact tag" value={filters.tag} onChange={(e) => set("tag", e.target.value)} style={{ width: "100%" }} /></Labeled>
                </HeaderFilter>
                <HeaderFilter label="Relay" active={!!(filters.relay || filters.rule)} onClear={() => clearKeys("relay", "rule")}>
                  <Labeled label="Relay">
                    <select value={filters.relay} onChange={(e) => set("relay", e.target.value)} style={{ width: "100%" }}>
                      <option value="">Any relay</option>
                      {relays.map((r) => <option key={r.id} value={r.name}>{r.name}</option>)}
                    </select>
                  </Labeled>
                  <Labeled label="Routing rule">
                    <select value={filters.rule} onChange={(e) => set("rule", e.target.value)} style={{ width: "100%" }}>
                      <option value="">Any rule</option>
                      {rules.map((r) => <option key={r.id} value={r.name}>{r.name}</option>)}
                    </select>
                  </Labeled>
                </HeaderFilter>
                <th>Provider</th>
                <HeaderFilter label="Source" active={!!(filters.source || filters.apiKeyId)} onClear={() => clearKeys("source", "apiKeyId")}>
                  <Labeled label="Ingest source">
                    <select value={filters.source} onChange={(e) => set("source", e.target.value)} style={{ width: "100%" }}>
                      <option value="">All sources</option>
                      <option value="SMTP">SMTP</option>
                      <option value="API">API</option>
                      <option value="Dashboard test">Dashboard test</option>
                    </select>
                  </Labeled>
                  {filters.source === "API" && (
                    <Labeled label="API key">
                      <select value={filters.apiKeyId} onChange={(e) => set("apiKeyId", e.target.value)} style={{ width: "100%" }}>
                        <option value="">Any key</option>
                        {keys.map((k) => <option key={k.id} value={String(k.id)}>{k.name}</option>)}
                      </select>
                    </Labeled>
                  )}
                </HeaderFilter>
                <th>Duration</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((r) => (
                <tr
                  key={r.id}
                  onClick={() => openDetail(r.id)}
                  style={{ cursor: "pointer", background: selected === r.id ? "var(--border)" : undefined }}
                >
                  <td style={{ whiteSpace: "nowrap" }} title={new Date(r.loggedAt).toLocaleString()}>{fmtTime(r.loggedAt)}</td>
                  <td><span className={badgeClass(r.status)}>{r.event}</span></td>
                  <td style={cap(200)} title={r.fromAddress}>{r.fromAddress}</td>
                  <td style={cap(190)}><Recipients to={r.toAddresses} domain={r.toDomain} /></td>
                  <td style={cap(200)} title={r.subject ?? undefined}>{r.subject ?? <span className="muted">(none)</span>}</td>
                  <td style={cap(140)} title={r.relayName ?? undefined}>{r.relayName ?? "—"}</td>
                  <td>{r.provider ?? "—"}</td>
                  <td>{r.ingestSource}</td>
                  <td>{r.durationMs != null ? `${r.durationMs} ms` : "—"}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {rows.length === 0 && !loading && <div className="center">No messages match these filters.</div>}
          {loading && <div className="center">Loading…</div>}
        </div>

        {!done && rows.length > 0 && (
          <button onClick={loadMore} disabled={loading}>Load more</button>
        )}
      </div>

      {selected !== null && (
        <Modal title="Message detail" onClose={() => { setSelected(null); setDetail(null); }}>
          {detailLoading && <div className="center">Loading…</div>}
          {!detailLoading && !detail && <div className="center">Not found.</div>}
          {detail && (
            <div style={{ display: "grid", gap: 18 }}>
              {/* Summary: the at-a-glance header (status, subject, from → to) + actions. */}
              <div>
                <div style={{ display: "flex", alignItems: "center", gap: 8, flexWrap: "wrap" }}>
                  <span className={badgeClass(detail.status)}>{detail.event}</span>
                  <span className="muted" style={{ fontSize: 13 }}>{new Date(detail.loggedAt).toLocaleString()}</span>
                </div>
                <div style={{ fontSize: 15, fontWeight: 600, margin: "10px 0 4px", color: "var(--text)" }}>
                  {detail.subject ?? <span className="muted">(no subject)</span>}
                </div>
                <div className="muted" style={{ fontSize: 13, wordBreak: "break-word" }}>
                  {detail.fromAddress} → {detail.toAddresses.length > 0 ? detail.toAddresses.join(", ") : `@${detail.toDomain}`}
                </div>
                {(detail.event === "Failed" || detail.status === "Error") && (
                  <div style={{ display: "flex", gap: 8, marginTop: 12, alignItems: "center", flexWrap: "wrap" }}>
                    <button
                      disabled={retrying}
                      onClick={async () => {
                        setRetrying(true); setRetryMsg(null);
                        try { await api.failed.retry(detail.spoolId); setRetryMsg("Re-queued for delivery."); }
                        catch (e) { setRetryMsg(`Error: ${(e as Error).message}`); }
                        finally { setRetrying(false); }
                      }}
                    >Retry delivery</button>
                    {retryMsg && <span className={retryMsg.startsWith("Error") ? "badge error" : "badge ok"}>{retryMsg}</span>}
                  </div>
                )}
              </div>

              {detail.error && (
                <Section title="Error">
                  <div className="badge error" style={{ whiteSpace: "pre-wrap", display: "block", padding: 10, borderRadius: 6, lineHeight: 1.4 }}>{detail.error}</div>
                </Section>
              )}

              <Section title="Delivery">
                <Row label="Relay">{detail.relayName ?? <Dash />}</Row>
                <Row label="Routing rule">{detail.routingMatched ? (detail.routingRuleName ?? "(rule)") : <span className="muted">Default (no rule)</span>}</Row>
                <Row label="Provider">{detail.provider ?? <Dash />}</Row>
                <Row label="Provider msg ID">{detail.providerMessageId ? <code>{detail.providerMessageId}</code> : <Dash />}</Row>
                <Row label="Provider response">{detail.providerResponse ?? <Dash />}</Row>
                <Row label="Duration">{detail.durationMs != null ? `${detail.durationMs} ms` : <Dash />}</Row>
                <Row label="Attempt">{detail.retryAttempt}</Row>
              </Section>

              <Section title="Origin">
                <Row label="Source">{detail.ingestSource}{detail.sourceIp ? ` (${detail.sourceIp})` : ""}</Row>
                <Row label="API key">{detail.apiKeyName ?? <Dash />}</Row>
                <Row label="Mailer">{detail.xMailer ?? <Dash />}</Row>
                <Row label="Attachments">{detail.attachmentCount}</Row>
                <Row label="Size">{detail.sizeBytes.toLocaleString()} bytes</Row>
                <Row label="Tags">{detail.tags.length > 0 ? detail.tags.map((t) => <span key={t} className="badge" style={{ marginRight: 6 }}>{t}</span>) : <Dash />}</Row>
                <Row label="Spool ID"><code>{detail.spoolId}</code></Row>
              </Section>

              {detail.history.length > 1 && (
                <Section title={`Retry history (${detail.history.length} attempts)`}>
                  <ol style={{ margin: 0, paddingLeft: 16, display: "grid", gap: 6 }}>
                    {detail.history.map((h, i) => (
                      <li key={i} style={{ fontSize: 13 }}>
                        <span className={badgeClass(h.status)} style={{ marginRight: 6 }}>{h.event}</span>
                        <span className="muted">{new Date(h.loggedAt).toLocaleString()}</span>
                        {h.durationMs != null ? <span className="muted"> · {h.durationMs} ms</span> : null}
                        {h.error ? <div className="muted" style={{ whiteSpace: "pre-wrap" }}>{h.error}</div> : null}
                      </li>
                    ))}
                  </ol>
                </Section>
              )}
            </div>
          )}
        </Modal>
      )}
    </>
  );
}

// A magnifying-glass icon; rendered next to a column label to signal the column is filterable.
function SearchIcon({ active }: { active: boolean }) {
  return (
    <svg width="11" height="11" viewBox="0 0 16 16" fill="none" stroke="currentColor"
      strokeWidth={active ? 2.4 : 1.8} style={{ opacity: active ? 1 : 0.5, flexShrink: 0 }}>
      <circle cx="7" cy="7" r="4.3" />
      <line x1="10.3" y1="10.3" x2="14" y2="14" strokeLinecap="round" />
    </svg>
  );
}

// A column header that doubles as a filter trigger: clicking it opens a small popover with the relevant
// control(s) plus a Clear action. The popover is position:fixed (anchored to the header) so the table's
// scroll container can't clip it. A magnifying glass marks the column; it turns blue when a filter is set.
function HeaderFilter({ label, active, onClear, children }: {
  label: string; active: boolean; onClear: () => void; children: React.ReactNode;
}) {
  const [open, setOpen] = useState(false);
  const [pos, setPos] = useState<{ left: number; top: number }>({ left: 0, top: 0 });
  const btnRef = useRef<HTMLButtonElement>(null);
  const toggle = () => {
    if (!open && btnRef.current) {
      const r = btnRef.current.getBoundingClientRect();
      setPos({ left: r.left, top: r.bottom + 2 });
    }
    setOpen((o) => !o);
  };
  return (
    <th style={{ padding: 0 }}>
      <button
        ref={btnRef}
        type="button"
        onClick={toggle}
        style={{
          display: "flex", alignItems: "center", gap: 5, width: "100%",
          background: "transparent", border: "none", borderRadius: 0, padding: "9px 10px",
          color: active ? "var(--blue)" : "var(--muted)", cursor: "pointer",
          textTransform: "uppercase", fontSize: 11, fontWeight: 500, letterSpacing: ".03em",
        }}
      >
        {label}<SearchIcon active={active} />
      </button>
      {open && (
        <>
          <div onClick={() => setOpen(false)} style={{ position: "fixed", inset: 0, zIndex: 40 }} />
          <div style={{
            position: "fixed", left: pos.left, top: pos.top, zIndex: 41,
            background: "var(--panel)", border: "1px solid var(--border)", borderRadius: 8, padding: 10,
            minWidth: 220, boxShadow: "0 8px 24px rgba(0,0,0,.45)", textTransform: "none",
          }}>
            {children}
            <div style={{ display: "flex", justifyContent: "flex-end", marginTop: 10, paddingTop: 8, borderTop: "1px solid var(--border)" }}>
              <button type="button" disabled={!active} onClick={() => { onClear(); setOpen(false); }} style={{ fontSize: 12 }}>Clear</button>
            </div>
          </div>
        </>
      )}
    </th>
  );
}

// Single-select option list used inside a HeaderFilter popover (e.g. Status).
function FilterChoices({ options, selected, onSelect }: {
  options: [string, string | null][]; selected: string | null; onSelect: (v: string | null) => void;
}) {
  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 2 }}>
      {options.map(([label, val]) => {
        const on = (val ?? null) === (selected ?? null);
        return (
          <button
            key={label}
            type="button"
            className="menu-item"
            onClick={() => onSelect(val)}
            style={{ textAlign: "left", border: "none", borderRadius: 6, padding: "6px 8px", fontSize: 13, color: on ? "var(--text)" : "var(--muted)", fontWeight: on ? 600 : 400 }}
          >
            {on ? "✓ " : "  "}{label}
          </button>
        );
      })}
    </div>
  );
}

// Shows the first full recipient (truncated, with the complete list on hover) plus a "+N" chip when there
// are more, so the To column stays bounded no matter how many recipients a message has.
function Recipients({ to, domain }: { to: string[]; domain: string }) {
  if (!to || to.length === 0) return <span className="muted">@{domain}</span>;
  const [first, ...rest] = to;
  return (
    <span title={to.join(", ")} style={{ display: "inline-flex", alignItems: "center", gap: 6, maxWidth: 240 }}>
      <span style={{ overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>{first}</span>
      {rest.length > 0 && <span className="badge" style={{ flexShrink: 0 }}>+{rest.length}</span>}
    </span>
  );
}

const Dash = () => <span className="muted">—</span>;

function Labeled({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label style={{ display: "block" }}>
      <div className="muted" style={{ fontSize: 12, marginBottom: 4 }}>{label}</div>
      {children}
    </label>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div>
      <div className="muted" style={{ fontSize: 11, textTransform: "uppercase", letterSpacing: ".05em", paddingBottom: 4, marginBottom: 8, borderBottom: "1px solid var(--border)" }}>{title}</div>
      <div style={{ display: "grid", gap: 8 }}>{children}</div>
    </div>
  );
}

function Row({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div style={{ display: "grid", gridTemplateColumns: "130px 1fr", gap: 12, alignItems: "baseline", fontSize: 13 }}>
      <div className="muted">{label}</div>
      <div style={{ wordBreak: "break-word" }}>{children}</div>
    </div>
  );
}
