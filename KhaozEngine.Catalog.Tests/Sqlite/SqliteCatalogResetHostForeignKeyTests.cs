using System;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Sqlite;

/// <summary>
/// A HOST table whose foreign key points into a catalog table, which SQLite lets a host declare.
/// <para>
/// <b>SQLite's DROP TABLE is not a plain drop.</b> With foreign keys on it deletes every row of the table
/// first, and that hidden delete fires the delete action of every key pointing at it from another table. A
/// host key declared <c>ON DELETE CASCADE</c> loses its rows, and <c>SET NULL</c> or <c>SET DEFAULT</c> has
/// its column rewritten, and none of that is a violation the commit would catch. So the reset reads every
/// host key before it drops anything and refuses when one of them would fire.
/// </para>
/// <para>
/// A <c>NO ACTION</c> or <c>RESTRICT</c> key fires nothing. It is a violation instead, and the reset fails and
/// rolls back as a whole, which <c>SqliteCatalogResetTests</c> holds.
/// </para>
/// </summary>
public class SqliteCatalogResetHostForeignKeyTests
{
    const string Actor = SqliteCatalogResetHarness.Actor;
    const string Operator = SqliteCatalogResetHarness.Operator;

    /// <summary>Every host row in id order, each column quoted, so a rewritten value reads as a different string.</summary>
    const string HostRowsSql = """
        SELECT COALESCE(group_concat(quote(id) || ',' || quote(version_number) || ',' || quote(body), ';'), '')
        FROM (SELECT id, version_number, body FROM host_notes ORDER BY id);
        """;

    [Theory]
    [InlineData("CASCADE")]
    [InlineData("SET NULL")]
    [InlineData("SET DEFAULT")]
    public async Task AHostKeyWhoseDeleteActionWouldFireIsRefusedAndTheHostRowsStandUnchanged(string action)
    {
        using var database = new TemporaryCatalogDatabase();
        using (var store = new SqliteContentAuthoringStore(
            database.ConnectionString, SqliteCatalogResetHarness.Registry(), database.Pack()))
        {
            await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
            await SqliteCatalogResetHarness.Seed(store, "one", "two");
        }

        string epochBefore = SqliteCatalogResetHarness.Text(database, "SELECT store_epoch FROM catalog_metadata;");
        long auditsBefore = database.Scalar("SELECT COUNT(*) FROM catalog_audit;");

        // The column is nullable with no default, so SET DEFAULT writes NULL, which is a value the commit
        // accepts. That is the shape that used to pass the reset with the host's rows rewritten.
        database.Execute(FormattableString.Invariant(
            $"""
            CREATE TABLE host_notes(
                id INTEGER NOT NULL PRIMARY KEY,
                version_number INTEGER NULL REFERENCES catalog_version(version_number) ON DELETE {action},
                body TEXT NOT NULL);
            INSERT INTO host_notes(id, version_number, body) VALUES (1, 1, 'first'), (2, 1, 'second');
            """));
        string hostBefore = SqliteCatalogResetHarness.Text(database, HostRowsSql);
        Assert.Equal("1,1,'first';2,1,'second'", hostBefore);

        Exception? thrown = await Record.ExceptionAsync(
            () => SqliteCatalogReset.ResetAsync(database.ConnectionString, Actor, Operator, "content release"));

        // The host's rows first, because they are what the refusal exists to protect.
        Assert.Equal(hostBefore, SqliteCatalogResetHarness.Text(database, HostRowsSql));

        ContentAuthoringException refused = Assert.IsType<ContentAuthoringException>(thrown);
        Assert.Equal(ContentAuthoringException.HostForeignKeyReason, refused.Reason);
        Assert.Contains("'host_notes'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("'catalog_version'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("ON DELETE " + action, refused.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was dropped", refused.Message, StringComparison.Ordinal);

        // And the catalog is exactly the one that stood, down to the epoch and the audit trail.
        Assert.Equal(epochBefore, SqliteCatalogResetHarness.Text(database, "SELECT store_epoch FROM catalog_metadata;"));
        Assert.Equal(1L, database.Scalar("SELECT active_version FROM catalog_metadata;"));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM catalog_version;"));
        Assert.Equal(2L, database.Scalar("SELECT COUNT(*) FROM catalog_row;"));
        Assert.Equal(auditsBefore, database.Scalar("SELECT COUNT(*) FROM catalog_audit;"));

        using var reopened = new SqliteContentAuthoringStore(
            database.ConnectionString, SqliteCatalogResetHarness.Registry(), database.Pack());
        await reopened.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
        Assert.Equal(1, await reopened.GetActiveVersionAsync());
    }
}
