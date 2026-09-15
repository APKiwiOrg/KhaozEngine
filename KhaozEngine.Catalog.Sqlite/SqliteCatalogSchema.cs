using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Catalog.Sqlite;

/// <summary>
/// The content authoring schema as SQLite holds it (spec 4.4): the fourteen tables, their indexes and the
/// metadata seed, as one idempotent DDL script, plus the version and migration this build supports.
/// <para>
/// This file holds the DDL and the two constants and NOTHING else. Validating a database against it is
/// <see cref="SqliteCatalogSchemaValidation"/>, which is the journal's split between its schema and its
/// validation helpers, and it is here from the first release because the DDL alone is most of a file.
/// </para>
/// <para>
/// <b>It is the JOURNAL's style rather than the wallet's.</b> A wallet-style inline bootstrap with no
/// version at all cannot be migrated, and this schema will gain tables as more content types land and as
/// inheritance ships, so it carries a <see cref="CurrentVersion"/>, a named
/// <see cref="RequiredMigration"/>, a <c>catalog_metadata.schema_version</c> row and validation of every
/// object read back from <c>sqlite_master</c>.
/// </para>
/// <para>
/// Every key column is <c>TEXT COLLATE BINARY</c>, because comparison is ordinal always and a
/// case-insensitive database default silently merged two accounts once (contracts 5.3). Every size cap is a
/// <c>CHECK</c>, and every foreign key is declared because the bootstrap sets
/// <c>PRAGMA foreign_keys = ON</c>.
/// </para>
/// <para>
/// <b>There is no <c>UPDATE</c> and no <c>DELETE</c> for <c>catalog_remap_rule</c> anywhere in this
/// provider.</b> Rules are append only (contracts 8.1). The journal takes the stronger position with a
/// <c>BEFORE DELETE</c> trigger, and this schema deliberately does not copy it: that guard exists to permit
/// a retention sweep and there is no retention sweep here. A later phase that adds a maintenance path over
/// rules adds the trigger with it.
/// </para>
/// </summary>
internal static class SqliteCatalogSchema
{
    /// <summary>The schema version this build writes and the only one it accepts.</summary>
    internal const int CurrentVersion = 1;

    /// <summary>The migration an operator is told to apply when the database does not match.</summary>
    internal const string RequiredMigration = "catalog-v1-initial";

    /// <summary>
    /// The bootstrap the held connection runs on open. Foreign keys are OFF by default in SQLite, and every
    /// table below declares its references, so this is what makes them mean anything.
    /// </summary>
    internal const string BootstrapSql = "PRAGMA foreign_keys = ON;";

