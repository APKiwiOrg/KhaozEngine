CREATE TABLE dbo.catalog_metadata (
    metadata_key int NOT NULL,
    schema_version int NOT NULL,
    store_epoch nvarchar(36) COLLATE Latin1_General_100_BIN2 NOT NULL,
    active_version int NOT NULL CONSTRAINT df_catalog_metadata_active DEFAULT 0,
    pinned_version int NULL,
    updated_at_utc datetimeoffset(7) NOT NULL,
    CONSTRAINT pk_catalog_metadata PRIMARY KEY (metadata_key),
    CONSTRAINT ck_catalog_metadata_key CHECK (metadata_key = 1),
    CONSTRAINT ck_catalog_metadata_version CHECK (schema_version >= 1),
    CONSTRAINT ck_catalog_metadata_epoch CHECK (LEN(store_epoch) IN (32, 36)),
    CONSTRAINT ck_catalog_metadata_active CHECK (active_version >= 0),
    CONSTRAINT ck_catalog_metadata_pinned CHECK (pinned_version IS NULL OR pinned_version >= 1));

CREATE TABLE dbo.catalog_type (
    type_id int NOT NULL,
    type_key nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    chunk_slots int NOT NULL,
    default_visibility int NOT NULL,
    max_definition_id int NULL,
    first_seen_version int NOT NULL,
    CONSTRAINT pk_catalog_type PRIMARY KEY (type_id),
    CONSTRAINT ck_catalog_type_id CHECK (type_id BETWEEN 1 AND 65535),
    CONSTRAINT ck_catalog_type_key CHECK (LEN(type_key) BETWEEN 1 AND 64),
    CONSTRAINT ck_catalog_type_slots CHECK (chunk_slots BETWEEN 256 AND 65536),
    CONSTRAINT ck_catalog_type_visibility CHECK (default_visibility IN (0, 1)),
    CONSTRAINT ck_catalog_type_ceiling CHECK (max_definition_id IS NULL OR max_definition_id >= 1),
    CONSTRAINT ck_catalog_type_first_seen CHECK (first_seen_version >= 0));
CREATE UNIQUE INDEX ux_catalog_type_key ON dbo.catalog_type(type_key);

CREATE TABLE dbo.catalog_version (
    version_number int NOT NULL,
    server_manifest_hash nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    client_manifest_hash nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    minimum_server_build int NOT NULL,
    minimum_client_build int NOT NULL,
    format_generation int NOT NULL,
    base_version int NOT NULL,
    published_by nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    note nvarchar(1024) COLLATE Latin1_General_100_BIN2 NOT NULL,
    published_at_utc datetimeoffset(7) NOT NULL,
    CONSTRAINT pk_catalog_version PRIMARY KEY (version_number),
    CONSTRAINT ck_catalog_version_number CHECK (version_number >= 1),
    CONSTRAINT ck_catalog_version_server_hash CHECK (LEN(server_manifest_hash) = 64),
    CONSTRAINT ck_catalog_version_client_hash CHECK (LEN(client_manifest_hash) = 64),
    CONSTRAINT ck_catalog_version_min_server CHECK (minimum_server_build >= 0),
    CONSTRAINT ck_catalog_version_min_client CHECK (minimum_client_build >= 0),
    CONSTRAINT ck_catalog_version_generation CHECK (format_generation >= 1),
    CONSTRAINT ck_catalog_version_base CHECK (base_version >= 0),
    CONSTRAINT ck_catalog_version_published_by CHECK (LEN(published_by) BETWEEN 1 AND 128),
    CONSTRAINT ck_catalog_version_note CHECK (LEN(note) <= 1024));

