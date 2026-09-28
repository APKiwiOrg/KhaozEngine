using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

/// <summary>
/// Offline pins on schema version 3 of the SQL Server journal: the embedded script, the version 2 to 3 migration
/// statements, the column set each version validates against, and the creation times the write batches bind. They
/// need no SQL Server, so they run where the live facts in <see cref="SqlServerJournalRowTimestampTests"/> skip.
/// </summary>
public sealed class SqlServerJournalRowTimestampScriptTests
{
    private const string CreationColumn = "created_at_utc datetimeoffset(7) NULL,";

    /// <summary>The four tables version 3 gives a <c>created_at_utc</c> column.</summary>
    private static readonly string[] CreationColumnTables =
    {
        "journal_metadata", "journal_stream", "journal_operation_stream", "journal_projection",
    };

    [Fact]
    public void Version_three_names_its_migration()
    {
        Assert.Equal(3, SqlServerJournalSchema.CurrentVersion);
        Assert.Equal("sqlserver-journal-v3-row-timestamps", SqlServerJournalSchema.RequiredMigration);
    }

    [Fact]
    public void Version_three_script_is_version_two_with_four_nullable_creation_columns()
    {
        string versionThree = SqlServerMutationJournalStore.SchemaSqlForTest.ReplaceLineEndings("\n");
        string versionTwo = SqlServerMutationJournalStore.VersionTwoSchemaSqlForTest.ReplaceLineEndings("\n");

        foreach (string table in CreationColumnTables)
            Assert.Contains(CreationColumn, TableBlock(versionThree, table), StringComparison.Ordinal);
        Assert.Equal(CreationColumnTables.Length, Occurrences(versionThree, CreationColumn));
        Assert.Contains(
            "INSERT INTO dbo.journal_metadata(metadata_key, schema_version, store_epoch, updated_at_utc, created_at_utc)\n" +
            "VALUES (1, 3, NEWID(), @createdAtUtc, @createdAtUtc);",
            versionThree,
            StringComparison.Ordinal);

        string derived = versionThree
            .Replace("\n    " + CreationColumn, string.Empty, StringComparison.Ordinal)
            .Replace(
                "DECLARE @createdAtUtc datetimeoffset(7) = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00');\n" +
                "INSERT INTO dbo.journal_metadata(metadata_key, schema_version, store_epoch, updated_at_utc, created_at_utc)\n" +
                "VALUES (1, 3, NEWID(), @createdAtUtc, @createdAtUtc);",
                "INSERT INTO dbo.journal_metadata(metadata_key, schema_version, store_epoch, updated_at_utc)\n" +
                "VALUES (1, 2, NEWID(), SYSUTCDATETIME());",
                StringComparison.Ordinal);
        Assert.Equal(versionTwo, derived);
    }

    [Fact]
    public void Version_two_migration_adds_each_missing_column_then_backfills_then_moves_the_version_last()
    {
        IReadOnlyList<string> commands = SqlServerMutationJournalStore.VersionTwoMigrationSqlForTest;
        string[] responsibilities =
        {
            "ALTER TABLE dbo.journal_metadata ADD created_at_utc datetimeoffset(7) NULL",
            "ALTER TABLE dbo.journal_stream ADD created_at_utc datetimeoffset(7) NULL",
            "ALTER TABLE dbo.journal_operation_stream ADD created_at_utc datetimeoffset(7) NULL",
            "ALTER TABLE dbo.journal_projection ADD created_at_utc datetimeoffset(7) NULL",
            "FROM dbo.journal_stream st",
            "FROM dbo.journal_operation_stream os",
            "SET schema_version = 3",
        };

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
        for (int index = 0; index < CreationColumnTables.Length; index++)
            Assert.StartsWith(
                $"IF COL_LENGTH(N'dbo.{CreationColumnTables[index]}', N'created_at_utc') IS NULL",
                commands[index],
                StringComparison.Ordinal);
    }

