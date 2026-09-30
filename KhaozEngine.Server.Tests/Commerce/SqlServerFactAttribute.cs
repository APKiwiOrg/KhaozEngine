using System;
using Xunit;

namespace KhaozEngine.Tests.Commerce;

/// <summary>
/// A <see cref="FactAttribute"/> that is SKIPPED unless the environment variable
/// <c>KE_COMMERCE_SQLSERVER</c> is set to a reachable SQL Server / Azure SQL connection string. CI's
/// <c>server-sqlserver</c> job sets it against a disposable SQL Server service. The fact that rebuilds the wallet
/// tables also needs <c>-commerce-test-</c> in the database name. Mirrors <c>GpuFactAttribute</c>.
/// </summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KE_COMMERCE_SQLSERVER")))
            Skip = "set KE_COMMERCE_SQLSERVER to run";
    }
}
