using System;
using Xunit;

namespace KhaozEngine.Tests.Commerce;

/// <summary>
/// A <see cref="FactAttribute"/> that is SKIPPED unless the environment variable
/// <c>KE_COMMERCE_SQLSERVER</c> is set to a reachable SQL Server / Azure SQL connection string. CI has no SQL
/// Server, so these run only locally / against a test DB on demand. Mirrors <c>GpuFactAttribute</c>.
/// </summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KE_COMMERCE_SQLSERVER")))
            Skip = "set KE_COMMERCE_SQLSERVER to run";
    }
}
