import { useRef, useState, type ReactNode } from "react";

// A magnifying-glass icon; rendered next to a column label to signal the column is filterable.
export function SearchIcon({ active }: { active: boolean }) {
  return (
    <svg width="11" height="11" viewBox="0 0 16 16" fill="none" stroke="currentColor"
      strokeWidth={active ? 2.4 : 1.8} style={{ opacity: active ? 1 : 0.5, flexShrink: 0 }}>
      <circle cx="7" cy="7" r="4.3" />
      <line x1="10.3" y1="10.3" x2="14" y2="14" strokeLinecap="round" />
    </svg>
  );
}

// A column header that doubles as a filter trigger: clicking it opens a small popover with the relevant
// control(s) plus a Clear action. The popover is position:fixed (anchored to the header) so a table's
// scroll container can't clip it. The magnifying glass turns blue when a filter is set.
export function HeaderFilter({ label, active, onClear, children }: {
  label: string; active: boolean; onClear: () => void; children: ReactNode;
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

// Single-select option list used inside a HeaderFilter popover (e.g. Status / Severity).
export function FilterChoices({ options, selected, onSelect }: {
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
            {on ? "✓ " : "  "}{label}
          </button>
        );
      })}
    </div>
  );
}
