using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Catalog.SqlServer;
using Xunit;

namespace KhaozEngine.Tests.Catalog.SqlServer;

/// <summary>
/// Offline pins on schema version 4 of the SQL Server content catalog, which need no SQL Server: the embedded
/// version 4 script is the version 3 script plus exactly the text additions, the migration runs those same
/// statements with the version moved last, and the older name sets the validator compares are version 4's
/// without them.
/// </summary>
public sealed partial class SqlServerTextSchemaMigrationTests
{
    /// <summary>The metadata seed's version, which is the one other line version 4 changes.</summary>
    const string SeedFour = "VALUES (1, 4, LOWER(";

    [Fact]
    public void Version_four_names_its_migration_and_its_four_tables()
    {
        Assert.Equal(4, SqlServerCatalogSchema.CurrentVersion);
        Assert.Equal(MigrationName, SqlServerCatalogSchema.RequiredMigration);
        Assert.Equal(
            new[] { "catalog_draft_text_edit", "catalog_draft_text_language", "catalog_text", "catalog_text_chunk" },
            SqlServerCatalogSchema.VersionFourTables);
    }

    [Fact]
    public void The_version_four_script_is_the_version_three_script_plus_exactly_the_text_additions()
    {
        string four = SqlServerCatalogSchema.SchemaSql.ReplaceLineEndings("\n");
        string three = SqlServerCatalogSchema.VersionThreeSchemaSql.ReplaceLineEndings("\n");

        string derived = four;
        foreach (string table in SqlServerCatalogSchema.VersionFourTables)
        {
            // Each table, its indexes and the blank line that separates it from the next statement group.
            string group = string.Join("\n", TextTableStatements(four, table)) + "\n\n";
            Assert.Equal(1, Occurrences(derived, group));
            derived = derived.Replace(group, string.Empty, StringComparison.Ordinal);
        }

        foreach (string column in new[] { SqlServerCatalogSchema.TextCompleteColumn, SqlServerCatalogSchema.AuditLanguageColumn })
        {
            Assert.Equal(1, Occurrences(derived, "\n    " + column + ",\n"));
            derived = derived.Replace("\n    " + column + ",\n", "\n", StringComparison.Ordinal);
        }

        foreach (string check in new[] { SqlServerCatalogSchema.TextCompleteCheck, SqlServerCatalogSchema.AuditLanguageCheck })
        {
            Assert.Equal(1, Occurrences(derived, ",\n    " + check + ");"));
            derived = derived.Replace(",\n    " + check + ");", ");", StringComparison.Ordinal);
        }

        Assert.Equal(1, Occurrences(derived, SeedFour));
        Assert.Equal(three, derived.Replace(SeedFour, "VALUES (1, 3, LOWER(", StringComparison.Ordinal));
    }

    [Fact]
    public void The_migration_runs_the_scripts_own_text_statements_then_the_columns_and_moves_the_version_last()
    {
        string four = SqlServerCatalogSchema.SchemaSql.ReplaceLineEndings("\n");
        IReadOnlyList<string> migration = SqlServerCatalogSchema.VersionThreeMigrationSql;
        string[] expected =
        [
            .. SqlServerCatalogSchema.VersionFourTables.SelectMany(table => TextTableStatements(four, table)),
            "ALTER TABLE dbo.catalog_version ADD " + SqlServerCatalogSchema.TextCompleteColumn + ";",
            "ALTER TABLE dbo.catalog_version ADD " + SqlServerCatalogSchema.TextCompleteCheck + ";",
            "ALTER TABLE dbo.catalog_audit ADD " + SqlServerCatalogSchema.AuditLanguageColumn + ";",
            "ALTER TABLE dbo.catalog_audit ADD " + SqlServerCatalogSchema.AuditLanguageCheck + ";",
        ];

        Assert.Equal(expected, migration.Take(expected.Length));
        Assert.Equal(expected.Length + 1, migration.Count);
        string last = migration[^1];
        Assert.Contains("SET schema_version = 4", last, StringComparison.Ordinal);
        Assert.Contains("WHERE metadata_key = 1 AND schema_version = 3;", last, StringComparison.Ordinal);
        Assert.Contains("SYSUTCDATETIME()", last, StringComparison.Ordinal);

        // Nothing in the migration writes a legacy row: no completeness, language, text or time is invented.
        Assert.DoesNotContain(migration.Take(migration.Count - 1), static statement => statement.Contains("UPDATE", StringComparison.Ordinal)
            || statement.Contains("INSERT", StringComparison.Ordinal));
    }

