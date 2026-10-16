using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageRenderContextTests
{
    [Fact]
    public void Late_render_refusal_selects_one_current_pose_fallback()
    {
        using var h = new CoverageSceneFixture();
        h.Begin();
        var frame = h.Stage();
        h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette);
        h.Outline(h.HostDraw(frame));
        h.SubmitEquipment();
        h.Scene.CameraOverride = new SingularCamera();
        h.Render();
        Assert.Equal(CoveragePresentation.UncoveredFallback, h.Scene.GetCoverageOutcome(frame).Presentation);
        Assert.NotEqual(CoverageFailureCode.None, h.Scene.GetCoverageOutcome(frame).Failure.Code);
        Assert.Equal(h.Posed, h.Scene.CoverageVerticesForTests(frame).ToArray());
        h.AssertFallbackDraws(frame);
    }

    [Fact]
    public void All_pass_matrices_precede_first_covered_draw()
    {
        using var h = new CoverageSceneFixture();
        h.EnablePointShadows();
        h.Begin();
        h.AddPoint();
        var frame = h.Stage();
        var draw = h.HostDraw(frame);
        h.Scene.DrawSkinned(draw, h.Palette);
        h.Outline(draw);
        h.SubmitEquipment();
        int coveredDraws = 0;
        h.BeforeDraw = recorded =>
        {
            if (!h.Scene.IsCoverageIndexBufferForTests(frame, recorded.IndexBuffer)) return;
            Assert.Equal(CoveragePresentation.Covered, h.Scene.GetCoverageOutcome(frame).Presentation);
            Assert.True(h.Scene.CoveragePassesForTests(frame).Length >= 16);
            coveredDraws++;
        };
        h.Render();
        Assert.True(coveredDraws >= 10);
        Assert.Equal(CoveragePresentation.Covered, h.Scene.GetCoverageOutcome(frame).Presentation);
        h.AssertCanonicalDraws(frame);
        h.AssertCapturedMatricesUploaded(frame);
        var passes = h.Scene.CoveragePassesForTests(frame).ToArray();
        Assert.True(passes.Length >= 16);
        Assert.Contains(passes, p => p.CommandId == 0 && p.PositionPerturbationBound > 0);
        Assert.Contains(passes, p => p.CommandId == 1 && p.PositionPerturbationBound > 0);
    }

    [Fact]
    public void Surface_scans_and_sphere_are_shared_across_passes()
    {
        using var h = new CoverageSceneFixture(seams: true);
        h.Scene.Post.Quality.Shadows.PointShadows.FaceResolution = 64;
        h.Scene.Post.Quality.Shadows.PointShadows.MaxShadowedLights = 2;
        void Lights()
        {
            h.AddPoint();
            h.Scene.AddLight(new(1, 0, 4), KhaozEngine.Primitives.Color.White, 25, 1, LightShadow.Dynamic);
        }
        h.Begin();
        Lights();
        h.Render();
        h.Begin();
        Lights();
        var frame = h.Stage();
        long staged = h.Scene.CoverageSurfaceScansForTests;
        for (int slice = 0; slice < 2; slice++)
        {
            h.Scene.DrawSkinned(h.HostDraw(frame, slice: slice), h.Palette);
            h.Outline(h.HostDraw(frame, slice: slice));
        }
        h.SubmitEquipment();
        h.Render();
        Assert.Equal(CoveragePresentation.Covered, h.Scene.GetCoverageOutcome(frame).Presentation);
        Assert.Equal(2, h.Scene.PointShadowedLights);
        Assert.True(h.Scene.CoveragePassesForTests(frame).Length >= 2 * 2 * 6);
        Assert.Equal(staged, h.Scene.CoverageSurfaceScansForTests);
        Assert.Equal(1, h.Scene.CoverageSphereEvaluationsForTests);
    }

    [Fact]
    public void Origin_or_view_change_rejects_stale_context()
    {
        using var h = new CoverageSceneFixture();
        h.Begin();
        var frame = h.Stage();
        h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette);
        h.SubmitEquipment();
        h.Render();
        h.Scene.CameraOverride = new SingularCamera();
        Assert.Throws<InvalidOperationException>(() => h.Render());
        h.Scene.Begin();
        Assert.Throws<InvalidOperationException>(() => h.Scene.GetCoverageOutcome(frame));
    }

    [Fact]
    public void Prepared_bounds_cover_shader_position_inputs()
    {
        using var h = new CoverageSceneFixture();
        h.Begin();
        var frame = h.Stage();
        h.Scene.DrawSkinned(h.HostDraw(frame) with { Material = Material.Shiny(1e30f) }, h.Palette);
        h.SubmitEquipment();
        h.Render();
        var passes = h.Scene.CoveragePassesForTests(frame).ToArray();
        Assert.Contains(passes, p => p.CommandId == 0 && p.PositionPerturbationBound >= 1);
        Assert.Equal(CoveragePresentation.UncoveredFallback, h.Scene.GetCoverageOutcome(frame).Presentation);
    }

    [Fact]
    public void Late_fallback_visibility_uses_the_current_pose_union()
    {
        using var h = new CoverageSceneFixture();
        h.Scene.FrustumCulling = true;
        h.Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        h.Scene.Camera.Frame(new(10, 0, 0), new(2, 2, 2));
        h.Begin();
        Matrix4x4 shift = Matrix4x4.CreateTranslation(10, 0, 0);
        h.Palette[0] *= shift;
        h.Palette[1] *= shift;
        var frozen = new RigidInstanceDraw[1];
        Assert.True(h.Scene.TryStageCoverageEquipment(MotionKey.From(10), 1, 1, h.Support,
            [new RigidInstanceDraw(h.RigidHandle, shift)], frozen, out var failure), failure.ToString());
        var commands = h.CommandPoses;
        commands[0] = commands[0] with { EquipmentWorld = shift, CoverageWorld = h.Support.CoverageLocal * shift };
        Assert.True(h.Conformer.TryPrepare(h.Palette, Matrix4x4.Identity, commands, MotionKey.From(10),
            CoverageFallback.UncoveredCurrentPose, out var frame, out failure), failure.ToString());
        h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette);
        h.Render();
        Assert.Equal(CoveragePresentation.UncoveredFallback, h.Scene.GetCoverageOutcome(frame).Presentation);
        Assert.Equal(1, h.Scene.CoverageDiagnosticsForTests.UncoveredGpuDraws);
        Assert.Equal(1, h.Scene.DrawnSkinnedInstances);
        h.AssertFallbackDraws(frame);
    }

    sealed class SingularCamera : IIsoCamera3D
    {
        public Vector3 Eye => new(0, 0, 4);
        public Vector3 Forward => -Vector3.UnitZ;
        public Matrix4x4 View => Matrix4x4.Identity;
        public Matrix4x4 Projection => default;
        public Matrix4x4 ViewProjection => default;
        public bool WorldToScreen(Vector3 world, int width, int height, out Vector2 screen)
        { screen = default; return false; }
    }
}
