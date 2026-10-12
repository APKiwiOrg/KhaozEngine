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
}
