using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using KhaozEngine.Catalog.SqlServer;
using Xunit;

namespace KhaozEngine.Tests.Catalog.SqlServer;

/// <summary>
/// Offline pins on schema version 3 of the SQL Server content catalog: the embedded scripts, the version 2 to 3
/// migration statements, the row time columns each version validates against, and the write statements in the
/// provider's source. They need no SQL Server, so they run where the live facts in
/// <see cref="SqlServerCatalogRowTimestampTests"/> skip.
/// </summary>
public sealed partial class SqlServerCatalogRowTimestampScriptTests
{
    /// <summary>How version 3 declares every column it adds.</summary>
    const string AddedShape = " datetimeoffset(7) NULL,";

    /// <summary>The metadata seed version 3 ends with, both times from one database clock reading.</summary>
    const string VersionThreeSeed = """
        DECLARE @createdAtUtc datetimeoffset(7) = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00');
        IF NOT EXISTS (SELECT 1 FROM dbo.catalog_metadata WHERE metadata_key = 1)
        INSERT INTO dbo.catalog_metadata(
            metadata_key, schema_version, store_epoch, active_version, pinned_version, updated_at_utc, created_at_utc)
        VALUES (1, 3, LOWER(REPLACE(CONVERT(nvarchar(36), NEWID()), '-', '')), 0, NULL,
                @createdAtUtc, @createdAtUtc);
        """;

    /// <summary>The metadata seed exactly as version 2 shipped it.</summary>
    const string VersionTwoSeed = """
        IF NOT EXISTS (SELECT 1 FROM dbo.catalog_metadata WHERE metadata_key = 1)
        INSERT INTO dbo.catalog_metadata(
            metadata_key, schema_version, store_epoch, active_version, pinned_version, updated_at_utc)
        VALUES (1, 2, LOWER(REPLACE(CONVERT(nvarchar(36), NEWID()), '-', '')), 0, NULL,
                TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'));
        """;

    /// <summary>Every column version 3 adds, in the order the migration adds them.</summary>
    static readonly (string Table, string Column)[] NewColumns =
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

    [Fact]
    public void Version_three_names_its_migration()
    {
        Assert.Equal(3, SqlServerCatalogSchema.CurrentVersion);
        Assert.Equal("catalog-v3-row-timestamps", SqlServerCatalogSchema.RequiredMigration);
        Assert.Equal(NewColumns, SqlServerCatalogSchema.VersionThreeColumns);
    }

    [Fact]
    public void Version_three_script_is_version_two_with_sixteen_nullable_time_columns()
    {
        string versionThree = SqlServerCatalogSchema.SchemaSql.ReplaceLineEndings("\n");
        string versionTwo = SqlServerCatalogSchema.VersionTwoSchemaSql.ReplaceLineEndings("\n");

        foreach ((string table, string column) in NewColumns)
        {
            Assert.Contains("\n    " + column + AddedShape + "\n", TableBlock(versionThree, table), StringComparison.Ordinal);
        }

        Assert.Equal(NewColumns.Length, Occurrences(versionThree, AddedShape));
        Assert.EndsWith(VersionThreeSeed.ReplaceLineEndings("\n") + "\n", versionThree, StringComparison.Ordinal);

        string derived = versionThree
            .Replace("\n    created_at_utc" + AddedShape, string.Empty, StringComparison.Ordinal)
            .Replace("\n    updated_at_utc" + AddedShape, string.Empty, StringComparison.Ordinal)
            .Replace(
                VersionThreeSeed.ReplaceLineEndings("\n"),
                VersionTwoSeed.ReplaceLineEndings("\n"),
                StringComparison.Ordinal);
        Assert.Equal(versionTwo, derived);
    }

