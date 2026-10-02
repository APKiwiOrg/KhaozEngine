using System.Numerics;
using KhaozEngine.Automation;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests;

public sealed class AutomationPointerPointsTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(2, 3)]
    public void Injected_framebuffer_motion_has_the_same_tap_tolerance_and_replay(float x, float y)
    {
        var scale = new Vector2(x, y);
        var accumulator = new InputAccumulator();
        var real = accumulator.Snapshot(new Vector2(100, 150) * scale, true, 1920, 1080,
            framebufferScale: scale);
        var injector = new AutomationInputInjector();
        var gesture = new PointerGesture(MouseButton.Right);
        injector.SetPointer(new Vector2(100, 150) * scale);
        gesture.Advance(injector.Compose(real), false);
        injector.PressButton(MouseButton.Right, 0, 0);
        gesture.Advance(injector.Compose(real), false);
        injector.SetPointer(new Vector2(103, 151) * scale);
        gesture.Advance(injector.Compose(real), false);
        Assert.Equal(PointerGesturePhase.Pending, gesture.Phase);
        injector.SetPointer(new Vector2(105, 152) * scale);
        gesture.Advance(injector.Compose(real), false);
        Assert.Equal(new Vector2(5, 2), gesture.DragDelta);
    }

    [Fact]
    public void Compose_preserves_real_points_scale_and_other_fields_without_an_injected_pointer()
    {
        var a = new InputAccumulator();
        var scale = new Vector2(2, 3);
        a.Snapshot(new Vector2(200, 450), true, 1920, 1080, framebufferScale: scale);
        a.OnTextInput('a');
        a.OnScroll(2);
        var real = a.Snapshot(new Vector2(206, 456), true, 1920, 1080, framebufferScale: scale);
        var composed = new AutomationInputInjector().Compose(real);
        Assert.Equal(new Vector2(3, 2), composed.MouseDeltaPoints);
        Assert.Equal(real.MouseDelta, composed.MouseDelta);
        Assert.Equal(scale, composed.FramebufferScale);
        Assert.Equal(real.MousePosition, composed.MousePosition);
        Assert.Equal(real.TextInput, composed.TextInput);
        Assert.Equal(real.ScrollDelta, composed.ScrollDelta);
        Assert.Same(real.Gamepads, composed.Gamepads);
        Assert.Same(real.Touches, composed.Touches);
    }

    [Fact]
    public void Injected_points_rebase_across_scale_changes_beside_legacy_pixel_motion()
    {
        var a = new InputAccumulator();
        var injector = new AutomationInputInjector();
        injector.SetPointer(new Vector2(100, 150));
        injector.Compose(a.Snapshot(new Vector2(100, 150), true, 1920, 1080));
        var scale = new Vector2(2, 3);
        injector.SetPointer(new Vector2(200, 450));
        var rebased = injector.Compose(a.Snapshot(new Vector2(200, 450), true, 1920, 1080,
            framebufferScale: scale));
        Assert.Equal(Vector2.Zero, rebased.MouseDeltaPoints);
        Assert.Equal(new Vector2(100, 300), rebased.MouseDelta);
        injector.SetPointer(new Vector2(206, 456));
        var moved = injector.Compose(a.Snapshot(new Vector2(200, 450), true, 1920, 1080,
            framebufferScale: scale));
        Assert.Equal(new Vector2(3, 2), moved.MouseDeltaPoints);
        Assert.Equal(new Vector2(6, 6), moved.MouseDelta);
        injector.ReleasePointer();
        var real = a.Snapshot(new Vector2(202, 453), true, 1920, 1080, framebufferScale: scale);
        Assert.Equal(real.MouseDeltaPoints, injector.Compose(real).MouseDeltaPoints);
    }

    [Fact]
    public void A_captured_real_snapshot_keeps_points_motion_scale_and_framebuffer_anchor()
    {
        var a = new InputAccumulator();
        var scale = new Vector2(2, 3);
        var start = a.Snapshot(new Vector2(200, 450), true, 1920, 1080,
            pointerCaptured: true, framebufferScale: scale);
        var real = a.Snapshot(new Vector2(206, 456), true, 1920, 1080,
            pointerCaptured: true, framebufferScale: scale);
        var composed = new AutomationInputInjector().Compose(real);
        Assert.True(composed.PointerCaptured);
        Assert.Equal(new Vector2(3, 2), composed.MouseDeltaPoints);
        Assert.Equal(real.MouseDelta, composed.MouseDelta);
        Assert.Equal(scale, composed.FramebufferScale);
        Assert.Equal(start.MousePosition, composed.MousePosition);
    }
}
