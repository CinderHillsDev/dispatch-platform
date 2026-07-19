-- Spec §6.11: backing indexes for the Message Log "filter by relay" and "filter by routing rule"
-- queries. Without these, those filters scan relay_log. Guarded so the migration is re-runnable.
--
-- The Postgres original attaches INCLUDE (...) payloads to make these covering. SQLite has no INCLUDE,
-- so only the key columns carry over: still an index seek on (relay_id, logged_at) / (routing_rule_id,
-- logged_at), with one row lookup per match rather than answering from the index alone.

CREATE INDEX IF NOT EXISTS IX_relay_log_relay ON relay_log (relay_id, logged_at DESC);

CREATE INDEX IF NOT EXISTS IX_relay_log_rule ON relay_log (routing_rule_id, logged_at DESC);
