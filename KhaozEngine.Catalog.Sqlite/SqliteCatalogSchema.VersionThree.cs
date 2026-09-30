using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Sqlite;

/// <summary>
/// Version 3 and the two versions this build migrates from. Version 3 gives every catalog row a creation time
/// and every row that changes after insert an update time, as nullable <c>INTEGER</c> Unix milliseconds. An
/// existing column that already is the insert time needs no twin: <c>catalog_version.published_at_utc</c>,
/// <c>catalog_audit.occurred_at_utc</c>, <c>catalog_content_upgrade.recorded_at_utc</c> and
/// <c>catalog_draft.opened_at_utc</c>. <c>catalog_draft_edit.edited_at_utc</c> is rewritten by every re-edit,
/// so it is that table's update time.
/// <para>
/// <b>The older shapes are DERIVED from the current one</b>, version 2 by taking the version 3 columns back
/// out and version 1 by also leaving out the ledger table, so the three can never drift apart.
/// </para>
/// <para>
/// Every static initializer this class has lives in this file, in dependency order, because the order of
/// initializers across the files of a partial class is not defined.
/// </para>
/// </summary>
internal static partial class SqliteCatalogSchema
{
    /// <summary>
    /// Every column version 3 adds, in the order the migration adds them, with the statement that adds it.
    /// </summary>
    internal static IReadOnlyList<(string Table, string Column, string AddColumn)> VersionThreeColumns { get; } =
    [
        Add("catalog_metadata", "created_at_utc"),
        Add("catalog_type", "created_at_utc"),
        Add("catalog_type", "updated_at_utc"),
        Add("catalog_family", "created_at_utc"),
        Add("catalog_family_block", "created_at_utc"),
        Add("catalog_family_block", "updated_at_utc"),
        Add("catalog_row", "created_at_utc"),
        Add("catalog_row", "updated_at_utc"),
        Add("catalog_row_field", "created_at_utc"),
        Add("catalog_id_high_water", "created_at_utc"),
        Add("catalog_id_high_water", "updated_at_utc"),
        Add("catalog_draft", "updated_at_utc"),
        Add("catalog_draft_edit", "created_at_utc"),
        Add("catalog_draft_edit_field", "created_at_utc"),
        Add("catalog_remap_rule", "created_at_utc"),
        Add("catalog_chunk", "created_at_utc"),
    ];

    /// <summary>
    /// The metadata seed exactly as version 2 wrote it, which the derived version 2 and version 1 scripts end
    /// with.
    /// </summary>
    const string VersionTwoMetadataSeed = """
        INSERT OR IGNORE INTO catalog_metadata(metadata_key, schema_version, store_epoch, active_version, pinned_version, updated_at_utc)
        VALUES (1, 2, lower(hex(randomblob(16))), 0, NULL, CAST(strftime('%s', 'now') AS INTEGER) * 1000);
        """;

    /// <summary>Every table version 1 declared, as version 2 still declared it: the version 3 columns taken out.</summary>
    static readonly string VersionTwoCoreTables = WithoutVersionThreeColumns(CoreTables);

    /// <summary>
    /// The schema exactly as version 2 declared it, which is what a version 2 database is validated against
    /// BEFORE it is migrated.
    /// </summary>
    internal static string VersionTwoTables { get; } =
        VersionTwoCoreTables + "\n" + UpgradeLedgerTable + "\n" + VersionTwoMetadataSeed;

    /// <summary>
    /// The schema exactly as version 1 declared it, which is what a version 1 database is validated against
    /// BEFORE it is migrated: version 2 without the ledger table.
    /// </summary>
    internal static string VersionOneTables { get; } =
        VersionTwoCoreTables + "\n"
        + VersionTwoMetadataSeed.Replace("VALUES (1, 2,", "VALUES (1, 1,", StringComparison.Ordinal);

    /// <summary>
    /// The version 3 backfill, which runs after the columns are added and moves the version LAST.
    /// <para>
    /// <b>Only the four tables a publish writes are filled.</b> <c>catalog_row</c>, <c>catalog_row_field</c>,
    /// <c>catalog_chunk</c> and <c>catalog_remap_rule</c> are inserted by nothing but the publish commit, in
    /// the one transaction that inserts their version row, so each takes that version's
    /// <c>published_at_utc</c>. A row is closed by the same commit of the version that replaces it, so a closed
    /// row's update time is that version's publish time, and an open row's equals its creation time. A version
    /// the table does not hold leaves the time NULL rather than guessing one. Every other new column on a
    /// legacy row stays NULL: no table records when a family, a block, a mark, a draft or an edit was written.
    /// </para>
    /// </summary>
    internal const string VersionThreeBackfill = """
        UPDATE catalog_row SET
            created_at_utc = (SELECT v.published_at_utc FROM catalog_version v
                              WHERE v.version_number = catalog_row.valid_from_version),
            updated_at_utc = (SELECT v.published_at_utc FROM catalog_version v
                              WHERE v.version_number = COALESCE(catalog_row.replaced_in_version,
                                                                catalog_row.valid_from_version));
        UPDATE catalog_row_field SET created_at_utc =
            (SELECT v.published_at_utc FROM catalog_version v
             WHERE v.version_number = catalog_row_field.valid_from_version);
        UPDATE catalog_chunk SET created_at_utc =
            (SELECT v.published_at_utc FROM catalog_version v
             WHERE v.version_number = catalog_chunk.version_number);
        UPDATE catalog_remap_rule SET created_at_utc =
            (SELECT v.published_at_utc FROM catalog_version v
             WHERE v.version_number = catalog_remap_rule.introduced_in);
        UPDATE catalog_metadata
        SET schema_version = 3,
            updated_at_utc = CAST(strftime('%s', 'now') AS INTEGER) * 1000
        WHERE metadata_key = 1 AND schema_version = 2;
        """;

    static (string Table, string Column, string AddColumn) Add(string table, string column)
        => (table, column, "ALTER TABLE " + table + " ADD COLUMN " + column + " INTEGER NULL;");

    /// <summary>
    /// The script with every version 3 column line taken out, whichever line endings the source was checked
    /// out with. A column followed by another line loses its line, and a column that closes its table hands
    /// the closing parenthesis back to the line before it.
    /// </summary>
    static string WithoutVersionThreeColumns(string tables)
    {
        bool usesCrLf = tables.Contains("\r\n", StringComparison.Ordinal);
        string stripped = tables.ReplaceLineEndings("\n");
        foreach (string column in (string[])["created_at_utc", "updated_at_utc"])
        {
            stripped = stripped
                .Replace("\n    " + column + " INTEGER NULL,", string.Empty, StringComparison.Ordinal)
                .Replace(",\n    " + column + " INTEGER NULL);", ");", StringComparison.Ordinal);
        }

        return usesCrLf ? stripped.ReplaceLineEndings("\r\n") : stripped;
    }
}
