-- Audit/security event log surfaced on the System Logs page (spec §13/§17). Records who did what
-- (auth, API-key changes, config changes) and unhandled server errors. `kind` is the coarse filter the
-- UI exposes ('audit' vs 'error'); `category`/`severity` add detail. Guarded so the migration re-runs.

IF OBJECT_ID('audit_log', 'U') IS NULL
BEGIN
    CREATE TABLE audit_log (
        id         BIGINT IDENTITY PRIMARY KEY,
        logged_at  DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
        kind       NVARCHAR(16)  NOT NULL,                 -- audit | error
        category   NVARCHAR(32)  NOT NULL,                 -- Auth | ApiKey | Config | Error
        event      NVARCHAR(128) NOT NULL,
        severity   NVARCHAR(16)  NOT NULL DEFAULT 'Info',  -- Info | Notice | Warning | Error
        actor      NVARCHAR(128) NULL,
        source_ip  NVARCHAR(64)  NULL,
        detail     NVARCHAR(MAX) NULL
    );

    -- Default listing order (newest first) + keyset tie-break.
    CREATE INDEX IX_audit_log_at ON audit_log (logged_at DESC, id DESC);
    -- The 'audit' vs 'error' filter.
    CREATE INDEX IX_audit_log_kind ON audit_log (kind, logged_at DESC);
END