CREATE TABLE dbo.catalog_family (
    family_id bigint IDENTITY(1,1) NOT NULL,
    type_id int NOT NULL,
    family_key nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    block_size int NOT NULL,
    retired int NOT NULL CONSTRAINT df_catalog_family_retired DEFAULT 0,
    created_in_version int NOT NULL,
    CONSTRAINT pk_catalog_family PRIMARY KEY (family_id),
    CONSTRAINT fk_catalog_family_type FOREIGN KEY (type_id) REFERENCES dbo.catalog_type(type_id),
    CONSTRAINT ck_catalog_family_key CHECK (LEN(family_key) BETWEEN 1 AND 64),
    CONSTRAINT ck_catalog_family_block_size CHECK (block_size BETWEEN 16 AND 65536),
    CONSTRAINT ck_catalog_family_retired CHECK (retired IN (0, 1)),
    CONSTRAINT ck_catalog_family_created CHECK (created_in_version >= 1));
CREATE UNIQUE INDEX ux_catalog_family_key ON dbo.catalog_family(type_id, family_key);

CREATE TABLE dbo.catalog_family_block (
    family_id bigint NOT NULL,
    block_ordinal int NOT NULL,
    base_id int NOT NULL,
    block_size int NOT NULL,
    next_free_id int NOT NULL,
    reserved_in_version int NOT NULL,
    CONSTRAINT pk_catalog_family_block PRIMARY KEY (family_id, block_ordinal),
    CONSTRAINT fk_catalog_family_block_family FOREIGN KEY (family_id) REFERENCES dbo.catalog_family(family_id),
    CONSTRAINT ck_catalog_family_block_ordinal CHECK (block_ordinal >= 0),
    CONSTRAINT ck_catalog_family_block_base CHECK (base_id >= 1),
    CONSTRAINT ck_catalog_family_block_size CHECK (block_size BETWEEN 16 AND 65536),
    CONSTRAINT ck_catalog_family_block_next CHECK (next_free_id >= base_id),
    CONSTRAINT ck_catalog_family_block_version CHECK (reserved_in_version >= 1),
    CONSTRAINT ck_catalog_family_block_alignment CHECK (base_id % block_size = 0),
    CONSTRAINT ck_catalog_family_block_top CHECK (next_free_id <= base_id + block_size));

CREATE TABLE dbo.catalog_row (
    type_id int NOT NULL,
    definition_id int NOT NULL,
    valid_from_version int NOT NULL,
    replaced_in_version int NULL,
    content_key nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    parent_id int NOT NULL CONSTRAINT df_catalog_row_parent DEFAULT 0,
    family_id bigint NULL,
    retired int NOT NULL CONSTRAINT df_catalog_row_retired DEFAULT 0,
    CONSTRAINT pk_catalog_row PRIMARY KEY (type_id, definition_id, valid_from_version),
    CONSTRAINT fk_catalog_row_type FOREIGN KEY (type_id) REFERENCES dbo.catalog_type(type_id),
    CONSTRAINT fk_catalog_row_version FOREIGN KEY (valid_from_version) REFERENCES dbo.catalog_version(version_number),
    CONSTRAINT fk_catalog_row_family FOREIGN KEY (family_id) REFERENCES dbo.catalog_family(family_id),
    CONSTRAINT ck_catalog_row_definition CHECK (definition_id >= 1),
    CONSTRAINT ck_catalog_row_valid_from CHECK (valid_from_version >= 1),
    CONSTRAINT ck_catalog_row_replaced CHECK (replaced_in_version IS NULL OR replaced_in_version > valid_from_version),
    CONSTRAINT ck_catalog_row_key CHECK (LEN(content_key) BETWEEN 1 AND 64),
    CONSTRAINT ck_catalog_row_parent CHECK (parent_id >= 0),
    CONSTRAINT ck_catalog_row_retired CHECK (retired IN (0, 1)));
CREATE INDEX ix_catalog_row_live ON dbo.catalog_row(type_id, replaced_in_version, definition_id);
CREATE INDEX ix_catalog_row_key ON dbo.catalog_row(type_id, content_key, valid_from_version);
CREATE INDEX ix_catalog_row_changed ON dbo.catalog_row(valid_from_version, type_id, definition_id);

