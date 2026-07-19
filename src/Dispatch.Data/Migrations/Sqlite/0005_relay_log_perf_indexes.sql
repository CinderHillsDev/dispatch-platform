-- Performance tuning for relay_log (spec §19). Two access paths were unindexed and scanned the table:
--
--  1. Lookup by spool_id - used by the message-detail attempt history (GetByIdAsync), the by-spool
--     lookup (GetBySpoolIdAsync, also the Local Inbox "Show delivery log"), and the public by-spool
--     status endpoint. Both ascending (history) and descending (latest row) orderings are needed, so the
--     composite (spool_id, logged_at, id) serves either direction with a seek instead of a scan.
--
--  2. Per-API-key message list (RecentByApiKeyAsync / GET /api/v1/messages) filters api_key_id and orders
--     by logged_at DESC. A partial index (api_key_id IS NOT NULL) keeps it tiny - the vast majority of
--     rows are SMTP ingest with a NULL api_key_id and are excluded from the index entirely. SQLite
--     supports partial indexes, so this carries over unchanged.
--
-- Other access paths are already covered: the unfiltered/date-range list orders by (logged_at DESC, id
-- DESC) and is served by IX_relay_log_purge (logged_at) via an ordered backward scan; status / source /
-- from_domain / to_domain / relay_id / routing_rule_id each have a dedicated (col, logged_at DESC) index.
-- subject/tag use leading-wildcard LIKE and are inherently non-sargable, so no index is added for them.
-- (On SQLite, FTS5 would be the natural upgrade if that ever becomes a bottleneck.)

CREATE INDEX IF NOT EXISTS IX_relay_log_spool_id ON relay_log (spool_id, logged_at, id);

CREATE INDEX IF NOT EXISTS IX_relay_log_api_key ON relay_log (api_key_id, logged_at DESC)
    WHERE api_key_id IS NOT NULL;
