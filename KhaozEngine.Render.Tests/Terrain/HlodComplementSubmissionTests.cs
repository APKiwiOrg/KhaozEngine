using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Terrain;

public sealed class HlodComplementSubmissionTests
{
    static readonly PropClusterKey Key = new("roofs", 0, 0, 0);
    static readonly PropPlacement[] Placements =
        [new("panel", 0f, 0f, 0f, 1f, 0f, 0), new("panel", 1f, 0f, 0f, 1f, 0f, 0)];

    [Theory]
    [InlineData(-1, false)]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void Hlod_colour_and_shadow_queue_exact_complements(int hidden, bool parts)
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        using var renderer = Create(scene, parts);
        foreach (float distance in new[] { 90f, 100f, 110f })
        {
            scene.Begin();
            if (hidden < 0) renderer.Draw(new Vector3(distance, 0f, 0f));
            else renderer.DrawWithVisibility(new Vector3(distance, 0f, 0f), new Visibility(hidden));
            SceneInstances.Instance[] draws = scene.QueuedInstancesForTests.ToArray();
            Assert.Equal(3, draws.Length);
            float threshold = (distance - 80f) / 40f;
            Assert.All(draws, draw =>
            {
                Assert.Equal(threshold, draw.DissolveThreshold);
                Assert.False(draw.InvertShadowDissolve);
                Assert.True(draw.CastsShadows);
                Assert.Equal(hidden == 2, draw.ShadowOnly);
                Assert.Equal(ShadowCastKind.Dissolving, Scene3D.ClassifyCaster(draw));
            });
            Assert.Equal(0f, draws[0].DissolveComplement);
            Assert.Equal(0f, draws[1].DissolveComplement);
            Assert.Equal(1f, draws[2].DissolveComplement);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mixed_roof_visibility_uses_solid_individuals_for_colour_and_shadow(bool parts)
    {
        using var harness = new MotionTestScene();
        using var renderer = Create(harness.Scene, parts);
        harness.Scene.Begin();
        renderer.DrawWithVisibility(new Vector3(90f, 0f, 0f), new Visibility(1));
        var draws = harness.Scene.QueuedInstancesForTests;
        Assert.Equal(2, draws.Count);
        Assert.Single(draws, draw => draw.ShadowOnly);
        Assert.Single(draws, draw => !draw.ShadowOnly);
        Assert.All(draws, draw =>
        {
            Assert.Equal(0f, draw.DissolveThreshold);
            Assert.Equal(0f, draw.DissolveComplement);
            Assert.False(draw.InvertShadowDissolve);
            Assert.Equal(ShadowCastKind.Opaque, Scene3D.ClassifyCaster(draw));
        });
    }

    [Fact]
    public void Hlod_encoded_keep_sets_cover_each_noise_value_exactly_once()
    {
        using var harness = new MotionTestScene();
        using var renderer = Create(harness.Scene, false);
        foreach (float distance in new[] { 80f, 84f, 90f, 100f, 110f, 116f, 120f })
        {
            harness.Scene.Begin();
            renderer.Draw(new Vector3(distance, 0f, 0f));
            var draws = harness.Scene.QueuedInstancesForTests;
            // Each individual has its own footprint. Compare one placement against its merged representation.
            SceneInstances.Instance first = draws[0];
            SceneInstances.Instance? merged = draws.Count == 3 ? draws[2] : null;
            for (int sample = 0; sample < 1024; sample++)
            {
                float noise = sample / 1024f;
                int owners = Keeps(first, noise) ? 1 : 0;
                if (merged is { } other && Keeps(other, noise)) owners++;
                Assert.True(owners == 1, $"distance={distance}, noise={noise}, owners={owners}");
            }
        }
    }

    [Theory]
    [InlineData(120f)]
    [InlineData(300f)]
    public void Completed_Hlod_handoff_keeps_the_solid_fast_path(float distance)
    {
        using var harness = new MotionTestScene();
        using var renderer = Create(harness.Scene, false);
        harness.Scene.Begin();
        renderer.Draw(new Vector3(distance, 0f, 0f));
        var draw = Assert.Single(harness.Scene.QueuedInstancesForTests);
        Assert.Equal(0f, draw.DissolveThreshold);
        Assert.Equal(0f, draw.DissolveComplement);
        Assert.False(draw.InvertShadowDissolve);
        Assert.Equal(ShadowCastKind.Opaque, Scene3D.ClassifyCaster(draw));
    }

    [Theory]
    [InlineData(460f, 0f)]
    [InlineData(480f, .5f)]
    public void Hlod_exit_is_an_ordinary_dissolve(float distance, float threshold)
    {
        using var harness = new MotionTestScene();
        using var renderer = Create(harness.Scene, false);
        harness.Scene.Begin();
        renderer.Draw(new Vector3(distance, 0f, 0f));
        var draw = Assert.Single(harness.Scene.QueuedInstancesForTests);
        Assert.Equal(threshold, draw.DissolveThreshold);
        Assert.Equal(0f, draw.DissolveComplement);
        Assert.False(draw.InvertShadowDissolve);
    }

    static bool Keeps(SceneInstances.Instance draw, float noise) =>
        draw.DissolveComplement > .5f ? noise < draw.DissolveThreshold : noise >= draw.DissolveThreshold;

    static PropClusterRenderer Create(Scene3D scene, bool parts)
    {
        GltfMesh mesh = MeshPrimitives.Box(1f);
        MeshHandle handle = scene.LoadMesh(mesh);
        PropLayer layer = parts
            ? PropLayer.PlacementLayer(Placements,
                new Dictionary<string, IReadOnlyList<MeshHandle>> { ["panel"] = new[] { handle } }, 500f, 40f)
            : PropLayer.PlacementLayer(Placements,
                new Dictionary<string, MeshHandle> { ["panel"] = handle }, 500f, 40f);
        layer = layer.WithHlod(new Dictionary<string, GltfMesh> { ["panel"] = mesh }, 100f, 0f, 40f);
        var renderer = new PropClusterRenderer(scene);
        renderer.Apply(Key, renderer.BuildCpu(new PropClusterBuildRequest(Key, 1,
            new RectArea(-2f, -2f, 2f, 2f), layer, Placements)));
        return renderer;
    }

    sealed class Visibility(int hidden) : IPropClusterVisibility
    {
        public bool RequiresVisibility => true;
        public bool Controls(PropClusterKey key) => true;
        public int PlacementCount(PropClusterKey key, long generation) => 2;
        public bool IsHidden(PropClusterKey key, long generation, int index) => index < hidden;
    }
}