CREATE TABLE dbo.catalog_row_field (
    type_id int NOT NULL,
    definition_id int NOT NULL,
    valid_from_version int NOT NULL,
    field_name nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    field_kind int NOT NULL,
    int_value bigint NULL,
    text_value nvarchar(192) COLLATE Latin1_General_100_BIN2 NULL,
    blob_value varbinary(max) NULL,
    CONSTRAINT pk_catalog_row_field PRIMARY KEY (type_id, definition_id, valid_from_version, field_name),
    CONSTRAINT fk_catalog_row_field_row FOREIGN KEY (type_id, definition_id, valid_from_version)
        REFERENCES dbo.catalog_row(type_id, definition_id, valid_from_version),
    CONSTRAINT ck_catalog_row_field_name CHECK (LEN(field_name) BETWEEN 1 AND 64),
    CONSTRAINT ck_catalog_row_field_kind CHECK (field_kind BETWEEN 0 AND 6),
    CONSTRAINT ck_catalog_row_field_text CHECK (text_value IS NULL OR LEN(text_value) <= 192),
    CONSTRAINT ck_catalog_row_field_blob CHECK (blob_value IS NULL OR DATALENGTH(blob_value) <= 4096));

CREATE TABLE dbo.catalog_id_high_water (
    type_id int NOT NULL,
    reserved_through int NOT NULL,
    issued_through int NOT NULL,
    CONSTRAINT pk_catalog_id_high_water PRIMARY KEY (type_id),
    CONSTRAINT fk_catalog_id_high_water_type FOREIGN KEY (type_id) REFERENCES dbo.catalog_type(type_id),
    CONSTRAINT ck_catalog_id_high_water_reserved CHECK (reserved_through >= 0),
    CONSTRAINT ck_catalog_id_high_water_issued CHECK (issued_through >= 0 AND issued_through <= reserved_through));

CREATE TABLE dbo.catalog_draft (
    draft_key int NOT NULL,
    base_version int NOT NULL,
    opened_by nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    opened_at_utc datetimeoffset(7) NOT NULL,
    note nvarchar(1024) COLLATE Latin1_General_100_BIN2 NOT NULL CONSTRAINT df_catalog_draft_note DEFAULT N'',
    CONSTRAINT pk_catalog_draft PRIMARY KEY (draft_key),
    CONSTRAINT ck_catalog_draft_key CHECK (draft_key = 1),
    CONSTRAINT ck_catalog_draft_base CHECK (base_version >= 0),
    CONSTRAINT ck_catalog_draft_opened_by CHECK (LEN(opened_by) BETWEEN 1 AND 128),
    CONSTRAINT ck_catalog_draft_note CHECK (LEN(note) <= 1024));

CREATE TABLE dbo.catalog_draft_edit (
    edit_ordinal bigint IDENTITY(1,1) NOT NULL,
    type_id int NOT NULL,
    definition_id int NOT NULL CONSTRAINT df_catalog_draft_edit_definition DEFAULT 0,
    content_key nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    operation int NOT NULL,
    retire_policy int NOT NULL CONSTRAINT df_catalog_draft_edit_policy DEFAULT 0,
    replacement_id int NOT NULL CONSTRAINT df_catalog_draft_edit_replacement DEFAULT 0,
    fork_key nvarchar(64) COLLATE Latin1_General_100_BIN2 NULL,
    fork_flag_field nvarchar(64) COLLATE Latin1_General_100_BIN2 NULL,
    family_id bigint NULL,
    imported_retired int NOT NULL CONSTRAINT df_catalog_draft_edit_imported DEFAULT 0,
    edited_by nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    edited_at_utc datetimeoffset(7) NOT NULL,
    CONSTRAINT pk_catalog_draft_edit PRIMARY KEY (edit_ordinal),
    CONSTRAINT fk_catalog_draft_edit_type FOREIGN KEY (type_id) REFERENCES dbo.catalog_type(type_id),
    CONSTRAINT ck_catalog_draft_edit_definition CHECK (definition_id >= 0),
    CONSTRAINT ck_catalog_draft_edit_key CHECK (LEN(content_key) BETWEEN 1 AND 64),
    CONSTRAINT ck_catalog_draft_edit_operation CHECK (operation IN (1, 2, 3, 4)),
    CONSTRAINT ck_catalog_draft_edit_policy CHECK (retire_policy IN (0, 1, 2)),
    CONSTRAINT ck_catalog_draft_edit_replacement CHECK (replacement_id >= 0),
    CONSTRAINT ck_catalog_draft_edit_fork_key CHECK (fork_key IS NULL OR LEN(fork_key) BETWEEN 1 AND 64),
    CONSTRAINT ck_catalog_draft_edit_fork_flag CHECK (fork_flag_field IS NULL OR LEN(fork_flag_field) BETWEEN 1 AND 64),
    CONSTRAINT ck_catalog_draft_edit_imported CHECK (imported_retired IN (0, 1)),
    CONSTRAINT ck_catalog_draft_edit_edited_by CHECK (LEN(edited_by) BETWEEN 1 AND 128));
