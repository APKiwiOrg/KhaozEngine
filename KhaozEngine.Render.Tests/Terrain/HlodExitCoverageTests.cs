using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain;

public sealed class HlodExitCoverageTests
{
    static readonly PropClusterKey Key = new("panels", 0, 0, 0);
    static readonly PropPlacement[] Placements = { new("panel", 0f, 0f, 0f, 1f, 0f, 0) };

    [Theory]
    [InlineData(120f, 40f, 100f, 40f, false, false)]
    [InlineData(120f, 40f, 100f, 40f, true, true)]
    [InlineData(110f, 30f, 100f, 80f, false, true)]
    [InlineData(110f, 30f, 100f, 80f, true, false)]
    [InlineData(160f, 120f, 100f, 40f, false, false)]
    [InlineData(160f, 120f, 100f, 40f, true, true)]
    [InlineData(120f, 160f, 100f, 40f, false, true)]
    [InlineData(120f, 160f, 100f, 40f, true, false)]
    public void Exit_and_handoff_partition_the_surviving_coverage(
        float radius, float fadeWidth, float hlodDistance, float hlodWidth, bool shadowOnly, bool parts)
    {
        // Regression for https://github.com/APKiwiOrg/KhaozEngine/issues/1542.
        PropLayer layer = Layer(radius, fadeWidth, hlodDistance, hlodWidth, parts);
        var backend = new RecordingBackend();
        using var renderer = Create(backend, layer);
        float exitStart = MathF.Max(0f, layer.DrawRadius - layer.FadeBandWidth);
        float handoffStart = layer.HlodDistance - layer.HlodCrossfadeWidth * .5f;
        float handoffEnd = handoffStart + layer.HlodCrossfadeWidth;
        float overlapStart = MathF.Max(exitStart, handoffStart);
        float overlapEnd = MathF.Min(layer.DrawRadius, handoffEnd);
        float[] distances =
        {
            overlapStart + (overlapEnd - overlapStart) * .5f,
            exitStart, handoffStart, handoffEnd, layer.DrawRadius,
            overlapStart, overlapStart + (overlapEnd - overlapStart) * .25f,
            overlapStart + (overlapEnd - overlapStart) * .75f,
            MathF.BitDecrement(layer.DrawRadius),
            handoffStart + layer.HlodCrossfadeWidth * PropHlod.HlodMergedSkipAt,
            handoffStart + layer.HlodCrossfadeWidth * (PropHlod.HlodMergedSkipAt * .5f),
            handoffStart + layer.HlodCrossfadeWidth * PropHlod.HlodPropsSkipAt,
            handoffStart + layer.HlodCrossfadeWidth * ((1f + PropHlod.HlodPropsSkipAt) * .5f),
        };
        foreach (float distance in distances)
        {
            backend.Draws.Clear();
            Vector3 focus = new(distance, 0f, 0f);
            if (shadowOnly) renderer.DrawWithVisibility(focus, new Hidden());
            else renderer.Draw(focus);
            if (distance >= layer.DrawRadius)
            {
                Assert.Empty(backend.Draws);
                continue;
            }

            float exit = Math.Clamp((distance - exitStart) / (layer.DrawRadius - exitStart), 0f, 1f);
            float t = Math.Clamp((distance - handoffStart) / layer.HlodCrossfadeWidth, 0f, 1f);
            if (distance >= exitStart && distance < handoffStart)
            {
                // Before the HLOD band, preserve the legacy exit state as well as separated profiles.
                Assert.True(renderer.TryGetDrawState(Key, focus, out var beforeHandoff));
                Assert.Equal(State(true, 0f, true, exit, false), beforeHandoff);
                continue;
            }
            bool paired = t > PropHlod.HlodMergedSkipAt && t < PropHlod.HlodPropsSkipAt;
            if (paired)
            {
                Draw individual = Assert.Single(backend.Draws, draw => !draw.Merged);
                Draw merged = Assert.Single(backend.Draws, draw => draw.Merged);
                Assert.False(individual.Complement);
                Assert.True(merged.Complement);
                Assert.Equal(exit, individual.Dissolve - merged.Dissolve, 5);
                Assert.Equal((1f - t) * (1f - exit), 1f - individual.Dissolve, 5);
                Assert.Equal(t * (1f - exit), merged.Dissolve, 5);
                Assert.Equal(individual.Floor, individual.Dissolve);
                AssertCoverage(backend.Draws, merged.Dissolve, individual.Dissolve);
            }
            else
            {
                Draw single = Assert.Single(backend.Draws);
                Assert.False(single.Complement);
                Assert.Equal(exit, single.Dissolve, 5);
                AssertCoverage(backend.Draws, 0f, single.Dissolve);
            }
            Assert.All(backend.Draws, draw =>
            {
                Assert.Equal(layer, draw.Layer);
                Assert.Equal(shadowOnly, draw.ShadowOnly);
            });
        }
    }

