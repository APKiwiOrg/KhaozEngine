using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.SqlServer;

/// <summary>
/// Schema version 4, text authoring, and the version 3 to 4 migration <c>catalog-v4-text-authoring</c>: the
/// four text tables, the nullable completeness of a version's text record and the nullable language of an
/// audit row.
/// <para>
/// <b>Values are <c>nvarchar(max)</c> under the catalog's binary collation and carry no length check.</b> A
/// value's bound is strict UTF-8 bytes, which neither <c>LEN</c> nor <c>DATALENGTH</c> measures, so the store
/// enforces it before any statement. The identity columns keep the structural checks every other key column
/// here has, and a language column also checks that it holds only the ASCII letters, digits and hyphens a
/// language identity is made of.
/// </para>
/// <para>
/// <b>Every new table records its creation time, and the two whose rows change after insert record their
/// update time</b>, set by the store from its own clock in the statement that writes the row. The new tables
/// start empty on a migrated catalog, so their time columns are NOT NULL.
/// </para>
/// <para>
/// <b>A legacy version's completeness is NULL, which means unknown.</b> The migration writes no completeness,
/// no text, no language and no time for anything it did not see happen. Every commit this build makes writes
/// the version complete, a version publishing no language included.
/// </para>
/// <para>
/// <b>The migration's tables are READ from the embedded version 4 script rather than transcribed.</b> Each
/// text table's <c>CREATE TABLE</c> and its indexes are the very statements a fresh create runs, so a fresh
/// and a migrated catalog cannot disagree about them. The two column adds are the constants below, and
/// <c>SqlServerTextSchemaMigrationTests</c> pins that the version 4 script is the version 3 script plus
/// exactly these additions.
/// </para>
/// </summary>
internal static partial class SqlServerCatalogSchema
{
    /// <summary>The completeness column version 4 adds to <c>catalog_version</c>.</summary>
    internal const string TextCompleteColumn = "text_snapshot_complete int NULL";

    /// <summary>The named check on <see cref="TextCompleteColumn"/>.</summary>
    internal const string TextCompleteCheck =
        "CONSTRAINT ck_catalog_version_text_complete CHECK (text_snapshot_complete IS NULL OR text_snapshot_complete = 1)";

    /// <summary>The language column version 4 adds to <c>catalog_audit</c>.</summary>
    internal const string AuditLanguageColumn = "language_tag nvarchar(35) COLLATE Latin1_General_100_BIN2 NULL";

    /// <summary>The named check on <see cref="AuditLanguageColumn"/>.</summary>
    internal const string AuditLanguageCheck =
        "CONSTRAINT ck_catalog_audit_language CHECK (language_tag IS NULL OR (LEN(language_tag) BETWEEN 1 AND 35 AND language_tag NOT LIKE N'%[^a-z0-9-]%'))";

    /// <summary>
    /// The four tables version 4 adds, in the order the script creates them. <c>catalog_draft_text_edit</c>
    /// holds one intent per canonical target in first-applied order, <c>catalog_draft_text_language</c> the
    /// draft's language introductions, <c>catalog_text</c> the temporal values with one live revision per
    /// string, and <c>catalog_text_chunk</c> the complete language record of a committed version.
    /// </summary>
    internal static IReadOnlyList<string> VersionFourTables { get; } =
        ["catalog_draft_text_edit", "catalog_draft_text_language", "catalog_text", "catalog_text_chunk"];

    /// <summary>
    /// The version 3 to version 4 migration, one statement per batch: each text table and its indexes as the
    /// version 4 script declares them, the two nullable columns and their checks, and the metadata row moved
    /// to 4 LAST. No legacy row is written, so every version's completeness and every audit row's language
    /// stay NULL. It is built on each read rather than in a static initializer, because the order of
    /// initializers across the files of a partial class is not defined and this reads <see cref="SchemaSql"/>.
    /// </summary>
    internal static IReadOnlyList<string> VersionThreeMigrationSql => Array.AsReadOnly(BuildVersionThreeMigration());

    static string[] BuildVersionThreeMigration()
    {
        string script = SchemaSql.ReplaceLineEndings("\n");
        var statements = new List<string>();
        foreach (string table in VersionFourTables)
        {
            statements.Add(TableStatement(script, table));
            statements.AddRange(IndexStatements(script, table));
        }

        statements.Add("ALTER TABLE dbo.catalog_version ADD " + TextCompleteColumn + ";");
        statements.Add("ALTER TABLE dbo.catalog_version ADD " + TextCompleteCheck + ";");
        statements.Add("ALTER TABLE dbo.catalog_audit ADD " + AuditLanguageColumn + ";");
        statements.Add("ALTER TABLE dbo.catalog_audit ADD " + AuditLanguageCheck + ";");
        statements.Add("""
            UPDATE dbo.catalog_metadata
            SET schema_version = 4,
                updated_at_utc = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
            WHERE metadata_key = 1 AND schema_version = 3;
            """);
        return statements.ToArray();
    }

    /// <summary>One table's <c>CREATE TABLE</c> statement, through the <c>);</c> that closes it.</summary>
    static string TableStatement(string script, string table)
    {
        int start = script.IndexOf("CREATE TABLE dbo." + table + " (\n", StringComparison.Ordinal);
        int end = start < 0 ? -1 : script.IndexOf(");\n", start, StringComparison.Ordinal);
        return end < 0
            ? throw new InvalidOperationException(
                "The embedded version 4 schema declares no table " + table + ", so the migration cannot add it.")
            : script[start..(end + 2)];
    }

    /// <summary>Every standalone index the script creates on one table, each a statement of its own.</summary>
    static IEnumerable<string> IndexStatements(string script, string table)
    {
        foreach (string line in script.Split('\n'))
        {
            if (line.StartsWith("CREATE ", StringComparison.Ordinal)
                && line.Contains(" INDEX ", StringComparison.Ordinal)
                && line.Contains(" ON dbo." + table + "(", StringComparison.Ordinal))
            {
                yield return line;
            }
        }
    }
}
