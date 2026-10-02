using System;
using System.Linq;
using Xunit;

namespace KhaozEngine.Tests;

public partial class ArchitectureTests
{
    [Fact]
    public void Movement_ReferencesOnlyLocomotionNavigationAndPhysics()
    {
        Project movement = LoadGraph()["KhaozEngine.Movement"];
        Assert.True(movement.IsPackableLibrary);
        Assert.Equal(new[] { "Locomotion", "Navigation", "Physics" },
            movement.ProjectRefs.Select(Short).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(movement.PackageRefs, package => !IgnoredInfraPackages.Contains(package));
    }

    [Fact]
    public void Movement_IsAnExplicitReferenceOutsideEveryUmbrella()
    {
        var graph = LoadGraph();
        Assert.Contains("Movement", OptInBackends);
        foreach (string umbrella in Umbrellas)
            Assert.DoesNotContain("KhaozEngine.Movement", TransitiveClosure(umbrella, graph));
    }

    [Fact]
    public void MovementTests_ReferenceOnlyMovementAndPhysicsBackend()
    {
        Project tests = LoadGraph()["KhaozEngine.Movement.Tests"];
        Assert.False(tests.IsPackableLibrary);
        Assert.Equal(new[] { "Movement", "Physics.Bepu" },
            tests.ProjectRefs.Select(Short).OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }
}
