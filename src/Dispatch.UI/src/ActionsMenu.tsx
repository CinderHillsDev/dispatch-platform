import { useEffect, useRef, useState } from "react";

export interface MenuAction { label: string; onClick: () => void; danger?: boolean; disabled?: boolean }

/// A "⋯" button that opens a small dropdown of row actions. Closes on outside-click or Esc.
export function ActionsMenu({ items }: { items: MenuAction[] }) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const onDoc = (e: MouseEvent) => { if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false); };
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") setOpen(false); };
    document.addEventListener("mousedown", onDoc);
    document.addEventListener("keydown", onKey);
    return () => { document.removeEventListener("mousedown", onDoc); document.removeEventListener("keydown", onKey); };
  }, [open]);

  return (
    <div ref={ref} style={{ position: "relative", display: "inline-block" }}>
      <button
        aria-label="Actions"
        onClick={(e) => { e.stopPropagation(); setOpen((o) => !o); }}
        style={{ padding: "2px 10px", fontWeight: 700, lineHeight: 1 }}
      >⋯</button>
      {open && (
        <div style={{
          position: "absolute", right: 0, top: "100%", marginTop: 4, zIndex: 50, minWidth: 170,
          background: "var(--panel-2)", border: "1px solid var(--border)", borderRadius: 8,
          boxShadow: "0 8px 24px rgba(0,0,0,.45)", overflow: "hidden",
        }}>
          {items.map((it, i) => (
            <button
              key={i}
              disabled={it.disabled}
              onClick={(e) => { e.stopPropagation(); setOpen(false); it.onClick(); }}
              style={{
                display: "block", width: "100%", textAlign: "left", border: "none", borderRadius: 0,
                background: "transparent", padding: "9px 12px", fontSize: 13,
                color: it.disabled ? "var(--muted)" : it.danger ? "var(--red)" : "var(--text)",
                cursor: it.disabled ? "default" : "pointer",
              }}
            >{it.label}</button>
          ))}
        </div>
      )}
    </div>
  );
}
