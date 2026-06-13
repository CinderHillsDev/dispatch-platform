-- Dispatch schema v1 (spec §6.11, §7.6, §12.3). Runs as a single batch (no GO separators);
-- tables are created in FK-dependency order. Guarded by schema_version, so it runs once.

CREATE TABLE config (
    [key]      NVARCHAR(128)  NOT NULL PRIMARY KEY,
    value      NVARCHAR(MAX)  NOT NULL,
    encrypted  BIT            NOT NULL DEFAULT 0,
    updated_at DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE relays (
    id                INT IDENTITY  PRIMARY KEY,
    name              NVARCHAR(128) NOT NULL UNIQUE,
    provider          NVARCHAR(64)  NOT NULL,
    is_default        BIT           NOT NULL DEFAULT 0,
    enabled           BIT           NOT NULL DEFAULT 1,
    max_concurrency   INT           NOT NULL DEFAULT 4,
    max_message_bytes INT           NOT NULL DEFAULT 0,
    created_at        DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at        DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE UNIQUE INDEX IX_relays_default ON relays (is_default) WHERE is_default = 1;

CREATE TABLE routing_rules (
    id                INT IDENTITY  PRIMARY KEY,
    priority          INT           NOT NULL UNIQUE,
    name              NVARCHAR(128) NOT NULL,
    recipient_pattern NVARCHAR(256) NULL,
    sender_pattern    NVARCHAR(256) NULL,
    relay_id          INT           NOT NULL REFERENCES relays(id),
    enabled           BIT           NOT NULL DEFAULT 1,
    created_at        DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE api_keys (
    id                    INT IDENTITY  PRIMARY KEY,
    key_id                NVARCHAR(32)  NOT NULL UNIQUE,
    key_hash              NVARCHAR(512) NOT NULL,
    name                  NVARCHAR(256) NOT NULL,
    created_at            DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    last_used_at          DATETIME2     NULL,
    message_count         BIGINT        NOT NULL DEFAULT 0,
    revoked               BIT           NOT NULL DEFAULT 0,
    revoked_at            DATETIME2     NULL,
    rate_limit_per_minute INT           NOT NULL DEFAULT 100,
    scope                 NVARCHAR(64)  NOT NULL DEFAULT 'send'
);
CREATE INDEX IX_api_keys_key_id ON api_keys (key_id) WHERE revoked = 0;

CREATE TABLE relay_counters (
    id        INT IDENTITY PRIMARY KEY,
    date      DATE         NOT NULL,
    relay_id  INT          NOT NULL REFERENCES relays(id),
    received  BIGINT       NOT NULL DEFAULT 0,
    delivered BIGINT       NOT NULL DEFAULT 0,
    failed    BIGINT       NOT NULL DEFAULT 0,
    retried   BIGINT       NOT NULL DEFAULT 0,
    denied    BIGINT       NOT NULL DEFAULT 0,
    CONSTRAINT UQ_relay_counters UNIQUE (date, relay_id)
);
CREATE INDEX IX_relay_counters_date ON relay_counters (date DESC);

CREATE TABLE relay_log (
    id                  BIGINT IDENTITY PRIMARY KEY,
    logged_at           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    spool_id            NVARCHAR(64)    NOT NULL,
    event               NVARCHAR(32)    NOT NULL,
    status              NVARCHAR(16)    NOT NULL,
    retry_attempt       INT             NOT NULL DEFAULT 0,
    from_address        NVARCHAR(512)   NOT NULL,
    from_domain         NVARCHAR(255)   NOT NULL,
    to_addresses        NVARCHAR(MAX)   NOT NULL,
    to_domain           NVARCHAR(255)   NOT NULL,
    subject             NVARCHAR(998)   NOT NULL,
    size_bytes          INT             NOT NULL DEFAULT 0,
    relay_id            INT             NULL REFERENCES relays(id),
    relay_name          NVARCHAR(128)   NULL,
    routing_rule_id     INT             NULL REFERENCES routing_rules(id),
    routing_rule_name   NVARCHAR(128)   NULL,
    routing_matched     BIT             NOT NULL DEFAULT 0,
    provider            NVARCHAR(64)    NULL,
    provider_message_id NVARCHAR(256)   NULL,
    provider_response   NVARCHAR(MAX)   NULL,
    duration_ms         INT             NULL,
    error               NVARCHAR(MAX)   NULL,
    ingest_source       NVARCHAR(16)    NOT NULL DEFAULT 'SMTP',
    source_ip           NVARCHAR(64)    NULL,
    api_key_id          INT             NULL REFERENCES api_keys(id),
    api_key_name        NVARCHAR(256)   NULL,
    tags                NVARCHAR(MAX)   NULL
);
CREATE INDEX IX_relay_log_status_date ON relay_log (status, logged_at DESC)
    INCLUDE (spool_id, from_address, from_domain, to_domain, subject, size_bytes,
             relay_name, routing_rule_name, provider, duration_ms, ingest_source, retry_attempt);
CREATE INDEX IX_relay_log_from_domain ON relay_log (from_domain, logged_at DESC);
CREATE INDEX IX_relay_log_to_domain   ON relay_log (to_domain, logged_at DESC);
CREATE INDEX IX_relay_log_source      ON relay_log (ingest_source, logged_at DESC);
CREATE INDEX IX_relay_log_purge       ON relay_log (logged_at);

CREATE TABLE config_smtp_credentials (
    id            INT IDENTITY  PRIMARY KEY,
    username      NVARCHAR(256) NOT NULL UNIQUE,
    password_hash NVARCHAR(512) NOT NULL,
    created_at    DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    last_used_at  DATETIME2     NULL
);

-- Seed a single default relay in the "Unconfigured" state: until an administrator picks a provider,
-- Dispatch refuses to relay (mail is never silently delivered or discarded).
INSERT INTO relays (name, provider, is_default, enabled, max_concurrency)
VALUES (N'default', N'Unconfigured', 1, 1, 4);