CREATE UNIQUE INDEX ux_catalog_draft_edit_target ON dbo.catalog_draft_edit(type_id, definition_id, content_key);

CREATE TABLE dbo.catalog_draft_edit_field (
    edit_ordinal bigint NOT NULL,
    field_name nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    field_kind int NOT NULL,
    int_value bigint NULL,
    text_value nvarchar(192) COLLATE Latin1_General_100_BIN2 NULL,
    blob_value varbinary(max) NULL,
    CONSTRAINT pk_catalog_draft_edit_field PRIMARY KEY (edit_ordinal, field_name),
    CONSTRAINT fk_catalog_draft_edit_field_edit FOREIGN KEY (edit_ordinal)
        REFERENCES dbo.catalog_draft_edit(edit_ordinal) ON DELETE CASCADE,
    CONSTRAINT ck_catalog_draft_edit_field_name CHECK (LEN(field_name) BETWEEN 1 AND 64),
    CONSTRAINT ck_catalog_draft_edit_field_kind CHECK (field_kind BETWEEN 0 AND 6),
    CONSTRAINT ck_catalog_draft_edit_field_text CHECK (text_value IS NULL OR LEN(text_value) <= 192),
    CONSTRAINT ck_catalog_draft_edit_field_blob CHECK (blob_value IS NULL OR DATALENGTH(blob_value) <= 4096));

