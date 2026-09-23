using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.Tests.Catalog.Publish;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Tests.Catalog.Sqlite;

/// <summary>
/// What every SQLite reset class needs and none of them should carry twice: the actor the audit row is
/// written under, the one seeded type, and the raw reads a test asserts a dropped or surviving table with.
/// </summary>
internal static class SqliteCatalogResetHarness
{
    /// <summary>The authenticated actor every reset in these suites is taken under.</summary>
    internal const string Actor = "sqlite-reset-tests";

    /// <summary>The console-asserted operator identity.</summary>
    internal const string Operator = "oid:tests";

    /// <summary>The one content type these suites publish rows of.</summary>
    internal static ContentTypeId Thing => new(PublishFixtures.ThingTypeId);

    /// <summary>A registry carrying <see cref="Thing"/> and nothing else.</summary>
    internal static ContentTypeRegistry Registry() => PublishFixtures.Registry(PublishFixtures.Thing);

    /// <summary>A publish request expecting the named base version.</summary>
    internal static ContentPublishRequest Request(int expectedBaseVersion)
        => new(Actor, Operator, "sqlite reset tests", expectedBaseVersion);

    /// <summary>The named rows added and published, which is the smallest store with a version and an audit trail.</summary>
    internal static async Task Seed(SqliteContentAuthoringStore store, params string[] keys)
    {
        var edits = new List<ContentEdit>(keys.Length);
        for (int i = 0; i < keys.Length; i++)
        {
            edits.Add(ContentEdit.Add(Thing, new ContentKey(keys[i]), PublishFixtures.Fields(11 * (i + 1))));
        }

        await store.ApplyEditsAsync(edits, Actor, Operator, "seed");
        await store.PublishAsync(Request(0));
    }

    /// <summary>One text scalar on a raw connection, beside the fixture's numeric one.</summary>
    internal static string Text(TemporaryCatalogDatabase database, string sql)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        string value;
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = sql;
            value = command.ExecuteScalar() as string ?? string.Empty;
        }

        SqliteConnection.ClearPool(connection);
        return value;
    }

    /// <summary>
    /// Every table the file holds whose name this schema declares, read through the provider's OWN
    /// inventory rather than a pattern written here, so a test that says "the schema's own rule" uses it.
    /// </summary>
    internal static IReadOnlyList<string> Tables(TemporaryCatalogDatabase database)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        IReadOnlyList<string> names = SqliteCatalogSchemaInventory.ReadExisting(connection, null);
        SqliteConnection.ClearPool(connection);
        return names;
    }

    /// <summary>Every table the file holds, catalog or not, which is how a host table is proved present.</summary>
    internal static IReadOnlyList<string> AllTables(TemporaryCatalogDatabase database)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        var names = new List<string>();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                names.Add(reader.GetString(0));
            }
        }

        SqliteConnection.ClearPool(connection);
        return names;
    }
}
