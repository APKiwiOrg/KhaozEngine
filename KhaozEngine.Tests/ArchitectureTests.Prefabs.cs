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

    [Fact]
    public void MarkerRuntime_HasNoTileWorldPhysicsOrGpuDependency()
    {
        var graph = LoadGraph();
        // Marker payloads, resolution, the registry, rectangle lookup, adoption and anchors all live in MapDoc.
        string[] closure = TransitiveClosure("KhaozEngine.MapDoc", graph).Select(Short).ToArray();
        Assert.DoesNotContain(closure, name => name.StartsWith("TileWorld", StringComparison.Ordinal)
            || name.StartsWith("Physics", StringComparison.Ordinal)
            || name.StartsWith("Gpu", StringComparison.Ordinal)
            || name.StartsWith("Render", StringComparison.Ordinal)
            || name.StartsWith("MapEdit", StringComparison.Ordinal)
            || name.StartsWith("Game", StringComparison.Ordinal));
        Assert.DoesNotContain(TransitiveClosure("KhaozEngine.MapDoc", graph).Append("KhaozEngine.MapDoc").Where(graph.ContainsKey)
            .SelectMany(p => graph[p].PackageRefs),
            package => package.StartsWith("Veldrid", StringComparison.Ordinal) || package.StartsWith("Silk", StringComparison.Ordinal));
        // The editor's marker readouts use MapDoc's shared seat query, never the physics consumer.
        Assert.DoesNotContain("KhaozEngine.MapDoc.Physics", graph["KhaozEngine.MapEditor"].ProjectRefs);
        Assert.DoesNotContain(graph["KhaozEngine.MapEditor"].ProjectRefs, name => name.StartsWith("KhaozEngine.TileWorld", StringComparison.Ordinal));
    }
}
