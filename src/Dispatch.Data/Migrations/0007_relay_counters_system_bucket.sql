-- relay_counters.relay_id was NOT NULL with a foreign key to relays(id). Connection-level events that
-- aren't tied to a relay — denials (counted under relay_id = 0) — could therefore never be inserted, so
-- they were silently dropped from the counters, and thus from /stats and the Reports page, even though the
-- relay_log row (Message Log) recorded them. Drop the FK so relay_id = 0 is a valid "no specific relay"
-- bucket. Aggregate summaries SUM across all rows (including 0); per-relay views join/filter to relay_id > 0,
-- so the denied bucket never appears as a phantom relay.
DECLARE @fk sysname;
SELECT @fk = fk.name
FROM sys.foreign_keys fk
WHERE fk.parent_object_id = OBJECT_ID('dbo.relay_counters')
  AND fk.referenced_object_id = OBJECT_ID('dbo.relays');
IF @fk IS NOT NULL
BEGIN
    -- EXEC() can't contain function calls inline, so build the statement into a variable first.
    DECLARE @sql nvarchar(max) = N'ALTER TABLE dbo.relay_counters DROP CONSTRAINT ' + QUOTENAME(@fk);
    EXEC sp_executesql @sql;
END