    [Fact]
    public void Version_two_backfill_takes_only_proven_times()
    {
        IReadOnlyList<string> commands = SqlServerMutationJournalStore.VersionTwoMigrationSqlForTest;
        string stream = Normalize(commands[4]);
        string receipts = Normalize(commands[5]);
        string version = Normalize(commands[6]);

        Assert.Equal(
            "UPDATE st SET created_at_utc = sn.created_at_utc FROM dbo.journal_stream st " +
            "LEFT JOIN dbo.journal_snapshot sn ON sn.stream_key = st.stream_key AND sn.through_version = 0;",
            stream);
        Assert.Equal(
            "UPDATE os SET created_at_utc = op.committed_at_utc FROM dbo.journal_operation_stream os " +
            "JOIN dbo.journal_operation op ON op.operation_id = os.operation_id;",
            receipts);
        Assert.Contains("WHERE metadata_key = 1 AND schema_version = 2;", version, StringComparison.Ordinal);
        Assert.DoesNotContain("created_at_utc", version, StringComparison.Ordinal);
    }

    [Fact]
    public void Earlier_column_sets_derive_from_version_three()
    {
        IReadOnlySet<string> three = SqlServerJournalSchema.ExpectedColumnsFor(3);
        IReadOnlySet<string> two = SqlServerJournalSchema.ExpectedColumnsFor(2);
        IReadOnlySet<string> one = SqlServerJournalSchema.ExpectedColumnsFor(1);

        Assert.True(two.IsProperSubsetOf(three));
        Assert.True(one.IsProperSubsetOf(two));
        Assert.Equal(
            CreationColumnTables.Select(table => $"{table}.created_at_utc|datetimeoffset|10|True|").OrderBy(value => value, StringComparer.Ordinal),
            three.Except(two).OrderBy(value => value, StringComparer.Ordinal));
        Assert.Equal(
            new[] { "journal_operation.retention_started_at_utc|datetimeoffset|10|False|" },
            two.Except(one));
    }

    [Fact]
    public void Version_two_validation_accepts_each_table_with_or_without_its_version_three_column()
    {
        IReadOnlySet<string> three = SqlServerJournalSchema.ExpectedColumnsFor(3);
        IReadOnlySet<string> two = SqlServerJournalSchema.ExpectedColumnsFor(2);
        string[] added = three.Except(two).OrderBy(value => value, StringComparer.Ordinal).ToArray();

        for (int count = 0; count <= added.Length; count++)
            Assert.True(SqlServerJournalSchema.ColumnsMatch(With(two, added.Take(count)), 2), $"{count} columns added");
        Assert.True(SqlServerJournalSchema.ColumnsMatch(With(two, added.Skip(2)), 2));
        Assert.True(SqlServerJournalSchema.ColumnsMatch(With(three), 3));

        Assert.False(SqlServerJournalSchema.ColumnsMatch(With(two), 3));
        Assert.False(SqlServerJournalSchema.ColumnsMatch(With(three.Take(three.Count - 1)), 3));
        Assert.False(SqlServerJournalSchema.ColumnsMatch(
            With(two, new[] { added[0].Replace("|True|", "|False|", StringComparison.Ordinal) }),
            2));
        Assert.False(SqlServerJournalSchema.ColumnsMatch(With(two, new[] { "journal_event.created_at_utc|datetimeoffset|10|True|" }), 2));
        Assert.False(SqlServerJournalSchema.ColumnsMatch(With(SqlServerJournalSchema.ExpectedColumnsFor(1)), 2));
        Assert.False(SqlServerJournalSchema.ColumnsMatch(With(SqlServerJournalSchema.ExpectedColumnsFor(1), added), 1));
    }

