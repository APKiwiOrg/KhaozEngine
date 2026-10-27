using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Terrain;

/// <summary>A per-placement colour fade through <see cref="IPropClusterVisibility.ColourFade"/>: the colour dissolves
/// while the shadow stays whole, and a fade never reaches a placement it was not asked about.</summary>
public sealed class PropClusterColourFadeSubmissionTests
{
    static readonly PropClusterKey Key = new("trees", 0, 0, 0);
    static readonly PropPlacement[] Placements =
        [new("tree", 0f, 0f, 0f, 1f, 0f, 0), new("tree", 3f, 0f, 0f, 1f, 0f, 0)];

    static PropClusterRenderer Create(Scene3D scene)
    {
        MeshHandle handle = scene.LoadMesh(MeshPrimitives.Box(1f));
        PropLayer layer = PropLayer.PlacementLayer(Placements,
            new Dictionary<string, IReadOnlyList<MeshHandle>> { ["tree"] = new[] { handle } }, 500f);
        var renderer = new PropClusterRenderer(scene);
        renderer.Apply(Key, renderer.BuildCpu(new PropClusterBuildRequest(Key, 1,
            new RectArea(-8f, -8f, 8f, 8f), layer, Placements)));
        return renderer;
    }

    static SceneInstances.Instance[] DrawsAt(SceneInstances.Instance[] draws, float x) =>
        draws.Where(d => d.World.Translation.X == x).ToArray();

    [Fact]
    public void APartlyFadedPlacementDissolvesColourAndKeepsAWholeShadow()
    {
        using var harness = new MotionTestScene();
        using PropClusterRenderer renderer = Create(harness.Scene);
        harness.Scene.Begin();

        renderer.DrawWithVisibility(Vector3.Zero, new Fades(0.4f));

        SceneInstances.Instance[] faded = DrawsAt(harness.Scene.QueuedInstancesForTests.ToArray(), 0f);
        Assert.Equal(2, faded.Length);
        SceneInstances.Instance colour = Assert.Single(faded, d => !d.ShadowOnly);
        Assert.False(colour.CastsShadows);
        Assert.Equal(0.4f, colour.DissolveThreshold);
        SceneInstances.Instance shadow = Assert.Single(faded, d => d.ShadowOnly);
        Assert.Equal(0f, shadow.DissolveThreshold);
    }

    [Fact]
    public void AFullyFadedPlacementIsShadowOnly()
    {
        using var harness = new MotionTestScene();
        using PropClusterRenderer renderer = Create(harness.Scene);
        harness.Scene.Begin();

        PropClusterDrawCounts counts = renderer.DrawWithVisibility(Vector3.Zero, new Fades(1f));

        SceneInstances.Instance shadow = Assert.Single(DrawsAt(harness.Scene.QueuedInstancesForTests.ToArray(), 0f));
        Assert.True(shadow.ShadowOnly);
        Assert.Equal(0f, shadow.DissolveThreshold);
        Assert.Equal(new PropClusterDrawCounts(1, 1), counts);
    }

    [Fact]
    public void NeighboursInTheClusterDrawNormally()
    {
        using var harness = new MotionTestScene();
        using PropClusterRenderer renderer = Create(harness.Scene);
        harness.Scene.Begin();

        renderer.DrawWithVisibility(Vector3.Zero, new Fades(0.4f));

        SceneInstances.Instance neighbour = Assert.Single(DrawsAt(harness.Scene.QueuedInstancesForTests.ToArray(), 3f));
        Assert.False(neighbour.ShadowOnly);
        Assert.True(neighbour.CastsShadows);
        Assert.Equal(0f, neighbour.DissolveThreshold);
    }

    [Fact]
    public void AFadeForcesIndividualsOverAMixedMerge()
    {
        var merged = new PropClusterDrawState(false, 0f, true, 0f, false, 1, 1) { HasIndividualGeometry = true };

        PropClusterVisibilityDecision decision = PropClusterVisibilityDecision.Resolve(Key, merged, new Fades(0.4f));

        Assert.True(decision.State.DrawsIndividuals);
        Assert.False(decision.State.DrawsMerged);
        Assert.Equal(0f, decision.State.IndividualDissolveFloor);
        Assert.Equal(0.4f, decision.IndividualColourFade(Key, 0, new Fades(0.4f)));
        Assert.Equal(0f, decision.IndividualColourFade(Key, 1, new Fades(0.4f)));
    }

    [Fact]
    public void AFadeNeverBlanksAMergeWithoutIndividuals()
    {
        var merged = new PropClusterDrawState(false, 0f, true, 0f, false, 1, 1) { HasIndividualGeometry = false };

        PropClusterVisibilityDecision decision = PropClusterVisibilityDecision.Resolve(Key, merged, new Fades(1f));

        Assert.True(decision.DrawsMergedColour);
        Assert.False(decision.MergedShadowOnly);
    }

    [Fact]
    public void WithNoFadeSubmissionIsUnchanged()
    {
        using var plain = new MotionTestScene();
        using var faded = new MotionTestScene();
        using PropClusterRenderer a = Create(plain.Scene);
        using PropClusterRenderer b = Create(faded.Scene);
        plain.Scene.Begin();
        faded.Scene.Begin();

        a.DrawWithVisibility(Vector3.Zero, new Fades(0f, controls: false));
        b.DrawWithVisibility(Vector3.Zero, new Fades(0f));

        Assert.Equal(
            plain.Scene.QueuedInstancesForTests.Select(d => (d.World, d.CastsShadows, d.ShadowOnly, d.DissolveThreshold)),
            faded.Scene.QueuedInstancesForTests.Select(d => (d.World, d.CastsShadows, d.ShadowOnly, d.DissolveThreshold)));
    }

    // Fades placement 0 by a fixed amount and leaves every other placement alone.
    sealed class Fades(float first, bool controls = true) : IPropClusterVisibility
    {
        public bool Controls(PropClusterKey key) => controls;
        public int PlacementCount(PropClusterKey key, long generation) => Placements.Length;
        public bool IsHidden(PropClusterKey key, long generation, int index) => false;
        public float ColourFade(PropClusterKey key, long generation, int index) => index == 0 ? first : 0f;
    }
}
