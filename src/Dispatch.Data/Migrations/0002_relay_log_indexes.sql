-- Spec §6.11: backing indexes for the Message Log "filter by relay" and "filter by routing rule"
-- queries. Without these, those filters scan relay_log. Guarded so the migration is re-runnable.

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_relay_log_relay' AND object_id = OBJECT_ID('relay_log'))
    CREATE INDEX IX_relay_log_relay ON relay_log (relay_id, logged_at DESC)
        INCLUDE (status, event, spool_id, from_address, to_domain, subject, provider, duration_ms);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_relay_log_rule' AND object_id = OBJECT_ID('relay_log'))
    CREATE INDEX IX_relay_log_rule ON relay_log (routing_rule_id, logged_at DESC)
        INCLUDE (status, event, spool_id, from_address, to_domain, subject, provider, duration_ms);
