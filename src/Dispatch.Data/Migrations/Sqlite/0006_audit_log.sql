-- Audit/security event log surfaced on the System Logs page (spec §13/§17). Records who did what
-- (auth, API-key changes, config changes) and unhandled server errors. `kind` is the coarse filter the
-- UI exposes ('audit' vs 'error'); `category`/`severity` add detail. Guarded so the migration re-runs.

CREATE TABLE IF NOT EXISTS audit_log (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    logged_at  TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    kind       TEXT    NOT NULL,                 -- audit | error
    category   TEXT    NOT NULL,                 -- Auth | ApiKey | Config | Error
    event      TEXT    NOT NULL,
    severity   TEXT    NOT NULL DEFAULT 'Info',  -- Info | Notice | Warning | Error
    actor      TEXT    NULL,
    source_ip  TEXT    NULL,
    detail     TEXT    NULL
);

-- Default listing order (newest first) + keyset tie-break.
CREATE INDEX IF NOT EXISTS IX_audit_log_at ON audit_log (logged_at DESC, id DESC);
-- The 'audit' vs 'error' filter.
CREATE INDEX IF NOT EXISTS IX_audit_log_kind ON audit_log (kind, logged_at DESC);
