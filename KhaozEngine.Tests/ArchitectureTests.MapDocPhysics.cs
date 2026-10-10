using System;
using System.Linq;
using Xunit;

namespace KhaozEngine.Tests;

public partial class ArchitectureTests
{
    [Fact]
    public void MapDocPhysics_ReferencesOnlyMapDocPhysicsAndMovement()
    {
        // The seams only: no renderer, no physics backend, no TileWorld and no third-party package.
        Project mapPhysics = LoadGraph()["KhaozEngine.MapDoc.Physics"];
        Assert.True(mapPhysics.IsPackableLibrary);
        Assert.Equal(new[] { "MapDoc", "Movement", "Physics" },
            mapPhysics.ProjectRefs.Select(Short).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(mapPhysics.PackageRefs, package => !IgnoredInfraPackages.Contains(package));
    }

    [Fact]
    public void MapDocPhysics_IsAnExplicitReferenceOutsideEveryUmbrella()
    {
        var graph = LoadGraph();
        Assert.Contains("MapDoc.Physics", OptInBackends);
        foreach (string umbrella in Umbrellas)
            Assert.DoesNotContain("KhaozEngine.MapDoc.Physics", TransitiveClosure(umbrella, graph));
    }

    [Fact]
    public void MapDocPhysicsTests_ReferenceOnlyThePackageThePhysicsBackendAndSharding()
    {
        Project tests = LoadGraph()["KhaozEngine.MapDoc.Physics.Tests"];
        Assert.False(tests.IsPackableLibrary);
        Assert.Equal(new[] { "MapDoc.Physics", "Physics.Bepu", "Sharding" },
            tests.ProjectRefs.Select(Short).OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }
}