    [Fact]
    public void Values_are_unbounded_nvarchar_and_no_column_measures_a_value_by_len_or_datalength()
    {
        string four = SqlServerCatalogSchema.SchemaSql;
        Assert.Equal(2, Occurrences(four, "string_value nvarchar(max) COLLATE Latin1_General_100_BIN2"));
        Assert.DoesNotContain("LEN(string_value", four, StringComparison.Ordinal);
        Assert.DoesNotContain("DATALENGTH(string_value", four, StringComparison.Ordinal);
        Assert.Equal(5, Occurrences(four, "language_tag nvarchar(35) COLLATE Latin1_General_100_BIN2"));
        Assert.Equal(2, Occurrences(four, "wire_tag nvarchar(35) COLLATE Latin1_General_100_BIN2"));
    }

    [Fact]
    public void The_older_name_sets_are_version_fours_without_its_additions()
    {
        Assert.Equal(
            SqlServerCatalogSchema.VersionFourTables.Order(StringComparer.Ordinal),
            SqlServerCatalogSchemaExpectations.Tables.Except(SqlServerCatalogSchemaExpectations.TablesFor(3)).Order(StringComparer.Ordinal));
        Assert.Equal(
            new[] { "catalog_audit.ck_catalog_audit_language", "catalog_version.ck_catalog_version_text_complete" },
            SqlServerCatalogSchemaExpectations.Checks
                .Except(SqlServerCatalogSchemaExpectations.ChecksFor(3))
                .Where(static name => !SqlServerCatalogSchema.VersionFourTables.Contains(name[..name.IndexOf('.', StringComparison.Ordinal)]))
                .Order(StringComparer.Ordinal));
        Assert.Equal(SqlServerCatalogSchemaExpectations.Defaults, SqlServerCatalogSchemaExpectations.DefaultsFor(3));
        Assert.Same(SqlServerCatalogSchemaExpectations.TablesFor(2), SqlServerCatalogSchemaExpectations.TablesFor(3));
        Assert.DoesNotContain(
            SqlServerCatalogSchemaExpectations.TablesFor(1), static table => table == "catalog_content_upgrade");
        Assert.Throws<ArgumentOutOfRangeException>(() => SqlServerCatalogSchemaExpectations.TablesFor(5));

        // The text tables' time columns are NOT NULL in version 4 only, and version 3 keeps exactly its own.
        Assert.Equal(
            new[]
            {
                "catalog_draft_text_edit.created_at_utc|datetimeoffset|7|NOT NULL",
                "catalog_draft_text_edit.updated_at_utc|datetimeoffset|7|NOT NULL",
                "catalog_draft_text_language.created_at_utc|datetimeoffset|7|NOT NULL",
                "catalog_text.created_at_utc|datetimeoffset|7|NOT NULL",
                "catalog_text.updated_at_utc|datetimeoffset|7|NOT NULL",
                "catalog_text_chunk.created_at_utc|datetimeoffset|7|NOT NULL",
            },
            SqlServerCatalogSchemaExpectations.TimeColumnsFor(4)
                .Except(SqlServerCatalogSchemaExpectations.TimeColumnsFor(3))
                .Order(StringComparer.Ordinal));
    }

    /// <summary>One text table's CREATE TABLE and its standalone indexes, in script order.</summary>
    static IEnumerable<string> TextTableStatements(string script, string table)
    {
        int start = script.IndexOf("CREATE TABLE dbo." + table + " (\n", StringComparison.Ordinal);
        Assert.True(start >= 0, table + " is not created.");
        int end = script.IndexOf(");\n", start, StringComparison.Ordinal);
        yield return script[start..(end + 2)];
        foreach (string line in script.Split('\n').Where(line => line.Contains(" INDEX ", StringComparison.Ordinal)
            && line.Contains(" ON dbo." + table + "(", StringComparison.Ordinal)))
        {
            yield return line;
        }
    }

    static int Occurrences(string text, string value)
    {
        int count = 0;
        for (int index = text.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(value, index + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