CREATE TABLE dbo.catalog_audit (
    audit_id bigint IDENTITY(1,1) NOT NULL,
    occurred_at_utc datetimeoffset(7) NOT NULL,
    actor nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [operator] nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL CONSTRAINT df_catalog_audit_operator DEFAULT N'',
    action nvarchar(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
    type_id int NOT NULL CONSTRAINT df_catalog_audit_type DEFAULT 0,
    definition_id int NOT NULL CONSTRAINT df_catalog_audit_definition DEFAULT 0,
    content_key nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL CONSTRAINT df_catalog_audit_key DEFAULT N'',
    field_name nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL CONSTRAINT df_catalog_audit_field DEFAULT N'',
    before_value nvarchar(max) COLLATE Latin1_General_100_BIN2 NULL,
    after_value nvarchar(max) COLLATE Latin1_General_100_BIN2 NULL,
    version_number int NOT NULL CONSTRAINT df_catalog_audit_version DEFAULT 0,
    note nvarchar(1024) COLLATE Latin1_General_100_BIN2 NOT NULL CONSTRAINT df_catalog_audit_note DEFAULT N'',
    CONSTRAINT pk_catalog_audit PRIMARY KEY (audit_id),
    CONSTRAINT ck_catalog_audit_actor CHECK (LEN(actor) BETWEEN 1 AND 128),
    CONSTRAINT ck_catalog_audit_operator CHECK (LEN([operator]) <= 128),
    CONSTRAINT ck_catalog_audit_action CHECK (LEN(action) BETWEEN 1 AND 32),
    CONSTRAINT ck_catalog_audit_key CHECK (LEN(content_key) <= 64),
    CONSTRAINT ck_catalog_audit_field CHECK (LEN(field_name) <= 64),
    CONSTRAINT ck_catalog_audit_before CHECK (before_value IS NULL OR LEN(before_value) <= 4096),
    CONSTRAINT ck_catalog_audit_after CHECK (after_value IS NULL OR LEN(after_value) <= 4096),
    CONSTRAINT ck_catalog_audit_note CHECK (LEN(note) <= 1024));
CREATE INDEX ix_catalog_audit_time ON dbo.catalog_audit(occurred_at_utc, audit_id);
CREATE INDEX ix_catalog_audit_target ON dbo.catalog_audit(type_id, definition_id, audit_id);

CREATE TABLE dbo.catalog_remap_rule (
    [sequence] int NOT NULL,
    introduced_in int NOT NULL,
    type_id int NOT NULL,
    kind int NOT NULL,
    from_id int NOT NULL,
    to_id int NOT NULL CONSTRAINT df_catalog_remap_rule_to DEFAULT 0,
    payload varbinary(max) NOT NULL CONSTRAINT df_catalog_remap_rule_payload DEFAULT 0x,
    CONSTRAINT pk_catalog_remap_rule PRIMARY KEY ([sequence]),
    CONSTRAINT fk_catalog_remap_rule_type FOREIGN KEY (type_id) REFERENCES dbo.catalog_type(type_id),
    CONSTRAINT fk_catalog_remap_rule_version FOREIGN KEY (introduced_in) REFERENCES dbo.catalog_version(version_number),
    CONSTRAINT ck_catalog_remap_rule_sequence CHECK ([sequence] >= 1),
    CONSTRAINT ck_catalog_remap_rule_introduced CHECK (introduced_in >= 1),
    CONSTRAINT ck_catalog_remap_rule_kind CHECK (kind BETWEEN 1 AND 255),
    CONSTRAINT ck_catalog_remap_rule_from CHECK (from_id >= 1),
    CONSTRAINT ck_catalog_remap_rule_to CHECK (to_id >= 0),
    CONSTRAINT ck_catalog_remap_rule_payload CHECK (DATALENGTH(payload) <= 64));

CREATE TABLE dbo.catalog_chunk (
    version_number int NOT NULL,
    type_id int NOT NULL,
    chunk_index int NOT NULL,
    chunk_hash nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    row_count int NOT NULL,
    uncompressed_bytes int NOT NULL,
    stored_bytes int NOT NULL,
    visibility int NOT NULL,
    CONSTRAINT pk_catalog_chunk PRIMARY KEY (version_number, type_id, chunk_index, visibility),
    CONSTRAINT fk_catalog_chunk_version FOREIGN KEY (version_number) REFERENCES dbo.catalog_version(version_number),
    CONSTRAINT fk_catalog_chunk_type FOREIGN KEY (type_id) REFERENCES dbo.catalog_type(type_id),
    CONSTRAINT ck_catalog_chunk_index CHECK (chunk_index >= 0),
    CONSTRAINT ck_catalog_chunk_hash CHECK (LEN(chunk_hash) = 64),
    CONSTRAINT ck_catalog_chunk_rows CHECK (row_count >= 0),
    CONSTRAINT ck_catalog_chunk_uncompressed CHECK (uncompressed_bytes >= 0),
    CONSTRAINT ck_catalog_chunk_stored CHECK (stored_bytes >= 0),
    CONSTRAINT ck_catalog_chunk_visibility CHECK (visibility IN (0, 1)));
CREATE INDEX ix_catalog_chunk_hash ON dbo.catalog_chunk(chunk_hash);

IF NOT EXISTS (SELECT 1 FROM dbo.catalog_metadata WHERE metadata_key = 1)
INSERT INTO dbo.catalog_metadata(
    metadata_key, schema_version, store_epoch, active_version, pinned_version, updated_at_utc)
VALUES (1, 1, LOWER(REPLACE(CONVERT(nvarchar(36), NEWID()), '-', '')), 0, NULL,
        TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'));
