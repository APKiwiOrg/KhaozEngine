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
    [InlineData(110f, 90f, 100f, 40f, false, false)]
    [InlineData(110f, 90f, 100f, 40f, true, true)]
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
            handoffEnd + (layer.DrawRadius - handoffEnd) * .5f,
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
                // Regression for #1807: the exit cannot introduce the merged mesh before its handoff.
                Assert.True(renderer.TryGetDrawState(Key, focus, out var beforeHandoff));
                Assert.Equal(State(true, 0f, false, exit, false), beforeHandoff);
                AssertCoverage(backend.Draws, 0f, exit);
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
            else if (t >= PropHlod.HlodPropsSkipAt && distance >= exitStart)
            {
                Draw merged = Assert.Single(backend.Draws);
                Assert.True(merged.Merged);
                Assert.True(merged.Complement);
                Assert.Equal(t * (1f - exit), merged.Dissolve, 5);
                AssertCoverage(backend.Draws, merged.Dissolve, null);
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

    [Theory]
    [InlineData(160f, 120f, 40f, false)]
    [InlineData(160f, 120f, 40f, true)]
    [InlineData(160f, 60f, 40f, false)]
    [InlineData(110f, 90f, 40f, false)]
    [InlineData(120f, 160f, 40f, true)]
    [InlineData(160f, 41f, 40f, false)]
    [InlineData(160f, 120f, 0f, false)]
    public void Exit_coverage_changes_only_by_a_skip_sliver_at_band_boundaries(
        float radius, float fadeWidth, float hlodWidth, bool shadowOnly)
    {
        // Regression for https://github.com/APKiwiOrg/KhaozEngine/issues/1542.
        PropLayer layer = Layer(radius, fadeWidth, 100f, hlodWidth, parts: shadowOnly);
        var backend = new RecordingBackend();
        using var renderer = Create(backend, layer);
        float start = layer.HlodDistance - layer.HlodCrossfadeWidth * .5f;
        float[] boundaries =
        {
            MathF.Max(0f, layer.DrawRadius - layer.FadeBandWidth),
            start, start + layer.HlodCrossfadeWidth,
            start + layer.HlodCrossfadeWidth * PropHlod.HlodMergedSkipAt,
            start + layer.HlodCrossfadeWidth * PropHlod.HlodPropsSkipAt,
            layer.DrawRadius,
        };
        const int samples = 2048;
        float sliver = MathF.Max(PropHlod.HlodMergedSkipAt, 1f - PropHlod.HlodPropsSkipAt);
        int allowedChanges = (int)MathF.Ceiling(samples * sliver) + 2;
        foreach (float boundary in boundaries)
        {
            bool[] before = KeptAt(MathF.Max(0f, boundary - .001f));
            foreach (float distance in new[] { boundary, boundary + .001f })
            {
                bool[] after = KeptAt(distance);
                int removed = 0, restored = 0;
                for (int sample = 0; sample < samples; sample++)
                {
                    if (before[sample] && !after[sample]) removed++;
                    if (!before[sample] && after[sample]) restored++;
                }
                // A skip can move one sliver between phases, but cannot reverse the noise domain.
                Assert.True(removed <= allowedChanges && restored <= allowedChanges,
                    $"At {boundary}, removed {removed} and restored {restored} of {samples} fixed noise samples.");
            }
        }

        bool[] KeptAt(float distance)
        {
            backend.Draws.Clear();
            Vector3 focus = new(distance, 0f, 0f);
            if (shadowOnly) renderer.DrawWithVisibility(focus, new Hidden());
            else renderer.Draw(focus);
            var kept = new bool[samples];
            for (int sample = 0; sample < samples; sample++)
            {
                float noise = sample / (float)samples;
                foreach (Draw draw in backend.Draws)
                    kept[sample] |= draw.Complement ? noise < draw.Dissolve : noise >= draw.Dissolve;
            }
            return kept;
        }
    }

    [Theory]
    [InlineData(110f, 90f, 40f, false, false)]
    [InlineData(110f, 90f, 40f, true, true)]
    [InlineData(40f, 20f, 40f, false, true)]
    [InlineData(40f, 20f, 40f, true, false)]
    [InlineData(160f, 120f, 0f, false, false)]
    [InlineData(160f, 120f, 0f, true, true)]
    public void Exits_before_the_handoff_submit_each_surviving_sample_once(
        float radius, float fadeWidth, float hlodWidth, bool shadowOnly, bool parts)
    {
        // Regression for https://github.com/APKiwiOrg/KhaozEngine/issues/1807.
        PropLayer layer = Layer(radius, fadeWidth, 100f, hlodWidth, parts);
        var backend = new RecordingBackend();
        using var renderer = Create(backend, layer);
        float exitStart = MathF.Max(0f, radius - fadeWidth);
        float handoffStart = layer.HlodDistance - hlodWidth * .5f;
        const int samples = 2048;

        for (int step = 0; step <= 128; step++) CoverageAt(radius * step / 128f);
        foreach (float boundary in new[] { exitStart, handoffStart })
        {
            int[] before = CoverageAt(boundary - .001f);
            foreach (float distance in new[] { boundary, boundary + .001f })
            {
                int[] after = CoverageAt(distance);
                int changed = 0;
                for (int sample = 0; sample < samples; sample++)
                    if (before[sample] != after[sample]) changed++;
                // Ordinary exit progress can cross one sampled threshold, but the handoff cannot jump sets.
                Assert.True(changed <= 1, $"At {boundary}, {changed} fixed noise samples changed ownership.");
            }
            int[] atBoundary = CoverageAt(boundary);
            AssertOrdinaryExit(atBoundary, boundary);
        }

        int[] CoverageAt(float distance)
        {
            backend.Draws.Clear();
            Vector3 focus = new(distance, 0f, 0f);
            if (shadowOnly) renderer.DrawWithVisibility(focus, new Hidden());
            else renderer.Draw(focus);
            var owners = new int[samples];
            for (int sample = 0; sample < samples; sample++)
            {
                float noise = sample / (float)samples;
                foreach (Draw draw in backend.Draws)
                    if (draw.Complement ? noise < draw.Dissolve : noise >= draw.Dissolve) owners[sample]++;
                Assert.InRange(owners[sample], 0, 1);
            }
            if (distance < handoffStart)
            {
                Assert.DoesNotContain(backend.Draws, draw => draw.Merged);
                AssertOrdinaryExit(owners, distance);
            }
            return owners;
        }

        void AssertOrdinaryExit(int[] owners, float distance)
        {
            float exit = Math.Clamp((distance - exitStart) / (radius - exitStart), 0f, 1f);
            for (int sample = 0; sample < samples; sample++)
                Assert.Equal(distance < radius && sample / (float)samples >= exit ? 1 : 0, owners[sample]);
        }
    }

    [Theory]
    [InlineData(500f, 40f, 40f, false)]
    [InlineData(500f, 40f, 40f, true)]
    [InlineData(160f, 120f, 0f, false)]
    [InlineData(160f, 120f, 0f, true)]
    [InlineData(160f, 41f, 40f, false)]
    [InlineData(160f, 41f, 40f, true)]
    public void Unpaired_exit_profiles_keep_every_draw_state_float_bit(
        float radius, float fadeWidth, float hlodWidth, bool shadowOnly)
    {
        // Regression for https://github.com/APKiwiOrg/KhaozEngine/issues/1542.
        PropLayer layer = Layer(radius, fadeWidth, 100f, hlodWidth, parts: false);
        var backend = new RecordingBackend();
        using var renderer = Create(backend, layer);
        float start = layer.HlodDistance - layer.HlodCrossfadeWidth * .5f;
        float end = start + layer.HlodCrossfadeWidth;
        float exit = layer.DrawRadius - layer.FadeBandWidth;
        // The hard swap is halfway through its exit. The late exit starts at t = 39/40,
        // one metre before the crossfade ends, with 41 metres of exit fade remaining.
        (float Distance, PropClusterDrawState State)[] samples = hlodWidth == 0f
        ? new[]
        {
            (0f, State(true, 0f, false, 0f, false)),
            // Regression for #1807: no merged draw before the hard swap.
            (exit, State(true, 0f, false, 0f, false)),
            (start, State(false, 1f, true, .5f, false)),
            (layer.DrawRadius, State(false, 0f, false, 0f, false)),
        }
        : exit < end
        ? new[]
        {
            (0f, State(true, 0f, false, 0f, false)),
            (start, State(true, 0f, false, 0f, false)),
            (layer.HlodDistance, State(true, .5f, true, .5f, true)),
            (exit, State(false, .975f, true, 0f, false)),
            (end, State(false, 1f, true, 1f / 41f, false)),
            (exit + layer.FadeBandWidth * .5f, State(false, 1f, true, .5f, false)),
            (layer.DrawRadius, State(false, 0f, false, 0f, false)),
        }
        : new[]
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

    static void AssertCoverage(List<Draw> draws, float lower, float? upper)
    {
        // Include exact boundaries, where the shader's strict complement comparison matters.
        var noiseValues = new List<float> { lower };
        if (upper is float boundary) noiseValues.Add(boundary);
        for (int sample = 0; sample < 128; sample++) noiseValues.Add(sample / 128f);
        foreach (float noise in noiseValues)
        {
            int owners = 0;
            foreach (Draw draw in draws)
                if (draw.Complement ? noise < draw.Dissolve : noise >= draw.Dissolve) owners++;
            Assert.Equal(noise < lower || (upper is float ceiling && noise >= ceiling) ? 1 : 0, owners);
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
