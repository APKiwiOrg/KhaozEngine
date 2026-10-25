using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class PreparePhaseSplitGpuTests(PreparePhaseSplitFixture fixture, ITestOutputHelper log)
    : IClassFixture<PreparePhaseSplitFixture>
{
    const int Width = 64, Height = 40, Frames = 8;

    [GpuTheory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, true)]
    public void Pipelined_frames_and_same_frame_captures_are_byte_identical(
        bool transparent, bool temporal, bool geometry, bool hdr)
    {
        byte[][] legacy = fixture.Render(false, transparent, temporal, geometry, hdr);
        byte[][] split = fixture.Render(true, transparent, temporal, geometry, hdr);
        for (int i = 0; i < Frames; i++) Assert.Equal(legacy[i], split[i]);
        log.WriteLine($"{fixture.Backend}: {Frames} pipelined frames, transparent={transparent}, "
            + $"temporal={temporal}, geometry={geometry}, hdr={hdr}, {Frames * Width * Height * 4} identical RGBA bytes.");
    }

    [GpuTheory]
    [InlineData(SceneDebugView.MotionVectors)]
    [InlineData(SceneDebugView.History)]
    [InlineData(SceneDebugView.Disocclusion)]
    [InlineData(SceneDebugView.Reactive)]
    public void Pipelined_debug_views_preserve_their_resolve_inputs(SceneDebugView debugView)
    {
        byte[][] legacy = fixture.Render(false, false, true, true, true, debugView);
        byte[][] split = fixture.Render(true, false, true, true, true, debugView);
        for (int i = 0; i < Frames; i++) Assert.Equal(legacy[i], split[i]);
    }

    [GpuFact]
    public void Resizing_a_temporal_scene_preserves_the_output_and_capture()
    {
        byte[][] legacy = fixture.Render(false, false, true, true, true, resize: true);
        byte[][] split = fixture.Render(true, false, true, true, true, resize: true);
        for (int i = 0; i < Frames; i++) Assert.Equal(legacy[i], split[i]);
        Assert.Equal(96 * 64 * 4, split[^1].Length);
    }

    [GpuTheory]
    [InlineData(ScreenTransitionStyle.Solid)]
    [InlineData(ScreenTransitionStyle.FrozenCrossfade)]
    public void Active_screen_transitions_preserve_every_frame(ScreenTransitionStyle style)
    {
        byte[][] legacy = fixture.Render(false, false, true, true, true, transitionStyle: style);
        byte[][] split = fixture.Render(true, false, true, true, true, transitionStyle: style);
        for (int i = 0; i < Frames; i++) Assert.Equal(legacy[i], split[i]);
    }

    [GpuFact]
    public void The_aged_shared_scene_matches_a_fresh_scene_after_configuration_changes()
    {
        fixture.Render(false, false, false, true, false);
        fixture.Render(true, true, true, false, true);
        byte[][] aged = fixture.Render(true, false, true, true, true);
        using var fresh = new PreparePhaseSplitFixture();
        byte[][] isolated = fresh.Render(true, false, true, true, true);
        for (int i = 0; i < Frames; i++) Assert.Equal(isolated[i], aged[i]);
    }
}
