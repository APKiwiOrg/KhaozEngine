using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageTransitionLargeOriginTests
{
    const float Origin = 16_777_216;
    static Matrix4x4 Model(float offset = 0) => Matrix4x4.CreateScale(.3f)
        * Matrix4x4.CreateTranslation(Origin + offset, 0, 0);

    static void Configure(CoverageSceneFixture h)
    {
        h.Scene.Post.Pixelated = false;
        h.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        h.Scene.RenderOrigin = new(Origin, 0, 0);
        h.Scene.CameraOverride = new RelativeCamera();
    }

    static SkinnedCoverageFrame Submit(CoverageSceneFixture h, float offset = 0, bool partial = false)
    {
        h.Begin();
        var frame = h.Stage(Model(offset));
        h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette);
        if (h.Host.Length > 1)
        {
            var second = h.HostDraw(frame, slice: 1);
            if (partial) h.Outline(second);
            else h.Scene.DrawSkinned(second, h.Palette);
        }
        h.SubmitEquipment();
        h.Render(100, 100);
        Assert.Equal(CoveragePresentation.Dispatched, h.Scene.GetCoverageOutcome(frame).Presentation);
        return frame;
    }

    static bool Rejects(CoverageSceneFixture h, int x, int y)
    {
        foreach (var r in h.Scene.CoverageTransitionForTests.Rectangles)
            if (x >= r.X && x < r.Right && y >= r.Y && y < r.Bottom) return true;
        return false;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ordinary_restoration_preserves_current_bounds_before_origin_subtraction(bool gpu)
    {
        using var h = new CoverageSceneFixture();
        Configure(h);
        h.Scene.UseGpuSkinning = gpu;
        Submit(h, offset: 32);
        Assert.Empty(h.Scene.CoverageTransitionForTests.Rectangles);
        h.Begin();
        h.Scene.DrawSkinned(new SkinnedInstanceDraw(h.Host[0], Model()) { Motion = MotionKey.From(10) }, h.Palette);
        h.Render(100, 100);
        Assert.True(h.Scene.TemporalHistory.IsValid);
        Assert.True(Rejects(h, 41, 50));
        Assert.True(Rejects(h, 57, 50));
        Assert.False(Rejects(h, 95, 50));
        Assert.Contains(h.Record.Scissors, r => r.X <= 41 && r.X + r.Width > 57 && r.Width < 90);
    }

    [Fact]
    public void Partial_main_surface_preserves_current_bounds_before_origin_subtraction()
    {
        using var h = new CoverageSceneFixture(seams: true);
        Configure(h);
        Submit(h, offset: 32);
        Assert.Empty(h.Scene.CoverageTransitionForTests.Rectangles);
        Submit(h, partial: true);
        Assert.True(Rejects(h, 41, 50));
        Assert.False(Rejects(h, 95, 50));
        Assert.Contains(h.Record.Scissors, r => r.X <= 41 && r.X + r.Width > 41 && r.Width < 90);
    }

    [Fact]
    public void Missing_surface_preserves_precise_committed_bounds_without_rejecting_unrelated_pixels()
    {
        using var h = new CoverageSceneFixture();
        Configure(h);
        Submit(h);
        h.Begin(); h.Render(100, 100);
        Assert.True(Rejects(h, 41, 50));
        Assert.True(Rejects(h, 57, 50));
        Assert.False(Rejects(h, 95, 50));
    }

    [Fact]
    public void Rebased_current_and_previous_anchors_keep_their_union_local()
    {
        using var device = new FakeGpuDevice();
        using var mask = new CoverageTransitionMask(device);
        mask.Reserve(1); mask.EnsureSize(100, 100, null);
        var oldOrigin = new Vector3(Origin, 0, 0);
        var origin = oldOrigin + new Vector3(128, 0, 0);
        var old = CoverageSurfaceBounds.FromRender(new MeshBounds(new(-.6f, -.1f, .4f), new(-.4f, .1f, .6f)), oldOrigin);
        var current = CoverageSurfaceBounds.FromRender(new MeshBounds(new(-127.6f, -.1f, .4f), new(-127.4f, .1f, .6f)), origin);
        mask.AddChangedBounds(new(current, old, false));
        Matrix4x4 vp = Matrix4x4.CreateTranslation(128, 0, 0);
        var view = new FrameView(vp, Matrix4x4.Identity, vp, default, origin, 100, 100, 2, Vector2.Zero);
        using var record = new RecordingGpuCommandList(new NullGpuCommandList());
        mask.Prepare(record, view);
        Assert.True(mask.Rejects(25, 50));
        Assert.True(mask.Rejects(75, 50));
        Assert.False(mask.Rejects(95, 50));
        Assert.Contains(record.Scissors, r => r.X <= 25 && r.X + r.Width > 75 && r.Width < 90);
    }

    [Fact]
    public void Absolute_float_expansion_negative_still_rejects_unrelated_pixels_in_the_wider_view()
    {
        using var h = new CoverageSceneFixture();
        Configure(h);
        var frame = Submit(h);
        Assert.False(Rejects(h, 95, 50));
        var bounds = h.Conformer.Gpu.Host.Bounds;
        var skin = bounds.BoundSkin(h.Conformer.Uniforms.ComposedPalette);
        var oldAbsolute = bounds.ExpandWorld(skin, frame.Preparation!.Model,
            frame.Preparation.Commands.AsSpan(0, frame.Preparation.CommandCount));
        using var device = new FakeGpuDevice();
        using var oldMask = new CoverageTransitionMask(device);
        oldMask.Reserve(1); oldMask.EnsureSize(100, 100, null);
        oldMask.AddChangedBounds(new(CoverageSurfaceBounds.FromWorld(oldAbsolute, h.Scene.CurrentFrameView.RenderOrigin), null, false));
        using var commands = new NullGpuCommandList();
        oldMask.Prepare(commands, h.Scene.CurrentFrameView);
        Assert.True(oldMask.Rejects(95, 50));
    }

    sealed class RelativeCamera : IIsoCamera3D, IRenderOriginAware
    {
        public Vector3 RenderOrigin { get; set; }
        public Matrix4x4 View => Matrix4x4.CreateTranslation(0, 0, -2);
        public Matrix4x4 Projection => Matrix4x4.CreateOrthographic(4, 4, .1f, 10);
        public Matrix4x4 ViewProjection => View * Projection;
        public Matrix4x4 AbsoluteViewProjection => Matrix4x4.CreateTranslation(-RenderOrigin) * ViewProjection;
        public Vector3 Eye => RenderOrigin + new Vector3(0, 0, 2);
        public Vector3 Forward => -Vector3.UnitZ;
        public bool WorldToScreen(Vector3 world, int width, int height, out Vector2 screenPixel)
        { screenPixel = default; return false; }
    }
}
