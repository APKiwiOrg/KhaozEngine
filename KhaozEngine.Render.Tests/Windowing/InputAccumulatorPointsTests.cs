using System.Numerics;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

public sealed class InputAccumulatorPointsTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(2, 3)]
    public void Uncaptured_points_are_normalized_beside_unchanged_framebuffer_motion(float x, float y)
    {
        var scale = new Vector2(x, y);
        var rig = new PointerPointsTestRig(scale);
        var input = rig.Sample(false, new Vector2(3, 2));
        Assert.Equal(new Vector2(3, 2), input.MouseDeltaPoints);
        Assert.Equal(new Vector2(3 * x, 2 * y), input.MouseDelta);
        Assert.Equal(new Vector2(103 * x, 152 * y), input.MousePosition);
        Assert.Equal(scale, input.FramebufferScale);
    }

    [Fact]
    public void New_scale_metadata_rejects_infinity_without_changing_legacy_capture_division()
    {
        var a = new InputAccumulator();
        var invalid = new Vector2(float.PositiveInfinity, float.NaN);
        a.Snapshot(new Vector2(10, 20), true, 1920, 1080, framebufferScale: invalid);
        var start = a.Snapshot(new Vector2(12, 22), true, 1920, 1080,
            pointerCaptured: true, framebufferScale: invalid);
        Assert.Equal(Vector2.Zero, start.MouseDeltaPoints);
        var held = a.Snapshot(new Vector2(18, 31), true, 1920, 1080,
            pointerCaptured: true, framebufferScale: invalid);
        Assert.Equal(Vector2.One, held.FramebufferScale);
        Assert.Equal(new Vector2(6, 9), held.MouseDeltaPoints);
        Assert.Equal(new Vector2(0, 9), held.MouseDelta);
        Assert.Equal(start.MousePosition, held.MousePosition);
    }

    [Fact]
    public void Logical_baseline_survives_missing_mouse_and_scale_changes()
    {
        var rig = new PointerPointsTestRig(Vector2.One);
        rig.Scale = new Vector2(2, 3);
        var missing = rig.Sample(false, hasMouse: false);
        Assert.Equal(Vector2.Zero, missing.MouseDeltaPoints);
        Assert.Equal(new Vector2(100, 150), missing.MousePosition);
        var returned = rig.Sample(false);
        Assert.Equal(Vector2.Zero, returned.MouseDeltaPoints);
        Assert.Equal(new Vector2(100, 300), returned.MouseDelta);
        var moved = rig.Sample(false, new Vector2(1, 2));
        Assert.Equal(new Vector2(1, 2), moved.MouseDeltaPoints);
        Assert.Equal(new Vector2(2, 6), moved.MouseDelta);
    }

    [Fact]
    public void First_real_sample_after_missing_mouse_and_both_capture_transitions_are_zero()
    {
        var a = new InputAccumulator();
        var scale = new Vector2(2, 3);
        Assert.Equal(Vector2.Zero, a.Snapshot(Vector2.Zero, false, 1920, 1080,
            framebufferScale: scale).MouseDeltaPoints);
        Assert.Equal(Vector2.Zero, a.Snapshot(new Vector2(200, 450), true, 1920, 1080,
            framebufferScale: scale).MouseDeltaPoints);
        var start = a.Snapshot(new Vector2(210, 480), true, 1920, 1080,
            pointerCaptured: true, framebufferScale: scale);
        Assert.Equal(Vector2.Zero, start.MouseDeltaPoints);
        var held = a.Snapshot(new Vector2(214, 477), true, 1920, 1080,
            pointerCaptured: true, framebufferScale: scale);
        Assert.Equal(new Vector2(2, -1), held.MouseDeltaPoints);
        Assert.Equal(held.MouseDelta, held.MouseDeltaPoints);
        Assert.Equal(start.MousePosition, held.MousePosition);
        var end = a.Snapshot(new Vector2(50, 60), true, 1920, 1080, framebufferScale: scale);
        Assert.Equal(Vector2.Zero, end.MouseDeltaPoints);
        Assert.Equal(Vector2.Zero, end.MouseDelta);
        Assert.Equal(new Vector2(50, 60), end.MousePosition);
    }
}
