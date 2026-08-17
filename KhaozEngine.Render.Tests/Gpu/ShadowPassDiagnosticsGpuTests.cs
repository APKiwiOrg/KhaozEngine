using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// End-to-end proof that <see cref="Scene3D.LastShadowPassDiagnostics"/> reports what the depth pass ACTUALLY
    /// did, driven through a live render (issue #410's instrument). The headless truth table for the reason bits is
    /// KhaozEngine.Tests.Render3D.ShadowPassDiagnosticsTests. What only a real pass can prove is the other half:
    /// that <see cref="ShadowPassDiagnostics.Rendered"/> agrees with whether the pass recorded, and that the counts
    /// are the ones the pass walked.
    /// <para>
    /// Each test drives ONE reason and asserts the snapshot names exactly it. The stationary skinned-caster row is
    /// the load-bearing one: it pins the CURRENT behaviour, in which a scene where nothing moves still re-records
    /// the whole atlas every frame because a skinned caster is present. That is the suspected waste #410 is
    /// measuring, so this test is what gives an eventual fix a before and an after.
    /// </para>
    /// <para>
    /// <see cref="ShadowPassDiagnostics.ResolutionChanged"/> has no row here on purpose:
    /// <c>ShadowSettings.ShadowMapResolution</c> is a construction-time knob, so no running scene can change it
    /// between two passes. Every test below asserts it stays false, and the bit itself is covered headless.
    /// </para>
    /// </summary>
    public sealed class ShadowPassDiagnosticsGpuTests
    {
        const int W = 256, H = 200;

        static void ConfigureShadowScene(Scene3D scene)
        {
            scene.Post.Starfield = false;
            scene.Post.BackgroundColor = new Color(0.10f, 0.12f, 0.16f, 1f);
            scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
            scene.Post.Quality.Shadows.ShadowNearDistance = 5f;
            scene.Post.LightDirection = new Vector3(-0.55f, -0.8f, -0.25f);
            scene.Camera.Frame(new Vector3(0.2f, 0.4f, 0f), new Vector3(6f, 4.5f, 6f));
        }

        // Every reason bit except the one named, so a test can say "only this".
        static void AssertOnlyReason(ShadowPassDiagnostics d, string reason)
        {
            Assert.True(d.Active, "the shadow tier must resolve to ShadowMap for these tests to mean anything");
            Assert.False(d.ResolutionChanged, "the atlas resolution cannot change on a live scene");
            if (reason != nameof(d.AnySkinnedCaster)) Assert.False(d.AnySkinnedCaster);
            if (reason != nameof(d.LightMatrixChanged)) Assert.False(d.LightMatrixChanged);
            if (reason != nameof(d.CasterDataChanged)) Assert.False(d.CasterDataChanged);
            if (reason != nameof(d.SkinnedCastersCleared)) Assert.False(d.SkinnedCastersCleared);
        }

        [GpuFact]
        public void First_frame_names_the_missing_atlas_and_counts_what_it_drew()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            ConfigureShadowScene(preview.Scene);
            MeshHandle floor = preview.Scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
            MeshHandle box = preview.Scene.LoadMesh(MeshPrimitives.Box(1.4f));

            preview.Capture(s =>
            {
                s.Draw(floor, Matrix4x4.Identity);
                s.Draw(box, Matrix4x4.CreateTranslation(-1.2f, 0.7f, -0.4f), new Color(0.15f, 0.75f, 0.2f, 1f));
            });

            ShadowPassDiagnostics d = preview.Scene.LastShadowPassDiagnostics;
            Assert.True(d.Rendered);
            Assert.False(d.Skipped);
            Assert.False(d.HadPrevious);                 // the one reason a first frame has
            AssertOnlyReason(d, reason: "");
            Assert.False(preview.Scene.ShadowPassSkippedLastFrame);

            // The pass recorded, so the counts must be real: two casters, both reaching at least the near cascade.
            Assert.True(d.CascadeCount > 0);
            Assert.True(d.TotalRigidSpanCount > 0, $"a rendered pass must report the spans it walked, got {d.TotalRigidSpanCount}");
            Assert.True(d.RigidDrawCalls > 0, $"a rendered pass must report the draws it issued, got {d.RigidDrawCalls}");
            Assert.True(d.RigidDrawCalls <= d.TotalRigidSpanCount,
                $"a draw is issued per walked span at most, got {d.RigidDrawCalls} draws over {d.TotalRigidSpanCount} spans");
            Assert.Equal(0, d.SkinnedDrawCalls);         // no skinned casters in this scene
            Assert.Equal(d.RigidDrawCalls + d.SkinnedDrawCalls, d.TotalDrawCalls);

            // Per-cascade spans must sum to the total, and nothing may be reported past the active cascade count.
            int summed = 0;
            for (int c = 0; c < ShadowSettings.MaxCascades; c++)
            {
                if (c >= d.CascadeCount) Assert.Equal(0, d.RigidSpanCount(c));
                summed += d.RigidSpanCount(c);
            }
            Assert.Equal(d.TotalRigidSpanCount, summed);
        }

        [GpuFact]
        public void Stationary_scene_skips_and_reports_zero_recorded_work()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            ConfigureShadowScene(preview.Scene);
            MeshHandle floor = preview.Scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
            MeshHandle box = preview.Scene.LoadMesh(MeshPrimitives.Box(1.4f));

            void DrawStatic(Scene3D s)
            {
                s.Draw(floor, Matrix4x4.Identity);
                s.Draw(box, Matrix4x4.CreateTranslation(-1.2f, 0.7f, -0.4f), new Color(0.15f, 0.75f, 0.2f, 1f));
            }

            preview.Capture(DrawStatic);
            int renderedSpans = preview.Scene.LastShadowPassDiagnostics.TotalRigidSpanCount;
            preview.Capture(DrawStatic);

            ShadowPassDiagnostics d = preview.Scene.LastShadowPassDiagnostics;
            Assert.True(d.Skipped);
            Assert.False(d.Rendered);
            Assert.True(d.HadPrevious);
            AssertOnlyReason(d, reason: "");

            // A skipped frame recorded nothing, so it must report nothing - NOT the previous pass's numbers, which
            // is exactly the lie a live counter would tell. Scene3D.ShadowCascadeSpanCount still holds those.
            Assert.True(renderedSpans > 0);
            Assert.Equal(0, d.TotalRigidSpanCount);
            Assert.Equal(0, d.RigidDrawCalls);
            Assert.Equal(0, d.SkinnedDrawCalls);
            Assert.Equal(0, d.TotalDrawCalls);
            int liveSpans = 0;
            for (int c = 0; c < ShadowSettings.MaxCascades; c++) liveSpans += preview.Scene.ShadowCascadeSpanCount(c);
            Assert.True(liveSpans > 0,
                "the live per-cascade property keeps reporting the last RENDERED pass across a skip");
        }

        [GpuFact]
        public void Stationary_scene_with_a_skinned_caster_still_renders_every_frame()
        {
            // THE LOAD-BEARING ROW. Nothing moves: same camera, same light, same rigid casters, the same bone pose
            // pushed twice. The pass re-records the whole atlas anyway, and the snapshot must say WHY in a way a
            // field trace can read. If a later change makes a stationary skinned scene skip, this test is the
            // before-and-after: it fails here, deliberately, rather than the regression passing unnoticed.
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            ConfigureShadowScene(preview.Scene);
            MeshHandle floor = preview.Scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
            var limb = new SkinnedLimb(preview.Scene, radius: 0.4f, length: 2.5f, ringSegments: 8, radialSegments: 8,
                boneCount: 5, ChainConfig.Writhe, Axis.Z);

            void DrawWithLimb(Scene3D s)
            {
                s.Draw(floor, Matrix4x4.Identity);
                limb.Draw(s, Matrix4x4.CreateTranslation(0f, 0.8f, 0f), new Color(0.8f, 0.4f, 0.3f, 1f));
            }

            limb.Update(new Vector3(0f, 0.8f, 0f), Vector3.UnitZ, Vector3.UnitY, 1.0f);
            preview.Capture(DrawWithLimb);               // first frame: renders because there is no prior atlas
            preview.Capture(DrawWithLimb);               // second frame: identical, and STILL renders
            preview.Capture(DrawWithLimb);               // third: prove it is every frame, not just the second

            ShadowPassDiagnostics d = preview.Scene.LastShadowPassDiagnostics;
            Assert.True(d.Rendered, "a stationary scene holding a skinned caster re-records the atlas every frame");
            Assert.False(d.Skipped);
            Assert.True(d.HadPrevious);
            Assert.True(d.AnySkinnedCaster);
            Assert.True(d.SkinnedCasterCount > 0);
            AssertOnlyReason(d, nameof(d.AnySkinnedCaster));   // the skinned caster is the ONLY reason left
            Assert.False(preview.Scene.ShadowPassSkippedLastFrame);

            // And the cost of that decision is reported, which is what makes it measurable in the field: a skinned
            // caster is drawn into every cascade unconditionally, so its draws scale with the cascade count.
            Assert.Equal(d.SkinnedCasterCount * d.CascadeCount, d.SkinnedDrawCalls);
            Assert.True(d.TotalDrawCalls > d.SkinnedDrawCalls, "the floor is a rigid caster and must be drawn too");

            limb.Dispose();
        }

        [GpuFact]
        public void Moving_light_names_the_matrix_and_still_reports_its_draws()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            ConfigureShadowScene(preview.Scene);
            MeshHandle floor = preview.Scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
            MeshHandle box = preview.Scene.LoadMesh(MeshPrimitives.Box(1.2f));

            void DrawStatic(Scene3D s)
            {
                s.Draw(floor, Matrix4x4.Identity);
                s.Draw(box, Matrix4x4.CreateTranslation(-1.4f, 0.6f, -0.4f), new Color(0.15f, 0.75f, 0.2f, 1f));
            }

            preview.Capture(DrawStatic);
            preview.Scene.Post.LightDirection = new Vector3(-0.35f, -0.8f, -0.45f);
            preview.Capture(DrawStatic);

            ShadowPassDiagnostics d = preview.Scene.LastShadowPassDiagnostics;
            Assert.True(d.Rendered);
            Assert.True(d.HadPrevious);
            Assert.True(d.LightMatrixChanged);
            AssertOnlyReason(d, nameof(d.LightMatrixChanged));   // the casters did not move, only the sun
            Assert.True(d.RigidDrawCalls > 0);
            Assert.Equal(0, d.SkinnedDrawCalls);
        }

        [GpuFact]
        public void Moved_caster_names_the_caster_data_and_a_re_settled_one_stops()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            ConfigureShadowScene(preview.Scene);
            MeshHandle floor = preview.Scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
            MeshHandle box = preview.Scene.LoadMesh(MeshPrimitives.Box(1.2f));

            void Frame(float x) => preview.Capture(s =>
            {
                s.Draw(floor, Matrix4x4.Identity);
                s.Draw(box, Matrix4x4.CreateTranslation(x, 0.6f, -0.4f), new Color(0.15f, 0.75f, 0.2f, 1f));
            });

            Frame(-1.4f);
            Frame(1.4f);

            ShadowPassDiagnostics moved = preview.Scene.LastShadowPassDiagnostics;
            Assert.True(moved.Rendered);
            Assert.True(moved.CasterDataChanged);
            AssertOnlyReason(moved, nameof(moved.CasterDataChanged));   // the light held still, the caster did not
            Assert.True(moved.RigidDrawCalls > 0);

            Frame(1.4f);                                  // re-settled: nothing changed since the last rendered pass
            ShadowPassDiagnostics settled = preview.Scene.LastShadowPassDiagnostics;
            Assert.True(settled.Skipped);
            Assert.False(settled.CasterDataChanged);
            Assert.Equal(0, settled.TotalDrawCalls);
        }

        [GpuFact]
        public void Cascade_cull_off_reports_the_uncalled_span_load()
        {
            // The counts must track the pass's real shape rather than a derived guess: with the per-cascade cull
            // off, every cascade walks the whole caster list, so the per-cascade span counts must all be equal.
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            ConfigureShadowScene(preview.Scene);
            preview.Scene.ShadowCascadeCulling = false;
            MeshHandle floor = preview.Scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
            MeshHandle box = preview.Scene.LoadMesh(MeshPrimitives.Box(1.2f));

            preview.Capture(s =>
            {
                s.Draw(floor, Matrix4x4.Identity);
                s.Draw(box, Matrix4x4.CreateTranslation(-1.4f, 0.6f, -0.4f), new Color(0.15f, 0.75f, 0.2f, 1f));
            });

            ShadowPassDiagnostics d = preview.Scene.LastShadowPassDiagnostics;
            Assert.True(d.Rendered);
            int first = d.RigidSpanCount(0);
            Assert.True(first > 0);
            for (int c = 1; c < d.CascadeCount; c++)
                Assert.Equal(first, d.RigidSpanCount(c));
            Assert.Equal(first * d.CascadeCount, d.TotalRigidSpanCount);
            Assert.Equal(d.TotalRigidSpanCount, d.RigidDrawCalls);   // every walked span is drawn, none was stale
        }
    }
}