    [Fact]
    public void Initialization_batch_stamps_the_stream_projection_and_receipt_range_with_now()
    {
        using SqlCommand command = SqlServerMutationJournalStore.BuildInitializeBatchForTest(new JournalInitialization(
            Identity(9),
            "player/a",
            "player.v1",
            1,
            new byte[] { 1 },
            new[] { Projection("player/a", "bag", 2) },
            "result.v1",
            1,
            new byte[] { 3 }));
        string text = Normalize(command.CommandText);

        Assert.Contains(
            "INSERT INTO dbo.journal_stream(stream_key, current_version, retained_floor, updated_at_utc, created_at_utc) " +
            "VALUES (@stream0, 0, 0, @now, @now);",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "INSERT INTO dbo.journal_operation_stream(operation_id, stream_key, before_version, after_version, event_count, created_at_utc) " +
            "VALUES (@operation, @stream0, 0, 0, 0, @now);",
            text,
            StringComparison.Ordinal);
        AssertProjectionArms(text, 1);
        Assert.DoesNotContain("created_at_utc =", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Commit_batch_sets_a_creation_time_only_where_it_inserts()
    {
        using SqlCommand command = SqlServerMutationJournalStore.BuildCommitBatchForTest(
            new JournalCommit(
                Identity(1),
                new[]
                {
                    new JournalStreamMutation("player/a", 4, new[] { new JournalEvent("state.changed", 1, new byte[] { 1 }) }),
                    new JournalStreamMutation("player/b", 2, Array.Empty<JournalEvent>()),
                },
                new[] { Projection("player/a", "bag", 7), Projection("player/a", "skills", 8) },
                "result.v1",
                1,
                new byte[] { 5 }),
            JournalLimits.Maximum);
        string text = Normalize(command.CommandText);

        Assert.Contains(
            "INSERT INTO dbo.journal_operation_stream(operation_id, stream_key, before_version, after_version, event_count, created_at_utc) " +
            "VALUES (@operation, @stream0, @expected0, @expected0 + 1, 1, @now), (@operation, @stream1, @expected1, @expected1, 0, @now);",
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT INTO dbo.journal_stream(", text, StringComparison.Ordinal);
        AssertProjectionArms(text, 2);
        Assert.DoesNotContain("created_at_utc =", text, StringComparison.Ordinal);
        Assert.Equal(3, Occurrences(text, "created_at_utc"));
    }

    /// <summary>
    /// Each projection write is an UPDATE arm that must leave <c>created_at_utc</c> alone and an INSERT arm, run only
    /// when the UPDATE found no row, that stamps it with the statement's <c>@now</c>.
    /// </summary>
    private static void AssertProjectionArms(string text, int expected)
    {
        string[] writes = text.Split("UPDATE dbo.journal_projection ", StringSplitOptions.None)[1..];
        Assert.Equal(expected, writes.Length);
        foreach (string write in writes)
        {
            int insert = write.IndexOf("IF @@ROWCOUNT = 0", StringComparison.Ordinal);
            Assert.True(insert > 0, "A projection write has no INSERT arm.");
            string updateArm = write[..insert];
            string insertArm = write[insert..write.IndexOf(';', insert)];
            Assert.DoesNotContain("created_at_utc", updateArm, StringComparison.Ordinal);
            Assert.Contains("data_sha256, updated_at_utc, created_at_utc)", insertArm, StringComparison.Ordinal);
            Assert.EndsWith(", @now, @now)", insertArm, StringComparison.Ordinal);
        }
    }

    /// <summary>The <c>CREATE TABLE</c> statement for <paramref name="table"/>, through its closing <c>);</c>.</summary>
    private static string TableBlock(string sql, string table)
    {
        string header = $"CREATE TABLE dbo.{table} (\n";
        int start = sql.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{table} is not created.");
        int end = sql.IndexOf(");\n", start, StringComparison.Ordinal);
        return sql[start..end];
    }

    private static HashSet<string> With(IEnumerable<string> source, IEnumerable<string>? more = null)
    {
        var result = new HashSet<string>(source, StringComparer.Ordinal);
        if (more is not null) result.UnionWith(more);
        return result;
    }

    private static string Normalize(string sql) => Regex.Replace(sql, @"\s+", " ").Trim();

    private static int Occurrences(string text, string value)
    {
        int count = 0;
        for (int index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + 1, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static JournalOperationIdentity Identity(int suffix)
        => new(new Guid(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, checked((byte)suffix)), "world/account", "bank.deposit", new[] { checked((byte)suffix) });

    private static JournalProjectionWrite Projection(string streamKey, string section, byte value)
        => new(streamKey, section, "section.v1", 1, new[] { value });
}
