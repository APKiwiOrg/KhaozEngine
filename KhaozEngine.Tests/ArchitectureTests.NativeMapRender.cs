using System;
using System.Linq;
using Xunit;

namespace KhaozEngine.Tests;

public partial class ArchitectureTests
{
    [Fact]
    public void NativeRenderPackage_HasExactOptionalEdges()
    {
        var graph = LoadGraph();
        Assert.True(graph.TryGetValue("KhaozEngine.MapDoc.Render3D", out var render));
        Assert.Equal(new[] { "MapDoc", "Render3D", "Terrain.Render3D" },
            render.ProjectRefs.Select(Short).Order(StringComparer.Ordinal));
        Assert.Empty(render.PackageRefs);
        // Render3D -> Windowing already carries Gpu.Metal, Gpu.D3D11 and Gpu.Vulkan. Direct backend edges stay forbidden here.
        var closure = TransitiveClosure(render.Name, graph);
        Assert.DoesNotContain(closure, n => n == "KhaozEngine.MapDoc.Physics" ||
            n.StartsWith("KhaozEngine.TileWorld", StringComparison.Ordinal) || n.StartsWith("KhaozEngine.MapEdit", StringComparison.Ordinal));
        foreach (string umbrella in Umbrellas) Assert.DoesNotContain(render.Name, TransitiveClosure(umbrella, graph));
        foreach (string root in new[] { "MapDoc", "MapDoc.Physics", "Server" })
            Assert.DoesNotContain(TransitiveClosure("KhaozEngine." + root, graph), n =>
                n is "KhaozEngine.MapDoc.Render3D" or "KhaozEngine.Render3D" or "KhaozEngine.Windowing" or "KhaozEngine.Gpu" ||
                n.StartsWith("KhaozEngine.Gpu.", StringComparison.Ordinal));
    }
}
