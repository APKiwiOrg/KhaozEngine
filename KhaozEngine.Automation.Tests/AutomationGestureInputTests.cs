using System.Numerics;
using KhaozEngine.Automation;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests;

public sealed class AutomationGestureInputTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Composition_keeps_real_gestures_and_event_anchors_with_an_injected_pointer(bool injected)
    {
        var a = new InputAccumulator();
        a.OnPointerGesture(new PointerGestureSample(PointerGestureKind.PreciseScroll,
            PointerGestureSource.MacOSNative, PointerGestureInputPhase.Changed,
            new Vector2(240, 360), new Vector2(3, 4), commandDown: true, isMomentum: true));
        a.OnPointerGesture(new PointerGestureSample(PointerGestureKind.Magnify,
            PointerGestureSource.MacOSNative, PointerGestureInputPhase.Changed,
            new Vector2(300, 450), scaleFactor: 1.2f));
        a.OnScroll(new Vector2(2, 3));
        a.OnTextInput('x');
        var real = a.Snapshot(new Vector2(240, 360), true, 1200, 900, framebufferScale: new Vector2(2, 3));
        var injector = new AutomationInputInjector();
        if (injected) injector.SetPointer(new Vector2(100, 150));
        var composed = injector.Compose(real);
        Assert.Same(real.Gestures, composed.Gestures);
        Assert.Equal(2, composed.Gestures.Samples.Count);
        Assert.Equal(new Vector2(240, 360), composed.Gestures.Samples[0].PositionPixels);
        Assert.Equal(new Vector2(3, 4), composed.Gestures.Samples[0].ScrollDelta);
        Assert.Equal(2f, composed.HorizontalScrollDelta);
        Assert.Equal(3f, composed.ScrollDelta);
        Assert.Equal(1.2f, composed.PinchScaleFactor);
        Assert.True(composed.HasPreciseScroll);
        Assert.Equal(real.FramebufferScale, composed.FramebufferScale);
        Assert.Equal(real.TextInput, composed.TextInput);
        Assert.Same(real.Touches, composed.Touches);
        a.OnPointerGesture(new PointerGestureSample(PointerGestureKind.Wheel,
            PointerGestureSource.Synthetic, PointerGestureInputPhase.None, Vector2.Zero));
        injector.Compose(a.Snapshot(Vector2.Zero, true, 1200, 900));
        Assert.Equal(2, composed.Gestures.Samples.Count);
        Assert.Equal(1.2f, composed.PinchScaleFactor);
    }

    [Fact]
    public void Composition_reuses_shared_empty_gesture_frame()
    {
        var composed = new AutomationInputInjector().Compose(InputState.Empty);
        Assert.Same(PointerGestureFrame.Empty, composed.Gestures);
        Assert.Equal(1f, composed.PinchScaleFactor);
    }
}
