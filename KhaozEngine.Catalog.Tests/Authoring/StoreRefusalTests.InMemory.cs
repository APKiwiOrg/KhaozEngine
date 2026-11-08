namespace KhaozEngine.Tests.Catalog.Authoring;

public sealed class InMemoryStoreRefusalTests : StoreRefusalTests
{
    protected override bool UsesSqlite => false;
}
