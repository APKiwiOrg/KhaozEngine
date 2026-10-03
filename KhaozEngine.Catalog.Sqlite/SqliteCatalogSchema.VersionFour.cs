namespace KhaozEngine.Catalog.Sqlite;

/// <summary>
/// Version 4, text authoring: the four text tables, the nullable completeness of a version's text record and
/// the nullable language of an audit row, as constants only. The derived older shapes and every static
/// initializer stay in <c>SqliteCatalogSchema.VersionThree.cs</c>.
/// <para>
/// <b>Values are TEXT under binary comparison and carry no length check.</b> A value's bound is strict UTF-8
/// bytes, which SQLite's <c>length()</c> does not measure, so the store enforces it before any statement.
/// The identity columns keep the structural checks every other key column here has.
/// </para>
/// <para>
/// <b>Every new table records its creation time, and the two whose rows change after insert record their
/// update time</b>, set by the store from its own clock in the statement that writes the row. A text edit is
/// replaced in place by a later intent for its target, and a published revision is closed by the version that
/// replaces it. The new tables start empty on a migrated catalog, so their time columns are NOT NULL.
/// </para>
/// <para>
/// <b>A legacy version's completeness is NULL, which means unknown.</b> The migration writes no completeness,
/// no text, no language and no time for anything it did not see happen. Every commit this build makes writes
/// the version complete, a version publishing no language included.
/// </para>
/// </summary>
internal static partial class SqliteCatalogSchema
{
    /// <summary>The completeness column of <c>catalog_version</c>, as both the fresh DDL and the migration add it.</summary>
    internal const string TextCompleteColumn =
        "text_snapshot_complete INTEGER NULL CHECK (text_snapshot_complete IS NULL OR text_snapshot_complete = 1)";

    /// <summary>The language column of <c>catalog_audit</c>, as both the fresh DDL and the migration add it.</summary>
    internal const string AuditLanguageColumn =
        "language_tag TEXT COLLATE BINARY NULL CHECK (language_tag IS NULL OR length(language_tag) BETWEEN 1 AND 35)";

