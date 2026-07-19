-- relay_counters.relay_id was NOT NULL with a foreign key to relays(id). Connection-level events that
-- aren't tied to a relay - denials (counted under relay_id = 0) - could therefore never be inserted, so
-- they were silently dropped from the counters, and thus from /stats and the Reports page, even though the
-- relay_log row (Message Log) recorded them. Drop the FK so relay_id = 0 is a valid "no specific relay"
-- bucket. Aggregate summaries SUM across all rows (including 0); per-relay views join/filter to relay_id > 0,
-- so the denied bucket never appears as a phantom relay.
--
-- Postgres does this with ALTER TABLE ... DROP CONSTRAINT. SQLite has no DROP CONSTRAINT at all, so the
-- only way to remove a foreign key is the documented 12-step table rebuild: create the replacement, copy
-- the rows, drop the original, rename. Two things make this safe to run inside the migration transaction:
--   * No other table has a foreign key pointing AT relay_counters, so dropping it orphans nothing and we
--     don't need PRAGMA foreign_keys=OFF (which is a no-op inside a transaction anyway).
--   * The indexes are dropped with the table and recreated below; SQLite does not carry them across a
--     rename automatically.

CREATE TABLE relay_counters_new (
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    date      TEXT    NOT NULL,
    relay_id  INTEGER NOT NULL,          -- no REFERENCES: 0 is the valid "no specific relay" bucket
    received  INTEGER NOT NULL DEFAULT 0,
    delivered INTEGER NOT NULL DEFAULT 0,
    failed    INTEGER NOT NULL DEFAULT 0,
    retried   INTEGER NOT NULL DEFAULT 0,
    denied    INTEGER NOT NULL DEFAULT 0,
    CONSTRAINT UQ_relay_counters UNIQUE (date, relay_id)
);

INSERT INTO relay_counters_new (id, date, relay_id, received, delivered, failed, retried, denied)
SELECT id, date, relay_id, received, delivered, failed, retried, denied FROM relay_counters;

DROP TABLE relay_counters;

ALTER TABLE relay_counters_new RENAME TO relay_counters;

CREATE INDEX IX_relay_counters_date ON relay_counters (date DESC);
