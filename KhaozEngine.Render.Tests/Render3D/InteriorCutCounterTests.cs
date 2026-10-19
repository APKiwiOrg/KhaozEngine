using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D;

public sealed class InteriorCutCounterTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void UnchangedFieldsDoNotRepeatRelevanceOrFragmentMeshRuns(bool typical, bool temporal)
        => VerifyCounters(typical, temporal, false);

    [Fact]
    public void CulledVillageDoesNotAmplifyInteriorPartitionRuns()
        => VerifyCounters(true, false, true);

    [Fact]
    public void DenseVillageCulledCountersAreRecorded()
        => VerifyCounters(false, false, true);

    void VerifyCounters(bool typical, bool temporal, bool culling)
    {
        var inputs = InteriorCutCostScene.Create(typical ? InteriorCutCostScene.Layout.TypicalVillage
            : InteriorCutCostScene.Layout.DenseVillage);
        var results = new Dictionary<InteriorCutCostPerfGpuTests.Mode, InteriorCutCommandCounters>();
        foreach (var mode in Enum.GetValues<InteriorCutCostPerfGpuTests.Mode>())
        {
            using var stage = new CounterStage(inputs, mode, temporal, culling);
            stage.Frame();
            InteriorCutCommandCounters counters = stage.Frame();
            results.Add(mode, counters);
            foreach (string pass in new[] { "colour", "temporal", "cascade0", "cascade1", "cascade2", "cascade3",
                "point", "outline", "silhouette" })
                output.WriteLine($"{(typical ? "TypicalVillage" : "DenseVillage")} {mode} {(culling ? "culled" : temporal ? "TAA" : "plain")} {pass} {counters.For(pass)}");
        }
        var baseline = results[InteriorCutCostPerfGpuTests.Mode.NoOptIn];
        var empty = results[InteriorCutCostPerfGpuTests.Mode.EmptyField];
        var village = results[InteriorCutCostPerfGpuTests.Mode.VillageInteriors];
        if (culling && !typical)
        {
            // IC7 records dense costs. Coherence-preserving packing is tracked in KhaozEngine #1498.
            foreach (string pass in baseline.Passes.Keys)
            {
                var expected = baseline.For(pass);
                var actual = empty.For(pass);
                Assert.Equal((expected.Evaluations, expected.Lookups, expected.Draws, expected.Switches, expected.Binds),
                    (actual.Evaluations, actual.Lookups, actual.Draws, actual.Switches, actual.Binds));
            }
            return;
        }
        var failures = new List<string>();
        foreach (var pair in baseline.MeshRuns)
            if (village.MeshRuns.GetValueOrDefault(pair.Key) > pair.Value + 1)
                failures.Add($"{pair.Key}: village mesh runs {village.MeshRuns[pair.Key]}, baseline {pair.Value}");
        foreach (string pass in baseline.Passes.Keys)
        {
            var expected = baseline.For(pass);
            var actual = empty.For(pass);
            if (actual.Evaluations != 0) failures.Add($"{pass}: empty evaluations {actual.Evaluations}");
            if (actual.Lookups > 1) failures.Add($"{pass}: empty lookups {actual.Lookups}");
            if (expected.Draws != actual.Draws) failures.Add($"{pass}: empty draw count changed");
            if (expected.Switches != actual.Switches) failures.Add($"{pass}: empty switches changed");
            if (expected.Binds != actual.Binds) failures.Add($"{pass}: empty binds changed");
            // Only two meshes opt in. Each may add at most one extra draw per pass (six point faces).
            int extra = village.For(pass).Draws - expected.Draws;
            if (extra > (pass == "point" ? 12 : 2)) failures.Add($"{pass}: village extra draws {extra}");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    sealed class CounterStage : IDisposable
    {
        readonly MotionTestScene _rig = new();
        readonly InteriorCutCostScene _inputs;
        readonly InteriorCutCostPerfGpuTests.Mode _mode;
        readonly MeshHandle _ground, _box, _card;
        readonly bool _temporal;
        int _frame;
        Scene3D Scene => _rig.Scene;

        internal CounterStage(InteriorCutCostScene inputs, InteriorCutCostPerfGpuTests.Mode mode, bool temporal, bool culling)
        {
            _inputs = inputs;
            _mode = mode;
            _temporal = temporal;
            Scene.ForceTemporalForTests = temporal;
            Scene.Post.Outline = false;
            Scene.Post.Quality.Shadows.Mode = mode == InteriorCutCostPerfGpuTests.Mode.ShadowsOff ? ShadowMode.Off : ShadowMode.ShadowMap;
            Scene.Post.Quality.Shadows.ShadowLightHoldTexels = 0;
            Scene.RequestShadowMapLayout(256, 4);
            Scene.FrustumCulling = culling;
            Scene.ShadowCascadeCulling = culling;
            Scene.RenderOrigin = Vector3.Zero;
            Scene.Camera.Azimuth = .6f;
            Scene.Camera.Elevation = .55f;
            Scene.Camera.Frame(Vector3.Zero, new Vector3(250, 20, 250));
            Scene.InteriorCut = inputs.FieldFor(mode);
            _ground = Scene.LoadMesh(MeshPrimitives.Tile(500, .2f));
            _box = Scene.LoadMesh(MeshPrimitives.Box());
            var leaves = Scene.LoadTexture(new byte[8 * 8 * 4], 8, 8);
            _card = Scene.LoadMesh(MeshPrimitives.Plane(10, 14), new Scene3D.SurfaceMaps(leaves, alphaCutoff: .5f));
            Assert.True(Scene.EnsurePointShadowAtlas(64, 1));
        }

        internal InteriorCutCommandCounters Frame()
        {
            var counters = new InteriorCutCommandCounters();
            var cache = HighlightTestAccess.Field<InteriorCutRelevanceCache>(Scene, "_interiorRelevance");
            Scene.Begin();
            Scene.Post.LightDirection = Vector3.Normalize(new Vector3(1, -1, ++_frame * .01f));
            void Measure(string pass, Action submit)
            {
                int evaluations = cache.Evaluations + Scene.InteriorMarginEvaluations, lookups = Lookups();
                submit();
                counters.For(pass).Evaluations += cache.Evaluations + Scene.InteriorMarginEvaluations - evaluations;
                counters.For(pass).Lookups += Lookups() - lookups;
            }
            int Lookups() => cache.Lookups + Scene.InteriorMarginLookups;
            Measure(_temporal ? "temporal" : "colour", SubmitVillage);
            Measure("outline", () => Scene.DrawMeshOutlineDissolved(Scene.BeginMeshOutline(Color.White, 1),
                _box, Matrix4x4.Identity, 0, false, _inputs.CutsFor(_mode)));
            Measure("silhouette", () => Scene.DrawMeshSilhouette(_box, Matrix4x4.Identity, Color.White, .05f, _inputs.CutsFor(_mode)));
            Scene.PrepareFrame();
            using var commands = new RecordingGpuCommandList(new NullGpuCommandList()) { CommandTrace = new() };
            Scene.RenderInternal(commands, MotionTestScene.Width, MotionTestScene.Height, _rig.Target);
            Scene.BeginPointShadowFrame(1);
            Scene.PackPointShadowSlot(0, 0, new Vector3(0, 20, 0), 1000);
            Scene.UploadPointShadowFaces(commands);
            Scene.RenderPointShadowSlots(commands);
            counters.Read(commands.CommandTrace);
            var data = HighlightTestAccess.Field<List<ModelRenderer.InstanceData>>(Scene, "_instanceData");
            void RecordRuns(string pass, IEnumerable<Scene3D.ShadowCasterSpan> spans)
            {
                foreach (var group in spans.GroupBy(span => span.Index))
                    counters.MeshRuns.Add($"{pass}/mesh{group.Key}", group.Count());
                foreach (var span in spans)
                    for (uint slot = span.Start; slot < span.Start + span.Count; slot++)
                        Assert.Equal(data[(int)slot].SpecParams.W > .5f, span.Kind == ShadowCastKind.Dissolving);
            }
            var cascades = HighlightTestAccess.Field<List<Scene3D.ShadowCasterSpan>[]>(Scene, "_cascadeCasterSpans");
            if (_mode != InteriorCutCostPerfGpuTests.Mode.ShadowsOff)
                for (int cascade = 0; cascade < 4; cascade++) RecordRuns($"cascade{cascade}", cascades[cascade]);
            RecordRuns("point", Scene.DebugPointCasterSpans(new Vector3(0, 20, 0), 1000));
            return counters;
        }

        void SubmitVillage()
        {
            Scene.Draw(_ground, Matrix4x4.CreateTranslation(0, -.1f, 0), Color.White, Material.None, false);
            foreach (InteriorVolume interior in _inputs.Interiors)
            {
                Vector2 centre = (interior.Outline[0] + interior.Outline[2]) * .5f;
                Scene.Draw(_box, Matrix4x4.CreateScale(24, 9, 24) * Matrix4x4.CreateTranslation(centre.X, 4.5f, centre.Y));
            }
            foreach (var tree in _inputs.Trees)
            {
                Matrix4x4 world = Matrix4x4.CreateScale(tree.Scale) * Matrix4x4.CreateRotationY(tree.Yaw)
                    * Matrix4x4.CreateTranslation(tree.X, tree.Y, tree.Z);
                Scene.Draw(new RigidInstanceDraw(_box, Matrix4x4.CreateScale(.8f, 12, .8f)
                    * Matrix4x4.CreateTranslation(0, 6, 0) * world)
                { CutInsideInteriors = _inputs.CutsFor(_mode) });
                for (int side = 0; side < 2; side++)
                    Scene.Draw(new RigidInstanceDraw(_card, Matrix4x4.CreateRotationX(MathF.PI / 2)
                        * Matrix4x4.CreateRotationY(side * MathF.PI / 2) * Matrix4x4.CreateTranslation(0, 11, 0) * world)
                    { CutInsideInteriors = _inputs.CutsFor(_mode) });
            }
        }

        public void Dispose() => _rig.Dispose();
    }
}