    [Fact]
    public void An_exit_before_the_handoff_band_keeps_the_legacy_state()
    {
        PropLayer layer = Layer(40f, 20f, 100f, 40f, parts: false);
        var backend = new RecordingBackend();
        using var renderer = Create(backend, layer);
        float start = layer.DrawRadius - layer.FadeBandWidth;
        Assert.True(renderer.TryGetDrawState(Key, new(start, 0f, 0f), out var entering));
        Assert.Equal(State(true, 0f, true, 0f, false), entering);
        Assert.True(renderer.TryGetDrawState(Key, new(start + layer.FadeBandWidth * .5f, 0f, 0f), out var halfway));
        Assert.Equal(State(true, 0f, true, .5f, false), halfway);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Separated_bands_keep_every_draw_state_float_bit(bool shadowOnly)
    {
        PropLayer layer = Layer(500f, 40f, 100f, 40f, parts: false);
        var backend = new RecordingBackend();
        using var renderer = Create(backend, layer);
        float start = layer.HlodDistance - layer.HlodCrossfadeWidth * .5f;
        float end = start + layer.HlodCrossfadeWidth;
        float exit = layer.DrawRadius - layer.FadeBandWidth;
        (float Distance, PropClusterDrawState State)[] samples =
        {
            (0f, State(true, 0f, false, 0f, false)),
            (start, State(true, 0f, false, 0f, false)),
            (layer.HlodDistance, State(true, .5f, true, .5f, true)),
            (end, State(false, 0f, true, 0f, false)),
            (exit, State(false, 1f, true, 0f, false)),
            (exit + layer.FadeBandWidth * .5f, State(false, 1f, true, .5f, false)),
            (layer.DrawRadius, State(false, 0f, false, 0f, false)),
        };
        foreach (var sample in samples)
        {
            Vector3 focus = new(sample.Distance, 0f, 0f);
            Assert.True(renderer.TryGetDrawState(Key, focus, out var state));
            Assert.Equal(sample.State, state);
            Assert.Equal(BitConverter.SingleToInt32Bits(sample.State.IndividualDissolveFloor),
                BitConverter.SingleToInt32Bits(state.IndividualDissolveFloor));
            Assert.Equal(BitConverter.SingleToInt32Bits(sample.State.MergedDissolve),
                BitConverter.SingleToInt32Bits(state.MergedDissolve));
            backend.Draws.Clear();
            if (shadowOnly) renderer.DrawWithVisibility(focus, new Hidden());
            else renderer.Draw(focus);
            Assert.Equal(state.DrawsIndividuals, backend.Draws.Exists(draw => !draw.Merged));
            Assert.Equal(state.DrawsMerged, backend.Draws.Exists(draw => draw.Merged));
            Assert.All(backend.Draws, draw =>
            {
                Assert.Equal(draw.Merged ? state.MergedDissolve : state.IndividualDissolveFloor, draw.Dissolve);
                Assert.Equal(draw.Merged && state.MergedComplement, draw.Complement);
            });
        }
    }

    static PropClusterDrawState State(bool individual, float floor, bool merged, float dissolve, bool complement) =>
        new(individual, floor, merged, dissolve, complement, 1, 1) { HasIndividualGeometry = true };

    static void AssertCoverage(List<Draw> draws, float lower, float upper)
    {
        // Include exact boundaries, where the shader's strict complement comparison matters.
        var noiseValues = new List<float> { lower, upper };
        for (int sample = 0; sample < 128; sample++) noiseValues.Add(sample / 128f);
        foreach (float noise in noiseValues)
        {
            int owners = 0;
            foreach (Draw draw in draws)
                if (draw.Complement ? noise < draw.Dissolve : noise >= draw.Dissolve) owners++;
            Assert.Equal(noise < lower || noise >= upper ? 1 : 0, owners);
        }
    }

    static PropLayer Layer(float radius, float fade, float distance, float width, bool parts)
    {
        PropLayer layer = parts
            ? PropLayer.PlacementLayer(Placements,
                new Dictionary<string, IReadOnlyList<MeshHandle>> { ["panel"] = new[] { new MeshHandle(90) } }, radius, fade)
            : PropLayer.PlacementLayer(Placements,
                new Dictionary<string, MeshHandle> { ["panel"] = new MeshHandle(90) }, radius, fade);
        return layer.WithHlod(new Dictionary<string, GltfMesh> { ["panel"] = MeshPrimitives.Box(1f) }, distance, 0f, width);
    }

    static PropClusterRenderer Create(RecordingBackend backend, PropLayer layer)
    {
        var renderer = new PropClusterRenderer(backend, PropHlod.BuildMergedMeshMeasured, static (_, error) => throw error);
        renderer.Apply(Key, renderer.BuildCpu(new PropClusterBuildRequest(Key, 1,
            new RectArea(-1f, -1f, 1f, 1f), layer, Placements)));
        return renderer;
    }

    readonly record struct Draw(PropLayer Layer, bool Merged, bool ShadowOnly, float Dissolve, bool Complement, float Floor);

    sealed class RecordingBackend : IPropClusterRenderBackend
    {
        public readonly List<Draw> Draws = new();
        public MeshHandle LoadMesh(GltfMesh mesh) => new(1);
        public void UnloadMesh(MeshHandle handle) { }
        public int DrawProps(IReadOnlyList<PropPlacement> placements, PropLayer layer, Vector3 focus, float dissolveFloor) =>
            Emit(placements, layer, focus, dissolveFloor, false);
        public int DrawShadowOnlyProps(IReadOnlyList<PropPlacement> placements, PropLayer layer, Vector3 focus, float dissolveFloor) =>
            Emit(placements, layer, focus, dissolveFloor, true);

        int Emit(IReadOnlyList<PropPlacement> placements, PropLayer layer, Vector3 focus, float floor, bool shadowOnly)
        {
            void Record(RecordingBackend target, MeshHandle mesh, Matrix4x4 world, float dissolve, float complement) =>
                target.Draws.Add(new(layer, false, shadowOnly, dissolve, complement > .5f, floor));
            if (shadowOnly) return PropClusterShadowSubmission.Emit(placements, layer, focus, floor, this, Record);
            return layer.PartMeshes is { } parts
                ? PropRenderer.EmitParts(placements, parts, layer.LodPartMeshes, layer.LodDistance, focus,
                    layer.DrawRadius, layer.FadeBandWidth, floor, this, Record, lodCrossfadeWidth: layer.LodCrossfadeWidth)
                : PropRenderer.Emit(placements, layer.Meshes, layer.LodMeshes, layer.LodDistance, focus,
                    layer.DrawRadius, layer.FadeBandWidth, floor, this, Record, lodCrossfadeWidth: layer.LodCrossfadeWidth);
        }

        public void DrawMerged(MeshHandle handle, PropLayer layer, float dissolve, bool complement) =>
            Draws.Add(new(layer, true, false, dissolve, complement, 0f));
        public void DrawShadowOnlyMerged(MeshHandle handle, PropLayer layer, float dissolve, bool complement) =>
            Draws.Add(new(layer, true, true, dissolve, complement, 0f));
    }

    sealed class Hidden : IPropClusterVisibility
    {
        public bool RequiresVisibility => true;
        public bool Controls(PropClusterKey key) => true;
        public int PlacementCount(PropClusterKey key, long generation) => 1;
        public bool IsHidden(PropClusterKey key, long generation, int index) => true;
    }
}