    /// <summary>
    /// The whole schema, idempotent, exactly as spec 4.4 gives it with ONE addition named below.
    /// <para>
    /// <b>The addition is <c>catalog_draft_edit.imported_retired</c>.</b> A bundle carries every live row
    /// including the retired ones, and a lossless import reproduces them through an <c>Add</c> edit that is
    /// already retired and appends no second retire rule, which is
    /// <see cref="ContentEdit.ImportedAsRetired"/>. Spec 4.4's <c>catalog_draft_edit</c> has no column for
    /// that fact, so a draft written to the database and read back would silently publish every imported
    /// retired row as live. The column is the smallest thing that keeps the import lossless.
    /// </para>
    /// </summary>
    internal const string Tables = """
        CREATE TABLE IF NOT EXISTS catalog_metadata (
            metadata_key INTEGER NOT NULL PRIMARY KEY CHECK (metadata_key = 1),
            schema_version INTEGER NOT NULL CHECK (schema_version >= 1),
            store_epoch TEXT COLLATE BINARY NOT NULL CHECK (length(store_epoch) IN (32, 36)),
            active_version INTEGER NOT NULL DEFAULT 0 CHECK (active_version >= 0),
            pinned_version INTEGER NULL CHECK (pinned_version IS NULL OR pinned_version >= 1),
            updated_at_utc INTEGER NOT NULL);

        CREATE TABLE IF NOT EXISTS catalog_type (
            type_id INTEGER NOT NULL PRIMARY KEY CHECK (type_id BETWEEN 1 AND 65535),
            type_key TEXT COLLATE BINARY NOT NULL CHECK (length(type_key) BETWEEN 1 AND 64),
            chunk_slots INTEGER NOT NULL CHECK (chunk_slots BETWEEN 256 AND 65536),
            default_visibility INTEGER NOT NULL CHECK (default_visibility IN (0, 1)),
            max_definition_id INTEGER NULL CHECK (max_definition_id IS NULL OR max_definition_id >= 1),
            first_seen_version INTEGER NOT NULL CHECK (first_seen_version >= 0));
        CREATE UNIQUE INDEX IF NOT EXISTS ux_catalog_type_key ON catalog_type(type_key);

        CREATE TABLE IF NOT EXISTS catalog_version (
            version_number INTEGER NOT NULL PRIMARY KEY CHECK (version_number >= 1),
            server_manifest_hash TEXT COLLATE BINARY NOT NULL CHECK (length(server_manifest_hash) = 64),
            client_manifest_hash TEXT COLLATE BINARY NOT NULL CHECK (length(client_manifest_hash) = 64),
            minimum_server_build INTEGER NOT NULL CHECK (minimum_server_build >= 0),
            minimum_client_build INTEGER NOT NULL CHECK (minimum_client_build >= 0),
            format_generation INTEGER NOT NULL CHECK (format_generation >= 1),
            base_version INTEGER NOT NULL CHECK (base_version >= 0),
            published_by TEXT COLLATE BINARY NOT NULL CHECK (length(published_by) BETWEEN 1 AND 128),
            note TEXT COLLATE BINARY NOT NULL CHECK (length(note) <= 1024),
            published_at_utc INTEGER NOT NULL);

        CREATE TABLE IF NOT EXISTS catalog_row (
            type_id INTEGER NOT NULL,
            definition_id INTEGER NOT NULL CHECK (definition_id >= 1),
            valid_from_version INTEGER NOT NULL CHECK (valid_from_version >= 1),
            replaced_in_version INTEGER NULL CHECK (replaced_in_version IS NULL
                OR replaced_in_version > valid_from_version),
            content_key TEXT COLLATE BINARY NOT NULL CHECK (length(content_key) BETWEEN 1 AND 64),
            parent_id INTEGER NOT NULL DEFAULT 0 CHECK (parent_id >= 0),
            family_id INTEGER NULL,
            retired INTEGER NOT NULL DEFAULT 0 CHECK (retired IN (0, 1)),
            PRIMARY KEY (type_id, definition_id, valid_from_version),
            FOREIGN KEY (type_id) REFERENCES catalog_type(type_id),
            FOREIGN KEY (valid_from_version) REFERENCES catalog_version(version_number),
            FOREIGN KEY (family_id) REFERENCES catalog_family(family_id));
        CREATE INDEX IF NOT EXISTS ix_catalog_row_live ON catalog_row(type_id, replaced_in_version, definition_id);
        CREATE INDEX IF NOT EXISTS ix_catalog_row_key ON catalog_row(type_id, content_key, valid_from_version);
        CREATE INDEX IF NOT EXISTS ix_catalog_row_changed ON catalog_row(valid_from_version, type_id, definition_id);

        CREATE TABLE IF NOT EXISTS catalog_row_field (
            type_id INTEGER NOT NULL,
            definition_id INTEGER NOT NULL,
            valid_from_version INTEGER NOT NULL,
            field_name TEXT COLLATE BINARY NOT NULL CHECK (length(field_name) BETWEEN 1 AND 64),
            field_kind INTEGER NOT NULL CHECK (field_kind BETWEEN 0 AND 6),
            int_value INTEGER NULL,
            text_value TEXT COLLATE BINARY NULL CHECK (text_value IS NULL OR length(text_value) <= 192),
            blob_value BLOB NULL CHECK (blob_value IS NULL OR length(blob_value) <= 4096),
            PRIMARY KEY (type_id, definition_id, valid_from_version, field_name),
            FOREIGN KEY (type_id, definition_id, valid_from_version)
                REFERENCES catalog_row(type_id, definition_id, valid_from_version));

        CREATE TABLE IF NOT EXISTS catalog_family (
            family_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            type_id INTEGER NOT NULL,
            family_key TEXT COLLATE BINARY NOT NULL CHECK (length(family_key) BETWEEN 1 AND 64),
            block_size INTEGER NOT NULL CHECK (block_size BETWEEN 16 AND 65536),
            retired INTEGER NOT NULL DEFAULT 0 CHECK (retired IN (0, 1)),
            created_in_version INTEGER NOT NULL CHECK (created_in_version >= 1),
            FOREIGN KEY (type_id) REFERENCES catalog_type(type_id));
        CREATE UNIQUE INDEX IF NOT EXISTS ux_catalog_family_key ON catalog_family(type_id, family_key);

        CREATE TABLE IF NOT EXISTS catalog_family_block (
            family_id INTEGER NOT NULL,
            block_ordinal INTEGER NOT NULL CHECK (block_ordinal >= 0),
            base_id INTEGER NOT NULL CHECK (base_id >= 1),
            block_size INTEGER NOT NULL CHECK (block_size BETWEEN 16 AND 65536),
            next_free_id INTEGER NOT NULL CHECK (next_free_id >= base_id),
            reserved_in_version INTEGER NOT NULL CHECK (reserved_in_version >= 1),
            PRIMARY KEY (family_id, block_ordinal),
            FOREIGN KEY (family_id) REFERENCES catalog_family(family_id),
            CHECK (base_id % block_size = 0),
            CHECK (next_free_id <= base_id + block_size));

        CREATE TABLE IF NOT EXISTS catalog_id_high_water (
            type_id INTEGER NOT NULL PRIMARY KEY,
            reserved_through INTEGER NOT NULL CHECK (reserved_through >= 0),
            issued_through INTEGER NOT NULL CHECK (issued_through >= 0 AND issued_through <= reserved_through),
            FOREIGN KEY (type_id) REFERENCES catalog_type(type_id));

        CREATE TABLE IF NOT EXISTS catalog_draft (
            draft_key INTEGER NOT NULL PRIMARY KEY CHECK (draft_key = 1),
            base_version INTEGER NOT NULL CHECK (base_version >= 0),
            opened_by TEXT COLLATE BINARY NOT NULL CHECK (length(opened_by) BETWEEN 1 AND 128),
            opened_at_utc INTEGER NOT NULL,
            note TEXT COLLATE BINARY NOT NULL DEFAULT '' CHECK (length(note) <= 1024));

        CREATE TABLE IF NOT EXISTS catalog_draft_edit (
            edit_ordinal INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            type_id INTEGER NOT NULL,
            definition_id INTEGER NOT NULL DEFAULT 0 CHECK (definition_id >= 0),
            content_key TEXT COLLATE BINARY NOT NULL CHECK (length(content_key) BETWEEN 1 AND 64),
            operation INTEGER NOT NULL CHECK (operation IN (1, 2, 3, 4)),
            retire_policy INTEGER NOT NULL DEFAULT 0 CHECK (retire_policy IN (0, 1, 2)),
            replacement_id INTEGER NOT NULL DEFAULT 0 CHECK (replacement_id >= 0),
            fork_key TEXT COLLATE BINARY NULL CHECK (fork_key IS NULL OR length(fork_key) BETWEEN 1 AND 64),
            fork_flag_field TEXT COLLATE BINARY NULL CHECK (fork_flag_field IS NULL OR length(fork_flag_field) BETWEEN 1 AND 64),
            family_id INTEGER NULL,
            imported_retired INTEGER NOT NULL DEFAULT 0 CHECK (imported_retired IN (0, 1)),
            edited_by TEXT COLLATE BINARY NOT NULL CHECK (length(edited_by) BETWEEN 1 AND 128),
            edited_at_utc INTEGER NOT NULL,
            FOREIGN KEY (type_id) REFERENCES catalog_type(type_id));
        CREATE UNIQUE INDEX IF NOT EXISTS ux_catalog_draft_edit_target
            ON catalog_draft_edit(type_id, definition_id, content_key);

        CREATE TABLE IF NOT EXISTS catalog_draft_edit_field (
            edit_ordinal INTEGER NOT NULL,
            field_name TEXT COLLATE BINARY NOT NULL CHECK (length(field_name) BETWEEN 1 AND 64),
            field_kind INTEGER NOT NULL CHECK (field_kind BETWEEN 0 AND 6),
            int_value INTEGER NULL,
            text_value TEXT COLLATE BINARY NULL CHECK (text_value IS NULL OR length(text_value) <= 192),
            blob_value BLOB NULL CHECK (blob_value IS NULL OR length(blob_value) <= 4096),
            PRIMARY KEY (edit_ordinal, field_name),
            FOREIGN KEY (edit_ordinal) REFERENCES catalog_draft_edit(edit_ordinal) ON DELETE CASCADE);

        CREATE TABLE IF NOT EXISTS catalog_audit (
            audit_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            occurred_at_utc INTEGER NOT NULL,
            actor TEXT COLLATE BINARY NOT NULL CHECK (length(actor) BETWEEN 1 AND 128),
            operator TEXT COLLATE BINARY NOT NULL DEFAULT '' CHECK (length(operator) <= 128),
            action TEXT COLLATE BINARY NOT NULL CHECK (length(action) BETWEEN 1 AND 32),
            type_id INTEGER NOT NULL DEFAULT 0,
            definition_id INTEGER NOT NULL DEFAULT 0,
            content_key TEXT COLLATE BINARY NOT NULL DEFAULT '' CHECK (length(content_key) <= 64),
            field_name TEXT COLLATE BINARY NOT NULL DEFAULT '' CHECK (length(field_name) <= 64),
            before_value TEXT COLLATE BINARY NULL CHECK (before_value IS NULL OR length(before_value) <= 4096),
            after_value TEXT COLLATE BINARY NULL CHECK (after_value IS NULL OR length(after_value) <= 4096),
            version_number INTEGER NOT NULL DEFAULT 0,
            note TEXT COLLATE BINARY NOT NULL DEFAULT '' CHECK (length(note) <= 1024));
        CREATE INDEX IF NOT EXISTS ix_catalog_audit_time ON catalog_audit(occurred_at_utc, audit_id);
        CREATE INDEX IF NOT EXISTS ix_catalog_audit_target ON catalog_audit(type_id, definition_id, audit_id);

        CREATE TABLE IF NOT EXISTS catalog_remap_rule (
            sequence INTEGER NOT NULL PRIMARY KEY CHECK (sequence >= 1),
            introduced_in INTEGER NOT NULL CHECK (introduced_in >= 1),
            type_id INTEGER NOT NULL,
            kind INTEGER NOT NULL CHECK (kind BETWEEN 1 AND 255),
            from_id INTEGER NOT NULL CHECK (from_id >= 1),
            to_id INTEGER NOT NULL DEFAULT 0 CHECK (to_id >= 0),
            payload BLOB NOT NULL DEFAULT x'' CHECK (length(payload) <= 64),
            FOREIGN KEY (type_id) REFERENCES catalog_type(type_id),
            FOREIGN KEY (introduced_in) REFERENCES catalog_version(version_number));

        CREATE TABLE IF NOT EXISTS catalog_chunk (
            version_number INTEGER NOT NULL,
            type_id INTEGER NOT NULL,
            chunk_index INTEGER NOT NULL CHECK (chunk_index >= 0),
            chunk_hash TEXT COLLATE BINARY NOT NULL CHECK (length(chunk_hash) = 64),
            row_count INTEGER NOT NULL CHECK (row_count >= 0),
            uncompressed_bytes INTEGER NOT NULL CHECK (uncompressed_bytes >= 0),
            stored_bytes INTEGER NOT NULL CHECK (stored_bytes >= 0),
            visibility INTEGER NOT NULL CHECK (visibility IN (0, 1)),
            PRIMARY KEY (version_number, type_id, chunk_index, visibility),
            FOREIGN KEY (version_number) REFERENCES catalog_version(version_number),
            FOREIGN KEY (type_id) REFERENCES catalog_type(type_id));
        CREATE INDEX IF NOT EXISTS ix_catalog_chunk_hash ON catalog_chunk(chunk_hash);

        INSERT OR IGNORE INTO catalog_metadata(metadata_key, schema_version, store_epoch, active_version, pinned_version, updated_at_utc)
        VALUES (1, 1, lower(hex(randomblob(16))), 0, NULL, CAST(strftime('%s', 'now') AS INTEGER) * 1000);
        """;
}
