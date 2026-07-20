-- Surface two more message attributes in the Message Log detail: the originating client (X-Mailer
-- header) and how many attachments the message carried. Captured at ingest and stored per relay_log row.
-- Guarded so the migration is re-runnable.

ALTER TABLE relay_log ADD COLUMN IF NOT EXISTS x_mailer varchar(256) NULL;

ALTER TABLE relay_log ADD COLUMN IF NOT EXISTS attachment_count int NOT NULL DEFAULT 0;
