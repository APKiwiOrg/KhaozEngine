using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Xunit;

namespace KhaozEngine.Tests.Accounts.SqlServer;

/// <summary>
/// A <see cref="FactAttribute"/> that is SKIPPED unless <c>KE_ACCOUNTS_SQLSERVER</c> holds a reachable SQL Server or
/// Azure SQL connection string. Mirrors <c>CatalogSqlServerFactAttribute</c> and its <c>KE_CATALOG_SQLSERVER</c>.
/// <para>
/// A variable of its own rather than a reuse of the catalog's, because this suite creates and drops its own tables
/// and insists on its own database marker, and an operator should be able to run one suite without the other.
/// </para>
/// </summary>
public sealed class AccountsSqlServerFactAttribute : FactAttribute
{
    /// <summary>The variable naming the instance.</summary>
    public const string EnvironmentVariable = "KE_ACCOUNTS_SQLSERVER";

    /// <summary>Skips unless <see cref="EnvironmentVariable"/> names an instance.</summary>
    public AccountsSqlServerFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvironmentVariable)))
            Skip = "set " + EnvironmentVariable + " to run";
    }
}

/// <summary>
/// One test's view of the SQL Server instance: the connection string, table names no other test uses, raw reads and
/// writes behind the store's back, and the drop of every table it handed out.
/// <para>
/// <b>The connection string must name a DEDICATED test database</b>, the catalog and journal suites' rule: this
/// fixture creates and drops tables, and pointing it at a database someone cares about would be a mistake it
/// refuses rather than trusts to a convention.
/// </para>
/// </summary>
internal sealed class SqlServerAccountDatabase : IDisposable
{
    private const string DedicatedDatabaseMarker = "-accounts-test-";
    private readonly List<string> tables = new();

    public SqlServerAccountDatabase() => ConnectionString = Require(
        Environment.GetEnvironmentVariable(AccountsSqlServerFactAttribute.EnvironmentVariable));

    /// <summary>The connection string a store is opened with.</summary>
    public string ConnectionString { get; }

    /// <summary>A <c>dbo</c> table name no other test uses, dropped when this fixture is disposed.</summary>
    public string NewTableName()
    {
        string name = "accounts_" + Guid.NewGuid().ToString("N");
        tables.Add(name);
        return name;
    }

    /// <summary>Runs <paramref name="sql"/> on a raw connection.</summary>
    public void Execute(string sql)
    {
        using var connection = new SqlConnection(ConnectionString);
        connection.Open();
        using SqlCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Every row <paramref name="sql"/> returns, its columns as invariant text joined by <c>|</c>, with a
    /// null column written <c>NULL</c>.</summary>
    public List<string> Query(string sql)
    {
        var rows = new List<string>();
        using var connection = new SqlConnection(ConnectionString);
        connection.Open();
        using SqlCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using SqlDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var columns = new string[reader.FieldCount];
            for (int i = 0; i < columns.Length; i++)
                columns[i] = reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)!;
            rows.Add(string.Join('|', columns));
        }
        return rows;
    }

    /// <summary>The column names of <c>dbo.<paramref name="table"/></c> in declaration order.</summary>
    public List<string> Columns(string table) =>
        Query($"SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.{table}') ORDER BY column_id;");

    public void Dispose()
    {
        foreach (string table in tables)
        {
            try
            {
                Execute($"DROP TABLE IF EXISTS dbo.[{table}];");
            }
            catch (SqlException)
            {
                // A leftover table in a dedicated test database is not worth failing a green test over.
            }
        }
    }

    private static string Require(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "The SQL Server account suite requires " + AccountsSqlServerFactAttribute.EnvironmentVariable + ".");
        var builder = new SqlConnectionStringBuilder(connectionString);
        return builder.InitialCatalog.Contains(DedicatedDatabaseMarker, StringComparison.OrdinalIgnoreCase)
            ? connectionString
            : throw new InvalidOperationException(
                "The SQL Server account suite creates and drops tables, so its initial catalog must contain '" +
                DedicatedDatabaseMarker + "'.");
    }
}
