using System;
using System.IO;
using KhaozEngine.Catalog;
using Microsoft.Data.SqlClient;
using Xunit;

namespace KhaozEngine.Tests.Catalog.SqlServer;

/// <summary>
/// The collection every SQL Server catalog class enlists in, serialized because they share one thing that
/// cannot be namespaced apart: the <c>dbo.catalog_*</c> SCHEMA of the one test database. A catalog store owns
/// the whole schema rather than a key prefix, and every test here starts by dropping it, so two classes
/// running at once would drop each other's tables mid-test.
/// </summary>
[CollectionDefinition(SqlServerCatalogCollection.Name, DisableParallelization = true)]
public sealed class SqlServerCatalogCollection
{
    /// <summary>The collection name both classes carry.</summary>
    public const string Name = "SQL Server content catalog";
}

/// <summary>
/// One test's view of the SQL Server instance: the connection string, a pack root that dies with the test, and
/// the drop that gives every test an empty database to start from.
/// <para>
/// <b>The connection string must name a DEDICATED test database</b>, which is the journal suite's rule and is
/// here for the same reason: this fixture DROPS every <c>catalog_</c> table it finds, and pointing it at a
/// database someone cares about would be unrecoverable. The marker is checked rather than trusted to a
/// convention nobody reads.
/// </para>
/// </summary>
internal sealed class SqlServerCatalogDatabase : IDisposable
{
    /// <summary>The variable naming the instance, which is the catalog suite's own.</summary>
    internal const string EnvironmentVariable = CatalogSqlServerFactAttribute.EnvironmentVariable;

    /// <summary>What the initial catalog must contain before this fixture will drop anything in it.</summary>
    const string DedicatedDatabaseMarker = "-catalog-test-";

    public SqlServerCatalogDatabase()
    {
        ConnectionString = Require(Environment.GetEnvironmentVariable(EnvironmentVariable));
        Root = Path.Combine(Path.GetTempPath(), "kec-sqlserver-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Root);
        PackRoot = Path.Combine(Root, "pack");
        DropSchema();
    }

    /// <summary>The connection string a store is opened with.</summary>
    public string ConnectionString { get; }

    /// <summary>The directory everything this test writes to disk sits under.</summary>
    public string Root { get; }

    /// <summary>The pack store root a publish writes its files to.</summary>
    public string PackRoot { get; }

    /// <summary>A pack store over <see cref="PackRoot"/>, which a publish needs.</summary>
    public FileSystemPackStore Pack() => new(PackRoot);

    /// <summary>
    /// Every <c>catalog_</c> table gone, foreign keys dropped first so the order the tables come back in does
    /// not matter. This runs at construction, so each test starts from a database that has never been
    /// initialized, which is what the ValidateOnly cases need.
    /// </summary>
    public void DropSchema() => Execute(
        """
        DECLARE @sql nvarchar(max) = N'';

        SELECT @sql = @sql + N'ALTER TABLE dbo.' + QUOTENAME(t.name)
            + N' DROP CONSTRAINT ' + QUOTENAME(fk.name) + N';'
        FROM sys.foreign_keys fk
        JOIN sys.tables t ON t.object_id = fk.parent_object_id
        WHERE t.schema_id = SCHEMA_ID(N'dbo') AND t.name LIKE N'catalog[_]%';

        SELECT @sql = @sql + N'DROP TABLE dbo.' + QUOTENAME(name) + N';'
        FROM sys.tables
        WHERE schema_id = SCHEMA_ID(N'dbo') AND name LIKE N'catalog[_]%';

        EXEC sp_executesql @sql;
        """);

    /// <summary>
    /// Runs one statement on a RAW connection, which is how a test damages an object behind the store's back.
    /// </summary>
    /// <param name="sql">The statement to run.</param>
    public void Execute(string sql)
    {
        using var connection = new SqlConnection(ConnectionString);
        connection.Open();
        using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// One scalar on a RAW connection, which is how a test asserts on a table the store's own API does not
    /// expose. The whole statement is the argument rather than a table name, so every call site reads as a
    /// literal.
    /// </summary>
    /// <param name="sql">The statement to run, which must return one value.</param>
    public int Scalar(string sql)
    {
        using var connection = new SqlConnection(ConnectionString);
        connection.Open();
        using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() is int value ? value : 0;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a green test over.
        }
    }

    static string Require(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "The SQL Server content catalog suite requires " + EnvironmentVariable + ".");
        }

        var builder = new SqlConnectionStringBuilder(connectionString);
        return builder.InitialCatalog.Contains(DedicatedDatabaseMarker, StringComparison.OrdinalIgnoreCase)
            ? connectionString
            : throw new InvalidOperationException(
                "The SQL Server content catalog suite DROPS every catalog_ table it finds, so its initial catalog must contain '"
                + DedicatedDatabaseMarker + "'.");
    }
}
