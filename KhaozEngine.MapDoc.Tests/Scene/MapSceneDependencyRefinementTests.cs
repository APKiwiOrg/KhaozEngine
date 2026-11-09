using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapSceneDependencyRefinementTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Dependencies_RefuseEachWidenedRefinementCeiling(int field)
    {
        using var fixture = NativeSceneFixtures.Create("empty");
        var source = NativeSceneFixtures.Open(fixture, false);
        var defaults = new MapRefinementLimits();
        var tight = Change(defaults, field, 1);
        var scene = source.Acquire(Window(tight)).Scene!;
        Refused(scene.AcquireDependencies(Window(Change(tight, field, 2))));
        Refused(scene.AcquireDependencies(Window(null)));
        Assert.NotNull(scene.AcquireDependencies(Window(tight)).Scene);
        Assert.NotNull(scene.AcquireDependencies(Window(Change(tight, field, 0))).Scene);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Dependencies_CompareNullAsEffectiveDefaults(int field)
    {
        using var fixture = NativeSceneFixtures.Create("empty");
        var source = NativeSceneFixtures.Open(fixture, false);
        var defaults = new MapRefinementLimits();
        var scene = source.Acquire(Window(null)).Scene!;
        Assert.NotNull(scene.AcquireDependencies(Window(defaults)).Scene);
        Assert.NotNull(scene.AcquireDependencies(Window(null)).Scene);
        Refused(scene.AcquireDependencies(Window(Change(defaults, field, int.MaxValue))));
        var generous = source.Acquire(Window(Change(defaults, field, int.MaxValue))).Scene!;
        Assert.NotNull(generous.AcquireDependencies(Window(null)).Scene);
    }

    static void Refused(MapScopedSceneResult result)
    {
        Assert.Equal(MapSceneStatus.Invalid, result.Status);
        Assert.Null(result.Scene);
        Assert.False(result.Witness.CompleteForScope);
        Assert.Equal(0, result.Work.PayloadReads);
        Assert.Contains("Dependency acquisition cannot widen captured query budgets.", result.Findings);
    }

    static MapSceneScope Window(MapRefinementLimits? refinement)
    {
        var scope = MapSceneScopeTests.Window();
        return scope with { Surfaces = scope.Surfaces with { Limits = new MapQueryLimits { Refinement = refinement } } };
    }

    static MapRefinementLimits Change(MapRefinementLimits limits, int field, int value) => field switch
    {
        0 => limits with { MaxSourceFacesPerBound = value },
        1 => limits with { MaxPairChecks = value },
        2 => limits with { MaxRefinementFaces = value },
        3 => limits with { MaxRefinementVertices = value },
        4 => limits with { MaxContextPatches = value },
        _ => limits with { MaxContextFaces = value },
    };
}
