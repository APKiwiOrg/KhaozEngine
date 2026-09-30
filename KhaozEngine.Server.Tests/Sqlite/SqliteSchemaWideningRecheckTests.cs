using System;
using KhaozEngine.Sqlite;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using Xunit;

namespace KhaozEngine.Tests.Sqlite;

public sealed class SqliteSchemaWideningRecheckTests
{
    [Fact]
    public void Schema_completed_after_preflight_is_rechecked_under_the_write_lock()
    {
        using var file = new SqliteScratchFile("ke-schema-recheck-");
        file.Execute("CREATE TABLE records (id TEXT PRIMARY KEY);");
        using SqliteConnection writer = SqliteSchemaWideningTests.Open(file);
        using SqliteTransaction migration = writer.BeginTransaction(deferred: false);
        using SqliteConnection connection = SqliteSchemaWideningTests.Open(file);
        bool committed = false;
        Exception? traceFailure = null;

        // Complete the other migration after preflight and before this connection acquires the lock.
        raw.sqlite3_trace(connection.Handle!, (strdelegate_trace)((_, sql) =>
        {
            if (committed || !sql.StartsWith("BEGIN IMMEDIATE", StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                SqliteSchemaWideningTests.Execute(writer, migration,
                    "ALTER TABLE records ADD COLUMN created_at INTEGER NULL;");
                migration.Commit();
                committed = true;
            }
            catch (Exception error)
            {
                traceFailure = error;
            }
        }), null!);

        SqliteSchemaWidening.Ensure(connection, SqliteSchemaWideningTests.Requirements(),
            _ => Assert.Fail("The locked recheck must see the completed migration."));

        Assert.Null(traceFailure);
        Assert.True(committed);
        Assert.Equal(1, file.Column("records", "created_at").Count);
    }
}
