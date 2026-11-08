using Xunit;

namespace KhaozEngine.Tests.WorldStore;

/// <summary>A data-driven SQL Server fact with the same live-provider gate.</summary>
public sealed class SqlServerTheoryAttribute : TheoryAttribute
{
    public SqlServerTheoryAttribute() => Skip = new SqlServerFactAttribute().Skip;
}
