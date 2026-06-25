import { type ReactNode } from "react";
import { type TestResult } from "./lib/api";

// Verbose result of a relay/provider send test — surfaces the provider's own response (incl. the raw
// error body on failure) so operators can troubleshoot. Shared by the add-relay wizard, the row "Send
// test" modal, and the first-run wizard.
export function TestResultView({ result }: { result: TestResult }) {
  return (
    <div style={{ border: "1px solid var(--border)", borderRadius: 8, padding: 12, display: "grid", gap: 6 }}>
      <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
        <span className={result.ok ? "badge ok" : "badge error"}>{result.ok ? "Delivered" : "Failed"}</span>
        {result.provider && <span className="muted" style={{ fontSize: 12 }}>{result.provider}</span>}
      </div>
      {result.providerMessageId && <Row k="Provider message ID"><code>{result.providerMessageId}</code></Row>}
      {result.detail && <Row k="Detail">{result.detail}</Row>}
      {result.error && (
        <div>
          <div className="muted" style={{ fontSize: 12, marginBottom: 4 }}>Response from the provider</div>
          <pre style={{ whiteSpace: "pre-wrap", wordBreak: "break-word", background: "var(--panel-2)", border: "1px solid var(--border)", borderRadius: 6, padding: 10, margin: 0, fontSize: 12, lineHeight: 1.45 }}>{result.error}</pre>
        </div>
      )}
      {!result.ok && !result.error && <p className="muted" style={{ fontSize: 12, margin: 0 }}>No further detail was returned by the provider.</p>}
    </div>
  );
}

function Row({ k, children }: { k: string; children: ReactNode }) {
  return <div style={{ fontSize: 13, wordBreak: "break-word" }}><span className="muted">{k}: </span>{children}</div>;
}
