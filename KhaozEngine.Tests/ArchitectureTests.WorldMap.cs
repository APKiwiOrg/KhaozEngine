using System;
using System.Linq;
using Xunit;

namespace KhaozEngine.Tests;

public partial class ArchitectureTests
{
    /// <summary>The world-map core has only its colour primitives and no third-party dependency.</summary>
    [Fact]
    public void WorldMap_ReferencesOnlyPrimitives()
    {
        Project worldMap = LoadGraph()["KhaozEngine.WorldMap"];
        Assert.True(worldMap.IsPackableLibrary);
        Assert.Equal(new[] { "Primitives" },
            worldMap.ProjectRefs.Select(Short).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(worldMap.PackageRefs, package => !IgnoredInfraPackages.Contains(package));
    }

    /// <summary>World maps require an explicit package reference from every consumer.</summary>
    [Fact]
    public void WorldMap_IsAnExplicitReferenceOutsideEveryUmbrella()
    {
        var graph = LoadGraph();
        Assert.Contains("WorldMap", OptInBackends);
        foreach (string umbrella in Umbrellas)
            Assert.DoesNotContain("KhaozEngine.WorldMap", TransitiveClosure(umbrella, graph));
    }

    /// <summary>The tests bring only the core and the headless imaging used for later goldens.</summary>
    [Fact]
    public void WorldMapTests_ReferenceOnlyThePackageAndImaging()
    {
        Project tests = LoadGraph()["KhaozEngine.WorldMap.Tests"];
        Assert.False(tests.IsPackableLibrary);
        Assert.Equal(new[] { "Imaging", "WorldMap" },
            tests.ProjectRefs.Select(Short).OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    /// <summary>The TileWorld source adapter depends only on its two authored-world contracts.</summary>
    [Fact]
    public void TileWorldWorldMap_ReferencesOnlyWorldMapAndTileWorld()
    {
        Project adapter = LoadGraph()["KhaozEngine.TileWorld.WorldMap"];
        Assert.True(adapter.IsPackableLibrary);
        Assert.Equal(new[] { "TileWorld", "WorldMap" },
            adapter.ProjectRefs.Select(Short).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(adapter.PackageRefs, package => !IgnoredInfraPackages.Contains(package));
    }

    /// <summary>Every umbrella leaves the TileWorld map source as an explicit consumer choice.</summary>
    [Fact]
    public void TileWorldWorldMap_IsAnExplicitReferenceOutsideEveryUmbrella()
    {
        var graph = LoadGraph();
        Assert.Contains("TileWorld.WorldMap", OptInBackends);
        foreach (string umbrella in Umbrellas)
            Assert.DoesNotContain("KhaozEngine.TileWorld.WorldMap", TransitiveClosure(umbrella, graph));
    }

    /// <summary>The adapter tests reference only the adapter package.</summary>
    [Fact]
    public void TileWorldWorldMapTests_ReferenceOnlyThePackage()
    {
        Project tests = LoadGraph()["KhaozEngine.TileWorld.WorldMap.Tests"];
        Assert.False(tests.IsPackableLibrary);
        Assert.Equal(new[] { "TileWorld.WorldMap" },
            tests.ProjectRefs.Select(Short).OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    /// <summary>The map widget depends only on the cached map and shared GUI navigation.</summary>
    [Fact]
    public void WorldMapGui_ReferencesOnlyWorldMapAndGui()
    {
        Project view = LoadGraph()["KhaozEngine.WorldMap.Gui"];
        Assert.True(view.IsPackableLibrary);
        Assert.Equal(new[] { "Gui", "WorldMap" },
            view.ProjectRefs.Select(Short).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(view.PackageRefs, package => !IgnoredInfraPackages.Contains(package));
    }

    /// <summary>Every umbrella leaves the world map widget as an explicit consumer choice.</summary>
    [Fact]
    public void WorldMapGui_IsAnExplicitReferenceOutsideEveryUmbrella()
    {
        var graph = LoadGraph();
        Assert.Contains("WorldMap.Gui", OptInBackends);
        foreach (string umbrella in Umbrellas)
            Assert.DoesNotContain("KhaozEngine.WorldMap.Gui", TransitiveClosure(umbrella, graph));
    }

    /// <summary>The widget tests reference only the widget and the opt-in native capture test kit.</summary>
    [Fact]
    public void WorldMapGuiTests_ReferenceOnlyThePackageAndGpuTestKit()
    {
        Project tests = LoadGraph()["KhaozEngine.WorldMap.Gui.Tests"];
        Assert.False(tests.IsPackableLibrary);
        Assert.Equal(new[] { "Gpu.TestKit", "WorldMap.Gui" },
            tests.ProjectRefs.Select(Short).OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }
}