    /// <summary>
    /// The four tables version 4 adds, with their indexes.
    /// <para>
    /// <c>catalog_draft_text_edit</c> holds one intent per canonical target in first-applied order, and a later
    /// intent for the target replaces its value in the same row. <c>catalog_draft_text_language</c> holds the
    /// draft's language introductions, independent of the intents. <c>catalog_text</c> is temporal like
    /// <c>catalog_row</c>, with one live revision per string. <c>catalog_text_chunk</c> is the complete language
    /// record of a committed version, wire spelling and chunk hash, empty languages included.
    /// </para>
    /// </summary>
    internal const string VersionFourTables = """
        CREATE TABLE IF NOT EXISTS catalog_draft_text_edit (
            edit_ordinal INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            type_id INTEGER NOT NULL,
            content_key TEXT COLLATE BINARY NOT NULL CHECK (length(content_key) BETWEEN 1 AND 64),
            field_name TEXT COLLATE BINARY NOT NULL CHECK (length(field_name) BETWEEN 1 AND 64),
            language_tag TEXT COLLATE BINARY NOT NULL CHECK (length(language_tag) BETWEEN 1 AND 35),
            operation INTEGER NOT NULL CHECK (operation IN (1, 2)),
            text_value TEXT COLLATE BINARY NULL,
            edited_by TEXT COLLATE BINARY NOT NULL CHECK (length(edited_by) BETWEEN 1 AND 128),
            created_at_utc INTEGER NOT NULL,
            updated_at_utc INTEGER NOT NULL,
            FOREIGN KEY (type_id) REFERENCES catalog_type(type_id),
            CHECK ((operation = 1) = (text_value IS NOT NULL)));
        CREATE UNIQUE INDEX IF NOT EXISTS ux_catalog_draft_text_edit_target
            ON catalog_draft_text_edit(type_id, content_key, field_name, language_tag);

        CREATE TABLE IF NOT EXISTS catalog_draft_text_language (
            language_ordinal INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            language_tag TEXT COLLATE BINARY NOT NULL CHECK (length(language_tag) BETWEEN 1 AND 35),
            wire_tag TEXT COLLATE BINARY NOT NULL CHECK (length(wire_tag) BETWEEN 1 AND 35),
            created_at_utc INTEGER NOT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_catalog_draft_text_language_tag
            ON catalog_draft_text_language(language_tag);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_catalog_draft_text_language_wire
            ON catalog_draft_text_language(wire_tag);

        CREATE TABLE IF NOT EXISTS catalog_text (
            type_id INTEGER NOT NULL,
            definition_id INTEGER NOT NULL CHECK (definition_id >= 1),
            field_name TEXT COLLATE BINARY NOT NULL CHECK (length(field_name) BETWEEN 1 AND 64),
            language_tag TEXT COLLATE BINARY NOT NULL CHECK (length(language_tag) BETWEEN 1 AND 35),
            valid_from_version INTEGER NOT NULL CHECK (valid_from_version >= 1),
            replaced_in_version INTEGER NULL CHECK (replaced_in_version IS NULL
                OR replaced_in_version > valid_from_version),
            text_value TEXT COLLATE BINARY NOT NULL,
            created_at_utc INTEGER NOT NULL,
            updated_at_utc INTEGER NOT NULL,
            PRIMARY KEY (type_id, definition_id, field_name, language_tag, valid_from_version),
            FOREIGN KEY (type_id) REFERENCES catalog_type(type_id),
            FOREIGN KEY (valid_from_version) REFERENCES catalog_version(version_number));
        CREATE UNIQUE INDEX IF NOT EXISTS ux_catalog_text_live
            ON catalog_text(type_id, definition_id, field_name, language_tag) WHERE replaced_in_version IS NULL;
        CREATE INDEX IF NOT EXISTS ix_catalog_text_version ON catalog_text(valid_from_version, replaced_in_version);

        CREATE TABLE IF NOT EXISTS catalog_text_chunk (
            version_number INTEGER NOT NULL,
            language_tag TEXT COLLATE BINARY NOT NULL CHECK (length(language_tag) BETWEEN 1 AND 35),
            wire_tag TEXT COLLATE BINARY NOT NULL CHECK (length(wire_tag) BETWEEN 1 AND 35),
            chunk_hash TEXT COLLATE BINARY NOT NULL CHECK (length(chunk_hash) = 64),
            created_at_utc INTEGER NOT NULL,
            PRIMARY KEY (version_number, language_tag),
            FOREIGN KEY (version_number) REFERENCES catalog_version(version_number));
        CREATE UNIQUE INDEX IF NOT EXISTS ux_catalog_text_chunk_wire ON catalog_text_chunk(version_number, wire_tag);
        CREATE INDEX IF NOT EXISTS ix_catalog_text_chunk_hash ON catalog_text_chunk(chunk_hash);
        """;

    /// <summary>
    /// The version 3 to version 4 migration, which a held connection runs in ONE immediate transaction: the
    /// four empty tables, the two nullable columns, and the metadata row moved to 4 LAST. No legacy row is
    /// written, so every version's completeness and every audit row's language stay NULL.
    /// </summary>
    internal const string MigrateToVersionFour = VersionFourTables + "\n"
        + "ALTER TABLE catalog_version ADD COLUMN " + TextCompleteColumn + ";\n"
        + "ALTER TABLE catalog_audit ADD COLUMN " + AuditLanguageColumn + ";\n"
        + """
        UPDATE catalog_metadata
        SET schema_version = 4,
            updated_at_utc = CAST(strftime('%s', 'now') AS INTEGER) * 1000
        WHERE metadata_key = 1 AND schema_version = 3;
        """;
}
