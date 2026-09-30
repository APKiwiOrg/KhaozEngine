using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.SqlServer;

/// <summary>
/// Schema version 3 and the version 2 to 3 migration. Version 3 gives every catalog row a creation time and every
/// row that changes after insert an update time, as nullable <c>datetimeoffset(7)</c>. An existing column that
/// already is the insert time needs no twin: <c>catalog_version.published_at_utc</c>,
/// <c>catalog_audit.occurred_at_utc</c>, <c>catalog_content_upgrade.recorded_at_utc</c> and
/// <c>catalog_draft.opened_at_utc</c>. <c>catalog_draft_edit.edited_at_utc</c> is rewritten by every re-edit, so it
/// is that table's update time. Version 3 adds no table, index, key, check or default, so every name set serves
/// version 2 unchanged.
/// <para>
/// Every static initializer here lives in this one file, in dependency order, because the order of initializers
/// across the files of a partial class is not defined.
/// </para>
/// </summary>
internal static partial class SqlServerCatalogSchema
{
    /// <summary>Every column version 3 adds, in the order the migration adds them.</summary>
    internal static IReadOnlyList<(string Table, string Column)> VersionThreeColumns { get; } =
    [
        ("catalog_metadata", "created_at_utc"),
        ("catalog_type", "created_at_utc"),
        ("catalog_type", "updated_at_utc"),
        ("catalog_family", "created_at_utc"),
        ("catalog_family_block", "created_at_utc"),
        ("catalog_family_block", "updated_at_utc"),
        ("catalog_row", "created_at_utc"),
        ("catalog_row", "updated_at_utc"),
        ("catalog_row_field", "created_at_utc"),
        ("catalog_id_high_water", "created_at_utc"),
        ("catalog_id_high_water", "updated_at_utc"),
        ("catalog_draft", "updated_at_utc"),
        ("catalog_draft_edit", "created_at_utc"),
        ("catalog_draft_edit_field", "created_at_utc"),
        ("catalog_remap_rule", "created_at_utc"),
        ("catalog_chunk", "created_at_utc"),
    ];

    /// <summary>
    /// The version 2 to version 3 migration, one statement per batch, because SQL Server compiles a whole batch
    /// before running any of it and a statement that reads a column an earlier one added must not share its batch.
    /// <para>
    /// <b>Each column add is guarded by <c>COL_LENGTH</c></b>, so a database that already carries some of the
    /// columns, because the adds ran without the version move, gains only the rest.
    /// </para>
    /// <para>
    /// <b>Only the four tables a publish writes are filled.</b> <c>catalog_row</c>, <c>catalog_row_field</c>,
    /// <c>catalog_chunk</c> and <c>catalog_remap_rule</c> are inserted by nothing but the publish commit, in the one
    /// transaction that inserts their version row, so each takes that version's <c>published_at_utc</c>. A row is
    /// closed by the commit of the version that replaces it, so a closed row's update time is that version's
    /// publish time, and an open row's equals its creation time. A version the table does not hold leaves the time
    /// NULL rather than guessing one. Every other new column on a legacy row stays NULL: no table records when a
    /// family, a block, a mark, a draft or an edit was written. The version moves LAST.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> VersionTwoMigrationSql { get; } =
        Array.AsReadOnly(BuildVersionTwoMigration());

    static string[] BuildVersionTwoMigration()
    {
        var statements = new List<string>(VersionThreeColumns.Count + 5);
        foreach ((string table, string column) in VersionThreeColumns)
        {
            statements.Add($"""
                IF COL_LENGTH(N'dbo.{table}', N'{column}') IS NULL
                    ALTER TABLE dbo.{table} ADD {column} datetimeoffset(7) NULL;
                """);
        }

        statements.Add("""
            UPDATE r
            SET created_at_utc = created.published_at_utc,
                updated_at_utc = changed.published_at_utc
            FROM dbo.catalog_row r
            LEFT JOIN dbo.catalog_version created ON created.version_number = r.valid_from_version
            LEFT JOIN dbo.catalog_version changed
                ON changed.version_number = COALESCE(r.replaced_in_version, r.valid_from_version);
            """);
        statements.Add("""
            UPDATE f
            SET created_at_utc = v.published_at_utc
            FROM dbo.catalog_row_field f
            LEFT JOIN dbo.catalog_version v ON v.version_number = f.valid_from_version;
            """);
        statements.Add("""
            UPDATE c
            SET created_at_utc = v.published_at_utc
            FROM dbo.catalog_chunk c
            LEFT JOIN dbo.catalog_version v ON v.version_number = c.version_number;
            """);
        statements.Add("""
            UPDATE r
            SET created_at_utc = v.published_at_utc
            FROM dbo.catalog_remap_rule r
            LEFT JOIN dbo.catalog_version v ON v.version_number = r.introduced_in;
            """);
        statements.Add("""
            UPDATE dbo.catalog_metadata
            SET schema_version = 3,
                updated_at_utc = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
            WHERE metadata_key = 1 AND schema_version = 2;
            """);
        return statements.ToArray();
    }
}
