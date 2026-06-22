-- Surface two more message attributes in the Message Log detail: the originating client (X-Mailer
-- header) and how many attachments the message carried. Captured at ingest and stored per relay_log row.
-- Guarded so the migration is re-runnable.

IF COL_LENGTH('relay_log', 'x_mailer') IS NULL
    ALTER TABLE relay_log ADD x_mailer NVARCHAR(256) NULL;

IF COL_LENGTH('relay_log', 'attachment_count') IS NULL
    ALTER TABLE relay_log ADD attachment_count INT NOT NULL DEFAULT 0;
