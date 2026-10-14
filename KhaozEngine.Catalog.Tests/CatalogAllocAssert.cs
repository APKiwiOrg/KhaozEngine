using System;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>xUnit collection definitions are local to each test assembly.</summary>
[CollectionDefinition("AllocSensitive", DisableParallelization = true)]
public sealed class AllocSensitiveCollection
{
}

/// <summary>Zero-allocation assertions for the non-parallel AllocSensitive collection.</summary>
internal static class CatalogAllocAssert
{
    /// <summary>Measures the warmed workload once. Any allocation or interrupted window fails.</summary>
    public static void NoPerCallAllocation(string description, Action loop) =>
        AllocationMeasurement.NoPerCallAllocation(description, loop);
}
