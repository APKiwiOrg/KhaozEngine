using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.SqlServer;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Tests.Catalog.SqlServer;

/// <summary>
/// What every SQL Server reset class needs and none of them should carry twice: the actor the audit row is
/// written under, the one seeded type, the open, and the raw reads a test asserts a dropped or surviving
/// table with.
/// </summary>
internal static class SqlServerCatalogResetHarness
{
    /// <summary>The authenticated actor every reset in these suites is taken under.</summary>
    internal const string Actor = "sqlserver-reset-tests";

    /// <summary>The console-asserted operator identity.</summary>
    internal const string Operator = "oid:tests";

    /// <summary>The one content type these suites publish rows of.</summary>
    internal static ContentTypeId Thing => new(CatalogFixtures.ThingTypeId);

    /// <summary>A registry carrying <see cref="Thing"/> and nothing else.</summary>
    internal static ContentTypeRegistry Registry() => CatalogFixtures.Registry(CatalogFixtures.ThingSpec);

    /// <summary>A publish request expecting the named base version.</summary>
    internal static ContentPublishRequest Request(int expectedBaseVersion)
        => new(Actor, Operator, "sql server reset tests", expectedBaseVersion);

    /// <summary>A store opened over the fixture's database, creating the schema when there is none.</summary>
    internal static async Task<SqlServerContentAuthoringStore> OpenAsync(SqlServerCatalogDatabase database)
    {
        var store = new SqlServerContentAuthoringStore(
            database.ConnectionString, Registry(), database.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        return store;
    }

    /// <summary>The named rows added and published, which is the smallest store with a version and an audit trail.</summary>
    internal static async Task Seed(SqlServerContentAuthoringStore store, params string[] keys)
    {
        var edits = new List<ContentEdit>(keys.Length);
        for (int i = 0; i < keys.Length; i++)
        {
            edits.Add(ContentEdit.Add(Thing, new ContentKey(keys[i]), CatalogFixtures.Fields(11 * (i + 1))));
        }

        await store.ApplyEditsAsync(edits, Actor, Operator, "seed");
        await store.PublishAsync(Request(0));
    }

    /// <summary>One text scalar on a raw connection, beside the fixture's numeric one.</summary>
    internal static string Text(SqlServerCatalogDatabase database, string sql)
    {
        using var connection = new SqlConnection(database.ConnectionString);
        connection.Open();
        using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as string ?? string.Empty;
    }

    /// <summary>
    /// Every table the database holds whose name this schema declares, read through the provider's OWN
    /// inventory rather than a pattern written here, so a test that says "the schema's own rule" uses it.
    /// </summary>
    internal static IReadOnlyList<string> Tables(SqlServerCatalogDatabase database)
    {
        using var connection = new SqlConnection(database.ConnectionString);
        connection.Open();
        using SqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name " + InventoryFromSql + " ORDER BY name;";
        var names = new List<string>();
        using SqlDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>
    /// How many of the schema's own tables stand, by the inventory the drop itself names. The whole
    /// inventory is a current catalog, zero is a database with none, and a count between is either a partial
    /// catalog or a version 1 one, which the reset tells apart by the version its metadata row names.
    /// </summary>
    internal static int CountTables(SqlServerCatalogDatabase database)
        => database.Scalar("SELECT COUNT(*) " + InventoryFromSql + ";");

    /// <summary>The inventory clause both reads above are built from, off the provider's own expectation set.</summary>
    static string InventoryFromSql { get; } = FormattableString.Invariant($"""
        FROM sys.tables
        WHERE schema_id = SCHEMA_ID(N'dbo') AND name IN ({SqlServerCatalogSchemaExpectations.TableNameList})
        """);
}
