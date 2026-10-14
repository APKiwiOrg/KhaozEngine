using System;

namespace KhaozEngine.Tests;

/// <summary>Zero-allocation assertions for the non-parallel AllocSensitive collection.</summary>
internal static class AllocAssert
{
    /// <summary>Measures the warmed workload once. Any allocation or interrupted window fails.</summary>
    public static void NoPerCallAllocation(string description, Action loop) =>
        AllocationMeasurement.NoPerCallAllocation(description, loop);
}
