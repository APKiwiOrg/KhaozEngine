using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class CoverageComputeHistoryGpuTests
{
    [GpuTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Absent_incomplete_shadow_outline_and_dissolved_surfaces_never_become_history(int route)
    {
        using var h = new CoverageSceneFixture(seams: true, executeNative: true);
        h.Scene.DebugView = SceneDebugView.MotionVectors;
        Draw(h); h.Render();
        h.Begin();
        SkinnedCoverageFrame absent = default;
        if (route != 0)
        {
            absent = h.Stage(weight: .4f);
            for (int slice = 0; slice < h.Host.Length; slice++)
            {
                if (route == 6 && slice == 1) continue;
                var draw = h.HostDraw(absent, shadowOnly: route == 1, slice: slice)
                    with
                { Dissolve = route == 3 || route == 5 && slice == 1 ? 1 : 0 };
                if (route == 2 || route == 4 && slice == 1) h.Outline(draw);
                else h.Scene.DrawSkinned(draw, h.Palette);
            }
            h.SubmitEquipment();
        }
        h.Render();
        if (route != 0) Assert.False(absent.Preparation!.MotionPresented);
        var returned = Draw(h, .2f); h.Render();
        Assert.False(returned.Preparation!.MotionReusable);
        var drawReturned = h.NativeDraws.First();
        uint[] raw = CoverageSelectedReadback.Read(h.NativeDevice, drawReturned.Surface.Current);
        Assert.Equal(1u, raw[drawReturned.Surface.CurrentBase + 15]);
        _ = VerifySelection(h, drawReturned);
        Assert.DoesNotContain(h.Record.Uploads, u => ReferenceEquals(u.Buffer, drawReturned.Surface.Current)
            || ReferenceEquals(u.Buffer, drawReturned.Surface.Previous));
        Assert.Equal(0, h.Scene.CoverageSurfaceScansForTests);
    }

    [GpuFact]
    public void Near_plane_uncertainty_rejects_the_viewport_only_when_the_gpu_transition_changes()
    {
        using var h = new CoverageSceneFixture(executeNative: true);
        h.Scene.Post.Pixelated = false;
        h.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        h.Scene.CameraOverride = new HistoryCamera(.15f);
        Draw(h); h.Render(64, 48);
        Assert.All(h.Mask(), value => Assert.Equal(1f, value));
        Draw(h); h.Render(64, 48);
        Assert.All(h.Mask(), value => Assert.Equal(0f, value));
        Assert.True(h.Scene.TemporalHistory.IsValid);
    }

    [GpuFact]
    public void Registered_ordinary_replacement_uses_cold_palette_bounds_for_local_restoration()
    {
        using var h = new CoverageSceneFixture(executeNative: true);
        h.Scene.Post.Pixelated = false;
        h.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        h.Scene.CameraOverride = new HistoryCamera();
        var replacement = h.Scene.LoadSkinnedMesh(h.UncoveredMesh);
        Draw(h); h.Render(96, 72);
        h.Begin();
        h.Scene.DrawSkinned(new SkinnedInstanceDraw(replacement, Matrix4x4.Identity) { Motion = MotionKey.From(10) }, h.Palette);
        h.Render(96, 72);
        var mask = h.Mask();
        Assert.Contains(1f, mask); Assert.Equal(0f, mask[0]); Assert.Equal(0f, mask[^1]);
        Assert.Equal(0, h.Scene.CoverageSurfaceScansForTests);
        Assert.True(h.Scene.TemporalHistory.IsValid);
    }

    [Fact]
    public void Unrepresentable_registered_pose_uses_uncertainty_for_one_removal_transition()
    {
        using var h = new CoverageSceneFixture();
        h.Scene.Post.Pixelated = false;
        h.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        Draw(h); h.Render(96, 72);
        var palette = h.Palette.ToArray(); palette[0].M41 = float.NaN;
        void Ordinary()
        {
            h.Begin();
            h.Scene.DrawSkinned(new SkinnedInstanceDraw(h.Host[0], Matrix4x4.Identity) { Motion = MotionKey.From(10) }, palette);
            h.Render(96, 72);
        }
        Ordinary();
        var rectangle = Assert.Single(h.Scene.CoverageTransitionForTests.Rectangles);
        Assert.Equal((0, 0, 96, 72), (rectangle.X, rectangle.Y, rectangle.Right, rectangle.Bottom));
        Ordinary();
        Assert.Empty(h.Scene.CoverageTransitionForTests.Rectangles);
        Assert.True(h.Scene.TemporalHistory.IsValid);
    }
}
