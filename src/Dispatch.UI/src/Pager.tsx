// Shared page navigator: "Showing X–Y of N", page-size selector (50/100/200), and prev/next.
export function Pager({ page, pageSize, total, loading, onPage, onPageSize }: {
  page: number; pageSize: number; total: number; loading?: boolean;
  onPage: (p: number) => void; onPageSize: (n: number) => void;
}) {
  const lastPage = Math.max(1, Math.ceil(total / pageSize));
  const from = total === 0 ? 0 : (page - 1) * pageSize + 1;
  const to = Math.min(page * pageSize, total);
  return (
    <div style={{ display: "flex", alignItems: "center", gap: 12, marginTop: 12, flexWrap: "wrap" }}>
      <span className="muted" style={{ fontSize: 13 }}>
        {total === 0 ? "No results" : `Showing ${from.toLocaleString()}–${to.toLocaleString()} of ${total.toLocaleString()}`}
      </span>
      <span style={{ flex: 1 }} />
      <label className="muted" style={{ fontSize: 12, display: "flex", alignItems: "center", gap: 6 }}>
        Per page
        <select value={pageSize} onChange={(e) => onPageSize(Number(e.target.value))}>
          <option value={50}>50</option>
          <option value={100}>100</option>
          <option value={200}>200</option>
        </select>
      </label>
      <button type="button" disabled={loading || page <= 1} onClick={() => onPage(page - 1)}>← Prev</button>
      <span className="muted" style={{ fontSize: 12, minWidth: 90, textAlign: "center" }}>Page {page} of {lastPage}</span>
      <button type="button" disabled={loading || page >= lastPage} onClick={() => onPage(page + 1)}>Next →</button>
    </div>
  );
}
