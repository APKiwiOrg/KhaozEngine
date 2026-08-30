using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Headless coverage of the shadow-caster policy (issue #287, plus the per-mesh terrain rule of issue #280):
    /// the per-mesh cast test (<see cref="Scene3D.MeshCastsShadows"/>), the per-instance classification
    /// (<see cref="Scene3D.ClassifyCaster"/>), how <see cref="Scene3D.GroupInstances"/> carries it onto the uploaded
    /// slots, and the span builder (<see cref="Scene3D.AppendCasterSpans"/>) that turns it into the depth pass's
    /// draw list. The pass itself needs a GPU, but what it DRAWS is decided entirely by these pure pieces, so they are
    /// asserted without a device (the pixel proof lives in ShadowCasterPolicyGpuTests).
    /// </summary>
    public sealed class ShadowCasterPolicyTests
    {
        static SceneInstances.Instance Inst(int mesh, float tx, float dissolve = 0f, bool castsShadows = true)
            => new(new MeshHandle(mesh), Matrix4x4.CreateTranslation(tx, 0, 0), Color.White, Material.None,
                dissolve, 0f, default, castsShadows);

        // Spans of one run of `kinds`, placed at absolute slot `start`. The classification list is indexed by ABSOLUTE
        // slot (it is parallel to the whole uploaded instance array), so the run's kinds are padded into place.
        static List<Scene3D.ShadowCasterSpan> Spans(uint start, uint count, params ShadowCastKind[] kinds)
        {
            var all = new List<ShadowCastKind>();
            for (uint i = 0; i < start; i++) all.Add(ShadowCastKind.Opaque);
            all.AddRange(kinds);
            var spans = new List<Scene3D.ShadowCasterSpan>();
            Scene3D.AppendCasterSpans(7, 1, start, count, all, spans);
            return spans;
        }

        [Fact]
        public void Terrain_is_receive_only_until_the_scene_opts_in()
        {
            // Issue #280. The rule the caster walk applies per MESH, before the per-instance classification above
            // ever runs. A splat mesh is terrain, so it casts only with the scene flag on. A mesh with no splat
            // material (a model, tile ground, an HLOD cluster) always casts, either way.
            Assert.False(Scene3D.MeshCastsShadows(0, terrainCastsShadows: false));
            Assert.False(Scene3D.MeshCastsShadows(3, terrainCastsShadows: false));
            Assert.True(Scene3D.MeshCastsShadows(0, terrainCastsShadows: true));
            Assert.True(Scene3D.MeshCastsShadows(3, terrainCastsShadows: true));
            Assert.True(Scene3D.MeshCastsShadows(-1, terrainCastsShadows: false));
            Assert.True(Scene3D.MeshCastsShadows(-1, terrainCastsShadows: true));
        }

        [Fact]
        public void Classification_covers_the_three_cases()
        {
            Assert.Equal(ShadowCastKind.Opaque, Scene3D.ClassifyCaster(Inst(0, 0f)));
            Assert.Equal(ShadowCastKind.Dissolving, Scene3D.ClassifyCaster(Inst(0, 0f, dissolve: 0.3f)));
            Assert.Equal(ShadowCastKind.None, Scene3D.ClassifyCaster(Inst(0, 0f, castsShadows: false)));
            // An opted-out instance is a non-caster whether or not it also dissolves: no shadow beats a thin one.
            Assert.Equal(ShadowCastKind.None, Scene3D.ClassifyCaster(Inst(0, 0f, dissolve: 0.3f, castsShadows: false)));
        }

        [Fact]
        public void Grouping_classifies_each_uploaded_slot()
        {
            // Interleaved meshes: the classification must follow each instance to its SCATTERED slot, not its
            // submission index, or the depth pass would skip the wrong props.
            var items = new List<SceneInstances.Instance>
            {
                Inst(5, 10f),                              // slot 0 (mesh 5 run)
                Inst(2, 20f, castsShadows: false),         // slot 2 (mesh 2 run)
                Inst(5, 11f, dissolve: 0.5f),              // slot 1
                Inst(2, 21f),                              // slot 3
            };
            var data = new List<ModelRenderer.InstanceData>();
            var runs = new List<Scene3D.MeshRun>();
            var kinds = new List<ShadowCastKind>();

            Scene3D.GroupInstances(items, data, runs, null, kinds);

            Assert.Equal(4, kinds.Count);
            Assert.Equal(ShadowCastKind.Opaque, kinds[0]);
            Assert.Equal(ShadowCastKind.Dissolving, kinds[1]);
            Assert.Equal(ShadowCastKind.None, kinds[2]);
            Assert.Equal(ShadowCastKind.Opaque, kinds[3]);
            // The dissolve rode onto the same slot the classification did.
            Assert.Equal(0.5f, data[1].Dissolve.X, 4);
        }

        [Fact]
        public void Grouping_without_a_kind_list_is_the_unchanged_call()
        {
            // The optional output is genuinely optional: omitting it must not disturb the grouping at all.
            var items = new List<SceneInstances.Instance> { Inst(1, 0f, castsShadows: false), Inst(1, 1f) };
            var data = new List<ModelRenderer.InstanceData>();
            var runs = new List<Scene3D.MeshRun>();
            Scene3D.GroupInstances(items, data, runs);
            Assert.Single(runs);
            Assert.Equal(2u, runs[0].Count);
        }

        [Fact]
        public void Grouping_clears_the_kind_list_between_frames()
        {
            var kinds = new List<ShadowCastKind> { ShadowCastKind.Dissolving, ShadowCastKind.None };
            var data = new List<ModelRenderer.InstanceData>();
            var runs = new List<Scene3D.MeshRun>();
            Scene3D.GroupInstances(new List<SceneInstances.Instance> { Inst(0, 0f) }, data, runs, null, kinds);
            Assert.Single(kinds);
            Assert.Equal(ShadowCastKind.Opaque, kinds[0]);

            // An empty frame empties it too (a stale classification would misroute the next frame's spans).
            Scene3D.GroupInstances(new List<SceneInstances.Instance>(), data, runs, null, kinds);
            Assert.Empty(kinds);
        }

        [Fact]
        public void An_all_opaque_run_is_one_span()
        {
            // The parity case: nothing opted out, nothing dissolving, so the pass draws the whole run in one call
            // exactly as it did before the policy existed.
            var spans = Spans(0, 3, ShadowCastKind.Opaque, ShadowCastKind.Opaque, ShadowCastKind.Opaque);
            Assert.Single(spans);
            Assert.Equal(new Scene3D.ShadowCasterSpan(7, 1, 0, 3, ShadowCastKind.Opaque), spans[0]);
        }

        [Fact]
        public void An_unclassified_run_is_one_opaque_span()
        {
            // No classification supplied (a GroupInstances call that omitted the list): the whole run casts, which is
            // the pre-policy behaviour and the safe direction to fail in.
            var spans = new List<Scene3D.ShadowCasterSpan>();
            Scene3D.AppendCasterSpans(7, 1, 4, 2, new List<ShadowCastKind>(), spans);
            Assert.Single(spans);
            Assert.Equal(new Scene3D.ShadowCasterSpan(7, 1, 4, 2, ShadowCastKind.Opaque), spans[0]);
        }

        [Fact]
        public void Opted_out_instances_produce_no_span()
        {
            var spans = Spans(0, 3, ShadowCastKind.None, ShadowCastKind.None, ShadowCastKind.None);
            Assert.Empty(spans);
        }

        [Fact]
        public void Opted_out_instances_split_the_run_around_them()
        {
            // Slots 0 and 3 cast, 1..2 do not: two spans, and the skipped stretch is never drawn into the atlas.
            var spans = Spans(0, 4, ShadowCastKind.Opaque, ShadowCastKind.None, ShadowCastKind.None, ShadowCastKind.Opaque);
            Assert.Equal(2, spans.Count);
            Assert.Equal(new Scene3D.ShadowCasterSpan(7, 1, 0, 1, ShadowCastKind.Opaque), spans[0]);
            Assert.Equal(new Scene3D.ShadowCasterSpan(7, 1, 3, 1, ShadowCastKind.Opaque), spans[1]);
        }

        [Fact]
        public void Dissolving_instances_split_into_their_own_span()
        {
            // The kinds bind different depth pipelines, so a run that mixes them splits at the boundary and each
            // stretch stays contiguous (the pass draws sub-spans of the already-uploaded buffer, never a reorder).
            var spans = Spans(2, 4, ShadowCastKind.Opaque, ShadowCastKind.Dissolving,
                              ShadowCastKind.Dissolving, ShadowCastKind.Opaque);
            Assert.Equal(3, spans.Count);
            Assert.Equal(new Scene3D.ShadowCasterSpan(7, 1, 2, 1, ShadowCastKind.Opaque), spans[0]);
            Assert.Equal(new Scene3D.ShadowCasterSpan(7, 1, 3, 2, ShadowCastKind.Dissolving), spans[1]);
            Assert.Equal(new Scene3D.ShadowCasterSpan(7, 1, 5, 1, ShadowCastKind.Opaque), spans[2]);
        }

        [Fact]
        public void Spans_start_at_the_runs_own_offset()
        {
            // A run that is not the first one starts partway into the flat instance array, so the spans must be
            // absolute slot indices, because they are handed straight to the instanced draw as instanceStart.
            var spans = Spans(6, 2, ShadowCastKind.Opaque, ShadowCastKind.Opaque);
            Assert.Single(spans);
            Assert.Equal(6u, spans[0].Start);
            Assert.Equal(2u, spans[0].Count);
        }

        [Fact]
        public void An_empty_run_produces_nothing()
        {
            var spans = new List<Scene3D.ShadowCasterSpan>();
            Scene3D.AppendCasterSpans(7, 1, 0, 0, new List<ShadowCastKind> { ShadowCastKind.Opaque }, spans);
            Assert.Empty(spans);
        }

        [Fact]
        public void Spans_append_across_runs()
        {
            // BuildShadowCasterSpans appends run after run into one list, in draw order: the second run's spans must
            // follow the first's rather than replace them.
            var kinds = new List<ShadowCastKind> { ShadowCastKind.Opaque, ShadowCastKind.None, ShadowCastKind.Dissolving };
            var spans = new List<Scene3D.ShadowCasterSpan>();
            Scene3D.AppendCasterSpans(7, 1, 0, 2, kinds, spans);
            Scene3D.AppendCasterSpans(9, 3, 2, 1, kinds, spans);
            Assert.Equal(2, spans.Count);
            Assert.Equal(new Scene3D.ShadowCasterSpan(7, 1, 0, 1, ShadowCastKind.Opaque), spans[0]);
            Assert.Equal(new Scene3D.ShadowCasterSpan(9, 3, 2, 1, ShadowCastKind.Dissolving), spans[1]);
        }
    }
}
