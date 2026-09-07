CREATE TABLE dbo.journal_metadata (
    metadata_key tinyint NOT NULL CONSTRAINT pk_journal_metadata PRIMARY KEY,
    schema_version int NOT NULL,
    store_epoch uniqueidentifier NOT NULL,
    updated_at_utc datetimeoffset(7) NOT NULL,
    CONSTRAINT ck_journal_metadata_key CHECK (metadata_key = 1),
    CONSTRAINT ck_journal_metadata_version CHECK (schema_version >= 1));

CREATE TABLE dbo.journal_stream (
    stream_key nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    current_version bigint NOT NULL,
    retained_floor bigint NOT NULL CONSTRAINT df_journal_stream_floor DEFAULT 0,
    updated_at_utc datetimeoffset(7) NOT NULL,
    CONSTRAINT pk_journal_stream PRIMARY KEY (stream_key),
    CONSTRAINT ck_journal_stream_version CHECK (current_version >= 0),
    CONSTRAINT ck_journal_stream_floor CHECK (retained_floor >= 0 AND retained_floor <= current_version));

CREATE TABLE dbo.journal_event (
    stream_key nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    stream_version bigint NOT NULL,
    operation_id uniqueidentifier NOT NULL,
    operation_ordinal int NOT NULL,
    event_type nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    event_schema_version int NOT NULL,
    payload varbinary(max) NOT NULL,
    payload_sha256 binary(32) NOT NULL,
    committed_at_utc datetimeoffset(7) NOT NULL,
    CONSTRAINT pk_journal_event PRIMARY KEY (stream_key, stream_version),
    CONSTRAINT fk_journal_event_stream FOREIGN KEY (stream_key) REFERENCES dbo.journal_stream(stream_key),
    CONSTRAINT ck_journal_event_version CHECK (stream_version > 0),
    CONSTRAINT ck_journal_event_ordinal CHECK (operation_ordinal >= 0),
    CONSTRAINT ck_journal_event_schema_version CHECK (event_schema_version > 0),
    CONSTRAINT ck_journal_event_payload CHECK (DATALENGTH(payload) <= 262144));
CREATE INDEX ix_journal_event_operation ON dbo.journal_event(operation_id);

CREATE TABLE dbo.journal_operation (
    operation_id uniqueidentifier NOT NULL CONSTRAINT pk_journal_operation PRIMARY KEY,
    operation_kind nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    intent_fingerprint_format int NOT NULL,
    intent_fingerprint binary(32) NOT NULL,
    execution_fingerprint_format int NOT NULL,
    execution_fingerprint binary(32) NOT NULL,
    result_schema nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    result_schema_version int NOT NULL,
    result_data varbinary(max) NOT NULL,
    result_sha256 binary(32) NOT NULL,
    committed_at_utc datetimeoffset(7) NOT NULL,
    retention_started_at_utc datetimeoffset(7) NOT NULL
        CONSTRAINT df_journal_operation_retention DEFAULT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'),
    CONSTRAINT ck_journal_operation_intent_format CHECK (intent_fingerprint_format > 0),
    CONSTRAINT ck_journal_operation_execution_format CHECK (execution_fingerprint_format > 0),
    CONSTRAINT ck_journal_operation_result_schema_version CHECK (result_schema_version > 0),
    CONSTRAINT ck_journal_operation_result CHECK (DATALENGTH(result_data) <= 65536));
CREATE INDEX ix_journal_operation_commit ON dbo.journal_operation(committed_at_utc, operation_id);
CREATE INDEX ix_journal_operation_retention ON dbo.journal_operation(retention_started_at_utc, operation_id);
EXEC(N'CREATE TRIGGER dbo.trg_journal_operation_delete_guard
ON dbo.journal_operation
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF OBJECT_ID(N''tempdb..#khaoz_journal_operation_delete_guard'', N''U'') IS NULL
        THROW 51000, ''journal operation delete requires guarded maintenance'', 1;
END');

CREATE TABLE dbo.journal_operation_stream (
    operation_id uniqueidentifier NOT NULL,
    stream_key nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    before_version bigint NOT NULL,
    after_version bigint NOT NULL,
    event_count int NOT NULL,
    CONSTRAINT pk_journal_operation_stream PRIMARY KEY (operation_id, stream_key),
    CONSTRAINT fk_journal_operation_stream_operation FOREIGN KEY (operation_id) REFERENCES dbo.journal_operation(operation_id),
    CONSTRAINT fk_journal_operation_stream_stream FOREIGN KEY (stream_key) REFERENCES dbo.journal_stream(stream_key),
    CONSTRAINT ck_journal_operation_stream_versions CHECK (before_version >= 0 AND after_version >= before_version),
    CONSTRAINT ck_journal_operation_stream_count CHECK (event_count >= 0 AND after_version - before_version = event_count));

CREATE TABLE dbo.journal_snapshot (
    stream_key nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    through_version bigint NOT NULL,
    snapshot_schema nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    snapshot_schema_version int NOT NULL,
    data varbinary(max) NOT NULL,
    data_sha256 binary(32) NOT NULL,
    created_at_utc datetimeoffset(7) NOT NULL,
    CONSTRAINT pk_journal_snapshot PRIMARY KEY (stream_key),
    CONSTRAINT fk_journal_snapshot_stream FOREIGN KEY (stream_key) REFERENCES dbo.journal_stream(stream_key),
    CONSTRAINT ck_journal_snapshot_version CHECK (through_version >= 0),
    CONSTRAINT ck_journal_snapshot_schema_version CHECK (snapshot_schema_version > 0),
    CONSTRAINT ck_journal_snapshot_data CHECK (DATALENGTH(data) <= 8388608));

CREATE TABLE dbo.journal_projection (
    stream_key nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    section_name nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    source_version bigint NOT NULL,
    projection_schema nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    projection_schema_version int NOT NULL,
    data varbinary(max) NOT NULL,
    data_sha256 binary(32) NOT NULL,
    updated_at_utc datetimeoffset(7) NOT NULL,
    CONSTRAINT pk_journal_projection PRIMARY KEY (stream_key, section_name),
    CONSTRAINT fk_journal_projection_stream FOREIGN KEY (stream_key) REFERENCES dbo.journal_stream(stream_key),
    CONSTRAINT ck_journal_projection_version CHECK (source_version >= 0),
    CONSTRAINT ck_journal_projection_schema_version CHECK (projection_schema_version > 0),
    CONSTRAINT ck_journal_projection_data CHECK (DATALENGTH(data) <= 2097152));
CREATE INDEX ix_journal_projection_version ON dbo.journal_projection(stream_key, source_version);

INSERT INTO dbo.journal_metadata(metadata_key, schema_version, store_epoch, updated_at_utc)
VALUES (1, 2, NEWID(), SYSUTCDATETIME());
