using System.Numerics;
using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageTransitionTests
{
    static SkinnedCoverageFrame Draw(CoverageSceneFixture h, ulong instance = 1, float weight = 1)
    {
        h.Begin();
        var frame = h.Stage(instance: instance, weight: weight);
        h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette);
        h.SubmitEquipment();
        return frame;
    }

    static void Temporal(CoverageSceneFixture h)
    {
        h.Scene.Post.Pixelated = false;
        h.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
    }

    [Fact]
    public void Minimized_size_allocates_no_coverage_texture_and_motion_debug_needs_no_mask()
    {
        using var h = new CoverageSceneFixture();
        h.Scene.DebugView = SceneDebugView.MotionVectors;
        Draw(h);
        h.Scene.PrepareFrame(0, 0);
        Assert.Null(h.Scene.CoverageTransitionForTests.Texture);
        h.Render(prepare: false);
        Assert.NotNull(h.Scene.MotionResourcesForTests);
        Assert.Null(h.Scene.CoverageTransitionForTests.Texture);
    }

    [Fact]
    public void Zero_display_area_does_not_allocate_a_drawable_transition_mask()
    {
        using var h = new CoverageSceneFixture();
        Temporal(h);
        Draw(h);
        h.Scene.PrepareFrame(0, 0);
        h.Render(0, 0, prepare: false);
        Assert.Null(h.Scene.CoverageTransitionForTests.Texture);
        Draw(h); h.Render();
        Assert.NotNull(h.Scene.CoverageTransitionForTests.Texture);
        Assert.NotEmpty(h.Scene.CoverageTransitionForTests.Rectangles);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void All_resolve_entries_bind_the_active_mask(bool metal, bool split, bool upscale)
    {
        using var h = new CoverageSceneFixture(backend: metal ? GpuBackendKind.MetalNative : GpuBackendKind.VulkanNative);
        Temporal(h);
        h.Scene.Post.Temporal.Upscale = upscale ? TemporalUpscale.Performance : TemporalUpscale.Native;
        h.Scene.TemporalResolveEntryForTests = split ? TemporalResolveEntry.Split : TemporalResolveEntry.Fused;
        Draw(h); h.Render();
        var resolve = h.Scene.TemporalResolveRendererForTests!;
        var mask = h.Scene.CoverageTransitionForTests.Texture;
        Assert.Same(mask, Assert.IsType<FakeResourceSet>(resolve.CurrentSet).Resources[^2]);
        if (split)
        {
            Assert.Same(mask, Assert.IsType<FakeResourceSet>(resolve.SplitResolveForTests!
                .CoverageAccumulateSetForTests(h.Scene.TemporalHistory.ReadIndex)).Resources[^2]);
            Assert.Equal(!upscale, resolve.SplitResolveForTests!.AtDisplaySizeBuiltForTests);
        }
        Assert.NotEmpty(h.Scene.CoverageTransitionForTests.Rectangles);
    }

    [Fact]
    public void Mask_is_reserved_before_recording_reused_and_retired_after_resize()
    {
        using var h = new CoverageSceneFixture();
        Draw(h);
        h.Scene.PrepareFrame(32, 24);
        var initial = Assert.IsType<FakeTexture>(h.Scene.CoverageTransitionForTests.Texture);
        Assert.Equal(GpuPixelFormat.R8UNorm, initial.Format);
        Temporal(h);
        int textures = h.Factory.Textures.Count;
        h.Scene.PrepareFrame(32, 24);
        Assert.Equal(textures, h.Factory.Textures.Count);
        h.Render();
        Assert.Same(initial, h.Scene.CoverageTransitionForTests.Texture);
        Assert.NotEmpty(h.Scene.CoverageTransitionForTests.Rectangles);
        Assert.Contains(h.Record.Scissors, r => r.Width > 0 && r.Height > 0);
        var set = Assert.IsType<FakeResourceSet>(h.Scene.TemporalResolveRendererForTests!.CurrentSet);
        Assert.Same(initial, set.Resources[^2]);
        Draw(h, weight: .5f);
        h.Render();
        // Recording proves a conditional rectangle was encoded. Native tests prove its zero mask on steady frames.
        Assert.NotEmpty(h.Scene.CoverageTransitionForTests.Rectangles);
        Assert.Same(initial, h.Scene.CoverageTransitionForTests.Texture);
        Draw(h, instance: 2);
        h.Scene.PrepareFrame(48, 36);
        var replacement = Assert.IsType<FakeTexture>(h.Scene.CoverageTransitionForTests.Texture);
        Assert.NotSame(initial, replacement);
        Assert.False(initial.Disposed);
        Assert.Equal((48u, 36u), (replacement.Width, replacement.Height));
        h.Render(48, 36);
        Assert.NotEmpty(h.Scene.CoverageTransitionForTests.Rectangles);
        Assert.Same(replacement, Assert.IsType<FakeResourceSet>(h.Scene.TemporalResolveRendererForTests!.CurrentSet).Resources[^2]);
    }

    [Fact]
    public void Missing_or_wrong_mask_reservation_refuses_before_scene_commands_and_history_latch()
    {
        using var h = new CoverageSceneFixture();
        Temporal(h);
        Draw(h);
        h.Scene.PrepareFrame();
        var error = Assert.Throws<InvalidOperationException>(() => h.Render(prepare: false));
        Assert.Contains("PrepareFrame(viewportWidth, viewportHeight)", error.Message);
        Assert.Empty(h.Record.Uploads);
        Assert.Equal(0, h.Record.FramebufferBinds);
        Assert.Equal(0, h.Scene.CurrentFrameView.FrameIndex);
        h.Scene.PrepareFrame(32, 24);
        Assert.Throws<InvalidOperationException>(() => h.Render(48, 36, prepare: false));
        Assert.Empty(h.Record.Uploads);
        Assert.Equal(0, h.Record.FramebufferBinds);
        h.Scene.PrepareFrame(48, 36);
        h.Render(48, 36, prepare: false);
        Assert.NotEmpty(h.Record.Uploads);
    }

    [Fact]
    public void Sized_preparation_refreshes_after_settings_change_without_repeating_producers()
    {
        using var h = new CoverageSceneFixture();
        Draw(h);
        h.Scene.PrepareFrame(60, 40);
        var initial = h.Scene.CoverageTransitionForTests.Texture;
        h.Scene.Post.Temporal.Upscale = TemporalUpscale.Performance;
        h.Scene.PrepareFrame(60, 40);
        var next = h.Scene.CoverageTransitionForTests.Texture!;
        Assert.NotSame(initial, next);
        Assert.Equal((30u, 20u), (next.Width, next.Height));
        Temporal(h);
        h.Render(60, 40, prepare: false);
        Assert.Same(next, h.Scene.CoverageTransitionForTests.Texture);
        Assert.Equal((30, 20), (h.Scene.CurrentFrameView.Width, h.Scene.CurrentFrameView.Height));
    }

    [Fact]
    public void Unequip_and_disposal_reject_prior_surface_with_empty_current_preparation()
    {
        using var h = new CoverageSceneFixture();
        Temporal(h);
        Draw(h); h.Render();
        h.Begin();
        h.Scene.DrawSkinned(new SkinnedInstanceDraw(h.Host[0], Matrix4x4.Identity) { Motion = MotionKey.From(10) }, h.Palette);
        h.Render();
        Assert.NotEmpty(h.Scene.CoverageTransitionForTests.Rectangles);
        Draw(h); h.Render();
        h.Conformer.Dispose();
        h.Begin(); h.Render();
        Assert.NotEmpty(h.Scene.CoverageTransitionForTests.Rectangles);
    }

    [Fact]
    public void Render_context_refusal_rejects_previous_and_current_pose_locally()
    {
        using var h = new CoverageSceneFixture();
        Temporal(h);
        Draw(h); h.Render();
        h.Begin();
        var refused = h.Stage();
        h.Scene.DrawSkinned(h.HostDraw(refused), h.Palette);
        // Missing actual equipment presentation refuses the staged candidate.
        h.Render();
        Assert.Equal(CoveragePresentation.UncoveredFallback, h.Scene.GetCoverageOutcome(refused).Presentation);
        Assert.NotEmpty(h.Scene.CoverageTransitionForTests.Rectangles);
        Assert.True(h.Scene.TemporalHistory.IsValid);
    }

    [Fact]
    public void Mask_growth_refuses_inside_a_recording_and_keeps_the_old_resource()
    {
        using var device = new FakeGpuDevice();
        using var mask = new CoverageTransitionMask(device);
        mask.EnsureSize(20, 10, null);
        var initial = mask.Texture;
        using var commands = new NullGpuCommandList();
        using (GpuRecording.Open(device, commands, "coverage test"))
            Assert.Throws<InvalidOperationException>(() => mask.EnsureSize(40, 20, null));
        Assert.Same(initial, mask.Texture);
    }

    [Fact]
    public void Failed_mask_resize_keeps_the_live_previous_resource()
    {
        using var device = new FakeGpuDevice();
        using var mask = new CoverageTransitionMask(device);
        using var retired = GpuRetireQueue.CreateFrameCounted(device, 1);
        mask.EnsureSize(20, 10, retired);
        var initial = Assert.IsType<FakeTexture>(mask.Texture);
        var factory = Assert.IsType<FakeGpuResourceFactory>(device.Factory);
        factory.FailTextureCreationAfter(0);
        Assert.Throws<InvalidOperationException>(() => mask.EnsureSize(40, 20, retired));
        Assert.Same(initial, mask.Texture);
        Assert.False(initial.Disposed);
        Assert.Equal(0, retired.PendingCount);
        Assert.True(mask.Matches(20, 10));
        factory.ThrowOnTextureCreate = 0;
        mask.EnsureSize(40, 20, retired);
        Assert.Equal(2, retired.PendingCount);
        Assert.False(initial.Disposed);
        retired.Dispose();
        Assert.True(initial.Disposed);
    }

    static FrameView View => new(Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity,
        Matrix4x4.Identity, Vector3.Zero, 100, 100, 1, Vector2.Zero);

    [Fact]
    public void Transition_rejects_both_bounds_only()
    {
        using var device = new FakeGpuDevice();
        using var mask = new CoverageTransitionMask(device);
        mask.Reserve(2);
        mask.EnsureSize(100, 100, null);
        mask.AddChangedBounds(new(new(new(-.8f, -.2f, .4f), new(-.6f, .2f, .6f)),
            new(new(.4f, -.2f, .4f), new(.6f, .2f, .6f)), false));
        using var commands = new NullGpuCommandList();
        mask.Prepare(commands, View);
        Assert.True(mask.Rejects(15, 50));
        Assert.True(mask.Rejects(75, 50));
        Assert.False(mask.Rejects(95, 90));
    }

    [Fact]
    public void Near_plane_transition_rejects_the_viewport()
    {
        using var device = new FakeGpuDevice();
        using var mask = new CoverageTransitionMask(device);
        mask.Reserve(1);
        mask.EnsureSize(100, 100, null);
        mask.AddChangedBounds(new(new(new(-.1f, -.1f, -.1f), new(.1f, .1f, .1f)), null, false));
        using var commands = new NullGpuCommandList();
        mask.Prepare(commands, View);
        Assert.True(mask.Rejects(0, 0));
        Assert.True(mask.Rejects(99, 99));
    }

    [Fact]
    public void Wholly_behind_camera_transition_does_not_reject_visible_pixels()
    {
        using var device = new FakeGpuDevice();
        using var mask = new CoverageTransitionMask(device);
        mask.Reserve(1); mask.EnsureSize(100, 100, null);
        mask.AddChangedBounds(new(new(new(-.1f, -.1f, -2), new(.1f, .1f, -1)), null, false));
        using var commands = new NullGpuCommandList();
        mask.Prepare(commands, View);
        Assert.Empty(mask.Rectangles.ToArray());
    }
}