    [Fact]
    public void Each_embedded_script_declares_exactly_the_time_columns_its_version_validates()
    {
        Assert.Equal(
            Ordered(SqlServerCatalogSchemaExpectations.TimeColumnsFor(3)),
            Ordered(DeclaredTimeColumns(SqlServerCatalogSchema.SchemaSql)));
        Assert.Equal(
            Ordered(SqlServerCatalogSchemaExpectations.TimeColumnsFor(2)),
            Ordered(DeclaredTimeColumns(SqlServerCatalogSchema.VersionTwoSchemaSql)));
        Assert.Equal(
            Ordered(SqlServerCatalogSchemaExpectations.TimeColumnsFor(1)),
            Ordered(DeclaredTimeColumns(SqlServerCatalogSchema.VersionOneSchemaSql)));

        // Version 3 adds exactly the migration's columns, and every one of them nullable.
        Assert.Equal(
            Ordered(NewColumns.Select(static value => $"{value.Table}.{value.Column}|datetimeoffset|7|NULL").ToHashSet()),
            Ordered(SqlServerCatalogSchemaExpectations.TimeColumnsFor(3)
                .Except(SqlServerCatalogSchemaExpectations.TimeColumnsFor(2))
                .ToHashSet()));
    }

    [Fact]
    public void Version_two_migration_adds_each_missing_column_then_backfills_then_moves_the_version_last()
    {
        IReadOnlyList<string> commands = SqlServerCatalogSchema.VersionTwoMigrationSql;
        string[] responsibilities =
        [
            .. NewColumns.Select(static value => $"ALTER TABLE dbo.{value.Table} ADD {value.Column} datetimeoffset(7) NULL;"),
            "FROM dbo.catalog_row r",
            "FROM dbo.catalog_row_field f",
            "FROM dbo.catalog_chunk c",
            "FROM dbo.catalog_remap_rule r",
            "SET schema_version = 3",
        ];

        Assert.Equal(responsibilities.Length, commands.Count);
        for (int commandIndex = 0; commandIndex < commands.Count; commandIndex++)
        {
            for (int responsibilityIndex = 0; responsibilityIndex < responsibilities.Length; responsibilityIndex++)
            {
                Action<string, string, StringComparison> assertion = commandIndex == responsibilityIndex
                    ? Assert.Contains
                    : Assert.DoesNotContain;
                assertion(responsibilities[responsibilityIndex], commands[commandIndex], StringComparison.Ordinal);
            }
        }

        for (int index = 0; index < NewColumns.Length; index++)
        {
            (string table, string column) = NewColumns[index];
            Assert.StartsWith(
                $"IF COL_LENGTH(N'dbo.{table}', N'{column}') IS NULL",
                commands[index],
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Version_two_backfill_takes_only_proven_times()
    {
        IReadOnlyList<string> commands = SqlServerCatalogSchema.VersionTwoMigrationSql;
        int first = NewColumns.Length;

        Assert.Equal(
            "UPDATE r SET created_at_utc = created.published_at_utc, updated_at_utc = changed.published_at_utc "
            + "FROM dbo.catalog_row r "
            + "LEFT JOIN dbo.catalog_version created ON created.version_number = r.valid_from_version "
            + "LEFT JOIN dbo.catalog_version changed "
            + "ON changed.version_number = COALESCE(r.replaced_in_version, r.valid_from_version);",
            Normalize(commands[first]));
        Assert.Equal(
            "UPDATE f SET created_at_utc = v.published_at_utc FROM dbo.catalog_row_field f "
            + "LEFT JOIN dbo.catalog_version v ON v.version_number = f.valid_from_version;",
            Normalize(commands[first + 1]));
        Assert.Equal(
            "UPDATE c SET created_at_utc = v.published_at_utc FROM dbo.catalog_chunk c "
            + "LEFT JOIN dbo.catalog_version v ON v.version_number = c.version_number;",
            Normalize(commands[first + 2]));
        Assert.Equal(
            "UPDATE r SET created_at_utc = v.published_at_utc FROM dbo.catalog_remap_rule r "
            + "LEFT JOIN dbo.catalog_version v ON v.version_number = r.introduced_in;",
            Normalize(commands[first + 3]));

        string version = Normalize(commands[first + 4]);
        Assert.Contains("WHERE metadata_key = 1 AND schema_version = 2;", version, StringComparison.Ordinal);
        Assert.DoesNotContain("created_at_utc", version, StringComparison.Ordinal);
    }

    [Fact]
    public void Version_two_validation_accepts_each_table_with_or_without_its_version_three_column()
    {
        IReadOnlySet<string> three = SqlServerCatalogSchemaExpectations.TimeColumnsFor(3);
        IReadOnlySet<string> two = SqlServerCatalogSchemaExpectations.TimeColumnsFor(2);
        IReadOnlySet<string> one = SqlServerCatalogSchemaExpectations.TimeColumnsFor(1);
        string[] added = NewColumns.Select(static value => $"{value.Table}.{value.Column}|datetimeoffset|7|NULL").ToArray();

        for (int count = 0; count <= added.Length; count++)
        {
            Assert.True(
                SqlServerCatalogSchemaExpectations.TimeColumnsMatch(With(two, added.Take(count)), 2),
                $"{count} columns added");
        }

        Assert.True(SqlServerCatalogSchemaExpectations.TimeColumnsMatch(With(two, added.Skip(7)), 2));
        Assert.True(SqlServerCatalogSchemaExpectations.TimeColumnsMatch(With(three), 3));
        Assert.True(SqlServerCatalogSchemaExpectations.TimeColumnsMatch(With(one), 1));

        Assert.False(SqlServerCatalogSchemaExpectations.TimeColumnsMatch(With(two), 3));
        Assert.False(SqlServerCatalogSchemaExpectations.TimeColumnsMatch(With(three.Skip(1)), 3));
        Assert.False(SqlServerCatalogSchemaExpectations.TimeColumnsMatch(
            With(two, [added[0].Replace("|NULL", "|NOT NULL", StringComparison.Ordinal)]), 2));
        Assert.False(SqlServerCatalogSchemaExpectations.TimeColumnsMatch(
            With(two, [added[0].Replace("|7|", "|3|", StringComparison.Ordinal)]), 2));
        Assert.False(SqlServerCatalogSchemaExpectations.TimeColumnsMatch(
            With(two, ["catalog_version.created_at_utc|datetimeoffset|7|NULL"]), 2));
        Assert.False(SqlServerCatalogSchemaExpectations.TimeColumnsMatch(With(one, added.Take(1)), 1));
    }

    /// <summary>
    /// The row time columns a script declares, in the shape validation reads them back in: every
    /// <c>created_at_utc</c> and <c>updated_at_utc</c> line inside a <c>CREATE TABLE</c>.
    /// </summary>
    static HashSet<string> DeclaredTimeColumns(string sql)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        string? table = null;
        foreach (string raw in sql.ReplaceLineEndings("\n").Split('\n'))
        {
            Match created = CreateTable().Match(raw);
            if (created.Success)
            {
                table = created.Groups[1].Value;
                continue;
            }

            Match column = TimeColumnLine().Match(raw);
            if (table is not null && column.Success)
            {
                found.Add(FormattableString.Invariant(
                    $"{table}.{column.Groups[1].Value}|datetimeoffset|{column.Groups[2].Value}|{column.Groups[3].Value}"));
            }
        }

        return found;
    }

    /// <summary>The <c>CREATE TABLE</c> statement for <paramref name="table"/>, through its closing <c>);</c>.</summary>
    static string TableBlock(string sql, string table)
    {
        string header = $"CREATE TABLE dbo.{table} (\n";
        int start = sql.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{table} is not created.");
        int end = sql.IndexOf(");\n", start, StringComparison.Ordinal);
        return sql[start..end];
    }

    static HashSet<string> With(IEnumerable<string> source, IEnumerable<string>? more = null)
    {
        var result = new HashSet<string>(source, StringComparer.Ordinal);
        if (more is not null)
        {
            result.UnionWith(more);
        }

        return result;
    }

    static List<string> Ordered(IReadOnlySet<string> names)
    {
        var sorted = new List<string>(names);
        sorted.Sort(StringComparer.Ordinal);
        return sorted;
    }

    static string Normalize(string sql) => Whitespace().Replace(sql, " ").Trim();

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

    [GeneratedRegex(@"^CREATE TABLE dbo\.(\w+)")]
    private static partial Regex CreateTable();

    [GeneratedRegex(@"^    (created_at_utc|updated_at_utc) datetimeoffset\((\d)\) (NOT NULL|NULL)\b")]
    private static partial Regex TimeColumnLine();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
