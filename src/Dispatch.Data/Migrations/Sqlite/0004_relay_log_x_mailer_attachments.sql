-- Surface two more message attributes in the Message Log detail: the originating client (X-Mailer
-- header) and how many attachments the message carried. Captured at ingest and stored per relay_log row.
--
-- SQLite has no ADD COLUMN IF NOT EXISTS. The guard is not actually needed here: DatabaseInitializer
-- records every applied version in schema_version and skips it thereafter, so each migration runs exactly
-- once. The Postgres original's IF NOT EXISTS is belt-and-braces, not load-bearing.
-- A NOT NULL column added to an existing table must carry a non-null default, which attachment_count does.

ALTER TABLE relay_log ADD COLUMN x_mailer TEXT NULL;

ALTER TABLE relay_log ADD COLUMN attachment_count INTEGER NOT NULL DEFAULT 0;
