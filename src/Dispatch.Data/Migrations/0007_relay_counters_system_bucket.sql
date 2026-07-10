-- relay_counters.relay_id was NOT NULL with a foreign key to relays(id). Connection-level events that
-- aren't tied to a relay - denials (counted under relay_id = 0) - could therefore never be inserted, so
-- they were silently dropped from the counters, and thus from /stats and the Reports page, even though the
-- relay_log row (Message Log) recorded them. Drop the FK so relay_id = 0 is a valid "no specific relay"
-- bucket. Aggregate summaries SUM across all rows (including 0); per-relay views join/filter to relay_id > 0,
-- so the denied bucket never appears as a phantom relay.
--
-- The FK created inline in 0001 (relay_id int NOT NULL REFERENCES relays(id)) is auto-named by Postgres as
-- <table>_<column>_fkey, so we drop it by that deterministic name (IF EXISTS makes this re-runnable).
ALTER TABLE relay_counters DROP CONSTRAINT IF EXISTS relay_counters_relay_id_fkey;
