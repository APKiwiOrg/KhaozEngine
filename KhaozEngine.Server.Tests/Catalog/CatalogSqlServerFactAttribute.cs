using System;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// A <see cref="FactAttribute"/> that is SKIPPED unless the environment variable
/// <c>KE_CATALOG_SQLSERVER</c> is set to a reachable SQL Server or Azure SQL connection string. CI has no SQL
/// Server, so these run only locally or against a test database on demand. Mirrors
/// <c>KhaozEngine.Tests.Commerce.SqlServerFactAttribute</c>.
/// <para>
/// A SEPARATE variable rather than a reuse of <c>KE_COMMERCE_SQLSERVER</c> or the journal's, because the
/// suites create different schemas in the same instance and an operator should be able to run one without the
/// other. The catalog fixture also insists on a database named <c>-catalog-test-</c>, which the journal's
/// <c>-journal-test-</c> rule would refuse, so one variable could not serve both even if the schemas did not
/// collide.
/// </para>
/// </summary>
public sealed class CatalogSqlServerFactAttribute : FactAttribute
{
    /// <summary>The variable naming the instance, read once per fact at construction.</summary>
    public const string EnvironmentVariable = "KE_CATALOG_SQLSERVER";

    /// <summary>Skips unless <see cref="EnvironmentVariable"/> names an instance.</summary>
    public CatalogSqlServerFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvironmentVariable)))
        {
            Skip = "set " + EnvironmentVariable + " to run";
        }
    }
}
