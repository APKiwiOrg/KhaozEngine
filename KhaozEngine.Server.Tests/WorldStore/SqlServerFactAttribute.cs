using System;
using Xunit;

namespace KhaozEngine.Tests.WorldStore;

/// <summary>
/// A <see cref="FactAttribute"/> SKIPPED unless <c>KE_SQLSERVER_TEST_CONNSTRING</c> is set to a reachable SQL
/// Server / Azure SQL connection string. The SQLite backend carries the always-on coverage. CI's
/// <c>server-sqlserver</c> job sets the variable against a disposable SQL Server service. The journal facts, and the
/// world store fact that rebuilds its table, also need <c>-journal-test-</c> in the database name. (Mirrors
/// <c>GpuFactAttribute</c>.)
/// </summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KE_SQLSERVER_TEST_CONNSTRING")))
            Skip = "set KE_SQLSERVER_TEST_CONNSTRING to run SQL Server world-store conformance";
    }
}
