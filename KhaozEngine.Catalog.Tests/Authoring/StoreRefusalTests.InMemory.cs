using System;
using System.Linq;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Authoring;

public sealed class InMemoryStoreRefusalTests : StoreRefusalTests
{
    protected override bool UsesSqlite => false;

    [Fact]
    public void InMemoryStoreNeverConstructsAMessageOnlyRefusal()
    {
        var legacy = typeof(ContentAuthoringException).GetConstructors()
            .Where(constructor => constructor.GetParameters().All(parameter => parameter.ParameterType != typeof(ContentAuthoringRefusal)));
        Assert.DoesNotContain(RefusalConstructorFence.EngineCallers([typeof(InMemoryContentAuthoringStore).Assembly], legacy),
            caller => caller.StartsWith("KhaozEngine.Catalog.Authoring.InMemoryContentAuthoringStore", StringComparison.Ordinal));
    }

}
