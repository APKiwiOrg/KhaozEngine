using System;
using System.Linq;
using Xunit;

namespace KhaozEngine.Tests;

public partial class ArchitectureTests
{
    [Fact]
    public void NativeBuildingRuntime_HasNoTileWorldOrGpuEdge()
    {
        var graph = LoadGraph();
        foreach (string runtime in new[] { "KhaozEngine.MapDoc", "KhaozEngine.MapDoc.Physics" })
        {
            var closure = TransitiveClosure(runtime, graph).Select(Short).ToArray();
            Assert.DoesNotContain(closure, name => name.StartsWith("TileWorld", StringComparison.Ordinal)
                || name.StartsWith("Gpu", StringComparison.Ordinal)
                || name.StartsWith("Render", StringComparison.Ordinal)
                || name.StartsWith("MapEdit", StringComparison.Ordinal));
        }
        Assert.DoesNotContain(TransitiveClosure("KhaozEngine.MapDoc", graph).Select(Short),
            name => name.StartsWith("Physics", StringComparison.Ordinal));
        Assert.DoesNotContain("KhaozEngine.MapDoc.Physics", TransitiveClosure("KhaozEngine.MapEditor", graph));
    }
}
