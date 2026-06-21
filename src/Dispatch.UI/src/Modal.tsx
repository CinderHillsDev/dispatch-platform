import { useEffect, type ReactNode } from "react";

/// Centered overlay dialog. Click the backdrop or press Esc to close. Used for message detail so the
/// list stays full-width instead of being squeezed by a side pane.
export function Modal({ title, onClose, children }: { title: string; onClose: () => void; children: ReactNode }) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  return (
    <div
      onClick={onClose}
      style={{
        position: "fixed", inset: 0, background: "rgba(0,0,0,.6)", zIndex: 1000,
        display: "flex", alignItems: "flex-start", justifyContent: "center", padding: "6vh 20px", overflowY: "auto",
      }}
    >
      <div onClick={(e) => e.stopPropagation()} className="panel" style={{ width: 640, maxWidth: "100%", margin: 0 }}>
        <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 12, marginBottom: 14 }}>
          <h2 style={{ margin: 0, textTransform: "none", letterSpacing: 0, color: "var(--text)", fontSize: 16 }}>{title}</h2>
          <button onClick={onClose} aria-label="Close">✕</button>
        </div>
        {children}
      </div>
    </div>
  );
}
