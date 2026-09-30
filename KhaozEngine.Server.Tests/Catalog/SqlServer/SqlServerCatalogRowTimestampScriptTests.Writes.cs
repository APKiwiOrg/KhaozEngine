using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using KhaozEngine.Catalog.SqlServer;
using Xunit;

namespace KhaozEngine.Tests.Catalog.SqlServer;

/// <summary>
/// The provider's write statements read from its source, the way <c>SqlServerCatalogParameterTypeTests</c>
/// reads them: every insert names its table's creation column, every update sets its table's update column and
/// never the creation column, and no <c>MERGE</c> rewrites a row it has not first compared.
/// </summary>
public sealed partial class SqlServerCatalogRowTimestampScriptTests
{
    /// <summary>Every catalog table, the column that holds its creation time and the one that holds its update time.</summary>
    static readonly Dictionary<string, (string Created, string? Updated)> Stamps = new(StringComparer.Ordinal)
    {
        ["catalog_audit"] = ("occurred_at_utc", null),
        ["catalog_chunk"] = ("created_at_utc", null),
        ["catalog_content_upgrade"] = ("recorded_at_utc", null),
        ["catalog_draft"] = ("opened_at_utc", "updated_at_utc"),
        ["catalog_draft_edit"] = ("created_at_utc", "edited_at_utc"),
        ["catalog_draft_edit_field"] = ("created_at_utc", null),
        ["catalog_family"] = ("created_at_utc", null),
        ["catalog_family_block"] = ("created_at_utc", "updated_at_utc"),
        ["catalog_id_high_water"] = ("created_at_utc", "updated_at_utc"),
        ["catalog_metadata"] = ("created_at_utc", "updated_at_utc"),
        ["catalog_remap_rule"] = ("created_at_utc", null),
        ["catalog_row"] = ("created_at_utc", "updated_at_utc"),
        ["catalog_row_field"] = ("created_at_utc", null),
        ["catalog_type"] = ("created_at_utc", "updated_at_utc"),
        ["catalog_version"] = ("published_at_utc", null),
    };

    [Fact]
    public void Every_insert_stamps_its_creation_time_and_every_update_its_update_time()
    {
        Assert.Equal(
            Ordered(SqlServerCatalogSchemaExpectations.Tables),
            Ordered(Stamps.Keys.ToHashSet(StringComparer.Ordinal)));

        var inserted = new HashSet<string>(StringComparer.Ordinal);
        var updated = new HashSet<string>(StringComparer.Ordinal);
        var offenders = new List<string>();
        foreach ((string file, string sql) in WriteStatements())
        {
            Match merge = MergeInto().Match(sql);
            foreach (Match insert in InsertColumns().Matches(sql))
            {
                string table = insert.Groups[1].Success ? insert.Groups[1].Value : merge.Groups[1].Value;
                inserted.Add(table);
                string[] columns = insert.Groups[2].Value.Split(',').Select(static part => part.Trim()).ToArray();
                (string created, string? changed) = Stamps[table];
                if (!columns.Contains(created, StringComparer.Ordinal)
                    || (changed is not null && !columns.Contains(changed, StringComparer.Ordinal)))
                {
                    offenders.Add($"{file}: an insert into {table} does not stamp {created}{(changed is null ? string.Empty : " and " + changed)}");
                }
            }

            foreach (Match update in UpdateSet().Matches(sql))
            {
                string table = update.Groups[1].Success ? update.Groups[1].Value : merge.Groups[1].Value;
                updated.Add(table);
                string assignments = update.Groups[2].Value;
                (string created, string? changed) = Stamps[table];
                if (changed is null || !assignments.Contains(changed + " =", StringComparison.Ordinal))
                {
                    offenders.Add($"{file}: an update of {table} does not stamp {changed ?? "an update time"}");
                }

                if (assignments.Contains(created + " =", StringComparison.Ordinal))
                {
                    offenders.Add($"{file}: an update of {table} rewrites its creation time {created}");
                }
            }
        }

        Assert.True(offenders.Count == 0, string.Join("\n", offenders));

        // The scan saw the statements it is about: every table but the metadata row, which only the schema script
        // inserts, and exactly the tables whose rows change after insert.
        Assert.Equal(
            Ordered(Stamps.Keys.Where(static table => table != "catalog_metadata").ToHashSet(StringComparer.Ordinal)),
            Ordered(inserted));
        Assert.Equal(
            Ordered(Stamps.Where(static entry => entry.Value.Updated is not null).Select(static entry => entry.Key).ToHashSet(StringComparer.Ordinal)),
            Ordered(updated));
    }

