using System;
using System.Collections.Generic;
using System.Data;
using KhaozEngine.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Sqlite;

public sealed class SqliteSchemaWideningTests
{
    [Fact]
    public void Missing_table_is_created_in_the_callback_transaction()
    {
        using var file = new SqliteScratchFile("ke-schema-create-");
        using SqliteConnection connection = Open(file);
        SqliteSchemaWidening.Ensure(connection, Requirements(), transaction =>
        {
            using SqliteConnection competitor = Open(file);
            SqliteException error = Assert.Throws<SqliteException>(() => competitor.BeginTransaction(deferred: false));
            Assert.Equal(5, error.SqliteErrorCode);
            Execute(connection, transaction, "CREATE TABLE records (id TEXT PRIMARY KEY, created_at INTEGER NULL);");
        });

        Assert.Equal((1, "INTEGER", false), file.Column("records", "created_at"));
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public void Missing_column_is_added_without_inventing_legacy_times()
    {
        using var file = new SqliteScratchFile("ke-schema-widen-");
        file.Execute("CREATE TABLE records (id TEXT PRIMARY KEY); INSERT INTO records VALUES ('legacy');");
        using SqliteConnection connection = Open(file);
        SqliteSchemaWidening.Ensure(connection, Requirements(), transaction =>
            Execute(connection, transaction, "ALTER TABLE records ADD COLUMN created_at INTEGER NULL;"));

        Assert.Equal((1, "INTEGER", false), file.Column("records", "created_at"));
        Assert.Equal(((long?)null, (long?)null), file.ReadPair("SELECT created_at, created_at FROM records;"));
        SqliteSchemaWidening.Ensure(connection, Requirements(), _ => Assert.Fail("Complete schema must skip the callback."));
    }

    [Fact]
    public void Complete_schema_skips_the_callback_while_another_connection_holds_the_write_lock()
    {
        using var file = new SqliteScratchFile("ke-schema-complete-");
        file.Execute("CREATE TABLE records (id TEXT PRIMARY KEY, created_at INTEGER NULL);");
        using SqliteConnection writer = Open(file);
        using SqliteTransaction transaction = writer.BeginTransaction(deferred: false);
        using SqliteConnection connection = Open(file);

        SqliteSchemaWidening.Ensure(connection, Requirements(), _ => Assert.Fail("Complete schema must skip the callback."));
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public void Table_and_column_names_match_SQLite_case_insensitively()
    {
        using var file = new SqliteScratchFile("ke-schema-case-");
        file.Execute("CREATE TABLE Records (Id TEXT PRIMARY KEY, Created_At INTEGER NULL);");
        using SqliteConnection connection = Open(file);
        SqliteSchemaWidening.Ensure(connection, Requirements(), _ => Assert.Fail("Existing identifiers must match."));
    }

    [Fact]
    public void Missing_index_runs_the_callback_and_existing_index_matches_case_insensitively()
    {
        using var file = new SqliteScratchFile("ke-schema-index-");
        file.Execute("CREATE TABLE records (id TEXT PRIMARY KEY, created_at INTEGER NULL);");
        using SqliteConnection connection = Open(file);
        SqliteSchemaWidening.Ensure(connection, Requirements(), transaction =>
            Execute(connection, transaction, "CREATE UNIQUE INDEX ux_records ON records(id);"), new[] { "ux_records" });
        SqliteSchemaWidening.Ensure(connection, Requirements(), _ => Assert.Fail("Existing index must match."),
            new[] { "UX_RECORDS" });
    }

    [Fact]
    public void A_table_with_no_required_columns_still_has_to_exist()
    {
        using var file = new SqliteScratchFile("ke-schema-table-");
        using SqliteConnection connection = Open(file);
        var required = new Dictionary<string, string[]> { ["records"] = Array.Empty<string>() };
        SqliteSchemaWidening.Ensure(connection, required, transaction =>
            Execute(connection, transaction, "CREATE TABLE records (id TEXT PRIMARY KEY);"));
        Assert.Equal(1, file.Column("records", "id").Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("records; DROP TABLE records")]
    [InlineData("records\" quoted")]
    [InlineData("main.records")]
    [InlineData("1records")]
    public void Invalid_identifiers_are_rejected_before_running_the_callback(string identifier)
    {
        using var file = new SqliteScratchFile("ke-schema-name-");
        using SqliteConnection connection = Open(file);
        bool called = false;
        Assert.Throws<ArgumentException>(() => SqliteSchemaWidening.Ensure(connection,
            new Dictionary<string, string[]> { [identifier] = new[] { "created_at" } }, _ => called = true));
        Assert.Throws<ArgumentException>(() => SqliteSchemaWidening.Ensure(connection,
            new Dictionary<string, string[]> { ["records"] = new[] { identifier } }, _ => called = true));
        Assert.Throws<ArgumentException>(() => SqliteSchemaWidening.Ensure(connection, Requirements(),
            _ => called = true, new[] { identifier }));
        Assert.False(called);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public void Callback_failure_rolls_back_DDL_without_closing_the_connection()
    {
        using var file = new SqliteScratchFile("ke-schema-rollback-");
        file.Execute("CREATE TABLE records (id TEXT PRIMARY KEY);");
        using SqliteConnection connection = Open(file);
        Assert.Throws<InvalidOperationException>(() => SqliteSchemaWidening.Ensure(connection, Requirements(), transaction =>
        {
            Execute(connection, transaction, "ALTER TABLE records ADD COLUMN created_at INTEGER NULL;");
            throw new InvalidOperationException("Migration failed.");
        }));

        Assert.Equal(0, file.Column("records", "created_at").Count);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public void Callback_that_leaves_requirements_missing_rolls_back_its_changes()
    {
        using var file = new SqliteScratchFile("ke-schema-incomplete-");
        using SqliteConnection connection = Open(file);
        Assert.Throws<InvalidOperationException>(() => SqliteSchemaWidening.Ensure(connection, Requirements(), transaction =>
            Execute(connection, transaction, "CREATE TABLE records (id TEXT PRIMARY KEY);")));
        Assert.Equal(0, file.Column("records", "id").Count);
    }

    internal static SqliteConnection Open(SqliteScratchFile file)
    {
        var connection = new SqliteConnection(file.ConnectionString + ";Pooling=False;Default Timeout=1");
        connection.Open();
        return connection;
    }

    internal static Dictionary<string, string[]> Requirements() => new()
    {
        ["records"] = new[] { "id", "created_at" },
    };

    internal static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
