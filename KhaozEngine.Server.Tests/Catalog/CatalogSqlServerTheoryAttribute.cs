using System;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>Runs conformance data rows only when the catalog SQL Server test instance is configured.</summary>
public sealed class CatalogSqlServerTheoryAttribute : TheoryAttribute
{
    /// <summary>Uses the same environment gate as the SQL Server catalog facts.</summary>
    public CatalogSqlServerTheoryAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(CatalogSqlServerFactAttribute.EnvironmentVariable)))
        {
            Skip = "set " + CatalogSqlServerFactAttribute.EnvironmentVariable + " to run";
        }
    }
}