    [Fact]
    public void No_merge_updates_a_row_it_has_not_compared()
    {
        string[] offenders = WriteStatements()
            .Where(static entry => UnguardedMatch().IsMatch(entry.Sql))
            .Select(static entry => entry.File + ": " + Normalize(entry.Sql))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "A MERGE that updates a matched row without comparing it first moves the update time on a write that "
            + "changes nothing.\n  " + string.Join("\n  ", offenders));
        Assert.True(WriteStatements().Count(static entry => MergeInto().IsMatch(entry.Sql)) >= 5);
    }

    /// <summary>
    /// Every statement the store and the reset hold that writes a table: each raw string literal, and each one
    /// line literal that reads like a write. The schema files are left out, because a migration fills legacy rows
    /// rather than writing new ones.
    /// </summary>
    static IReadOnlyList<(string File, string Sql)> WriteStatements()
    {
        var statements = new List<(string, string)>();
        IEnumerable<string> paths = Directory
            .EnumerateFiles(ProviderDirectory(), "*.cs", SearchOption.TopDirectoryOnly)
            .Where(static path => Path.GetFileName(path).StartsWith("SqlServerContentAuthoringStore", StringComparison.Ordinal)
                || Path.GetFileName(path) == "SqlServerCatalogReset.cs")
            .OrderBy(static path => path, StringComparer.Ordinal);
        foreach (string path in paths)
        {
            string source = File.ReadAllText(path);
            string file = Path.GetFileName(path);
            foreach (Match match in RawLiteral().Matches(source))
            {
                statements.Add((file, match.Groups[1].Value));
            }

            foreach (Match match in LineLiteral().Matches(RawLiteral().Replace(source, "\n")))
            {
                statements.Add((file, match.Groups[1].Value));
            }
        }

        return statements
            .Where(static entry => WriteVerb().IsMatch(entry.Item2))
            .ToArray();
    }

    /// <summary>The provider's directory, found from this file's own compile time path.</summary>
    static string ProviderDirectory([CallerFilePath] string thisFile = "")
    {
        string root = Path.GetDirectoryName(Path.GetDirectoryName(
            Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!)!)!;
        return Path.Combine(root, "KhaozEngine.Catalog.SqlServer");
    }

    [GeneratedRegex(@"""""""(.*?)""""""", RegexOptions.Singleline)]
    private static partial Regex RawLiteral();

    [GeneratedRegex(@"""([^""\n]*)""")]
    private static partial Regex LineLiteral();

    [GeneratedRegex(@"\b(INSERT|UPDATE|MERGE)\b")]
    private static partial Regex WriteVerb();

    [GeneratedRegex(@"\bMERGE\s+dbo\.(\w+)")]
    private static partial Regex MergeInto();

    /// <summary>A plain insert names its table, and a MERGE's insert arm writes the MERGE's table.</summary>
    [GeneratedRegex(@"\bINSERT(?:\s+INTO\s+dbo\.(\w+))?\s*\(([^)]*)\)")]
    private static partial Regex InsertColumns();

    /// <summary>A plain update names its table, and a MERGE's update arm writes the MERGE's table.</summary>
    [GeneratedRegex(@"\bUPDATE(?:\s+dbo\.(\w+))?\s+SET\s+(.*?)(?=\bWHERE\b|\bOUTPUT\b|\bWHEN\b\s+NOT|;|$)", RegexOptions.Singleline)]
    private static partial Regex UpdateSet();

    [GeneratedRegex(@"\bWHEN\s+MATCHED\s+THEN\b")]
    private static partial Regex UnguardedMatch();
}
