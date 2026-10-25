using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui;

public sealed class SnapStripLifecycleTests
{
    static readonly Rect Band = new(100, 200, 200, 80);

    [Fact]
    public void No_input_still_advances_programmatic_animation()
    {
        var f = new Fixture();
        f.Strip.CenterOn(0, false);
        f.Strip.CenterOn(50);
        f.Frame(new(200, 240), false, dt: 0.09f, receivesInput: false);
        Assert.Equal(2117.5f, f.Strip.ScrollOffset, 3);
        Assert.True(f.Strip.IsAnimating);
        Assert.Equal(-1, f.Strip.HoveredItemIndex(f.Pointer));
        f.Frame(new(200, 240), false, dt: 0.09f, receivesInput: false);
        Assert.Equal(2420f, f.Strip.ScrollOffset);
        Assert.False(f.Strip.IsAnimating);
    }

    [Theory]
    [InlineData(0)] // Input disabled.
    [InlineData(1)] // Focus lost.
    [InlineData(2)] // Pointer captured.
    [InlineData(3)] // Origin occluded.
    [InlineData(4)] // Current point occluded.
    [InlineData(5)] // Nonfinite horizontal position.
    [InlineData(6)] // Nonfinite vertical position.
    public void Cancelled_drag_stays_consumed_until_release(int reason)
    {
        var f = Dragging();
        f.Focused = reason != 1;
        f.Captured = reason == 2;
        Vector2 at = reason switch
        {
            5 => new(float.NaN, 240),
            6 => new(170, float.PositiveInfinity),
            _ => new(170, 240)
        };
        f.Frame(at, true, dt: 0.18f, receivesInput: reason != 0, gate: p =>
        {
            if (reason == 3) p.Occlude(new(195, 235, 10, 10));
            if (reason == 4) p.Occlude(new(165, 235, 10, 10));
        });
        AssertSettled(f, 51, 2470f);
        Assert.True(f.Pointer.IsConsumed);
        f.Focused = true;
        f.Captured = false;
        AssertReleaseAndFreshAdmission(f, 2470f);
    }

    [Theory]
    [InlineData(0, 51, 2470f)] // Different viewport identity, identical transform.
    [InlineData(1, 51, 2470f)] // Same viewport, changed scale.
    [InlineData(2, 51, 2470f)] // Changed framebuffer scale.
    [InlineData(3, 51, 2470f)] // Translated viewport bounds.
    [InlineData(4, 50, 2370f)] // Changed viewport width.
    [InlineData(5, 9, 290f)] // Reduced item count.
    [InlineData(6, 50, 3430f)] // Changed item metrics.
    public void Mapping_and_layout_changes_cancel_stale_interaction(int change, int target, float offset)
    {
        var f = new Fixture { Viewport = new DesignViewport(1000, 1000) };
        f.Frame(new(200, 240), true);
        f.Frame(new(170, 240), true);
        Assert.True(f.Strip.IsDragging);
        if (change == 0) f.Viewport = new DesignViewport(1000, 1000);
        if (change == 1) f.Viewport.Update(2000, 2000);
        if (change == 2) f.FramebufferScale = new(2, 2);
        if (change == 3) f.Strip.Configure(new(110, 200, 200, 80), 100, new(40, 10));
        if (change == 4) f.Strip.Configure(new(100, 200, 300, 80), 100, new(40, 10));
        if (change == 5) f.Strip.Configure(Band, 10, new(40, 10));
        if (change == 6) f.Strip.Configure(Band, 100, new(60, 10));
        f.Frame(new(170, 240), true, dt: 0.18f);
        AssertSettled(f, target, offset);
        Assert.True(f.Pointer.IsConsumed);
        AssertReleaseAndFreshAdmission(f, offset);
    }

    [Fact]
    public void Close_reopen_requires_a_fresh_press()
    {
        var f = Dragging();
        f.Strip.CancelInteraction();
        f.Strip.Configure(Band, 100, new(40, 10));
        f.Strip.CenterOn(50, false);
        AssertSettled(f, 50, 2420f);
        AssertReleaseAndFreshAdmission(f, 2420f);
    }

    [Fact]
    public void Wrapper_geometry_matches_statics()
    {
        var f = new Fixture();
        f.Strip.CenterOn(51);
        f.Frame(new(200, 240), false, dt: 0.09f);
        var strip = f.Strip;
        Assert.Equal(2463.75f, strip.ScrollOffset);
        Assert.Equal(SnapStripGeometry.ItemBounds(strip.Bounds, 50, strip.ScrollOffset, 100, strip.Metrics), strip.ItemBounds(50));
        Assert.Equal(SnapStripGeometry.VisibleRange(strip.Bounds, strip.ScrollOffset, 100, strip.Metrics), strip.VisibleRange);
        Assert.Equal(SnapStripGeometry.ContentWidth(100, strip.Metrics), strip.ContentWidth);
        Assert.Equal(SnapStripGeometry.MaxScroll(strip.Bounds.Width, 100, strip.Metrics), strip.MaxScroll);
        Assert.Equal(new Rect(136.25f, 200, 40, 80), strip.ItemBounds(50));
        Assert.Equal((49, 5), strip.VisibleRange);
        Assert.Equal(4990f, strip.ContentWidth);
        Assert.Equal(4790f, strip.MaxScroll);
        foreach (Vector2 point in new Vector2[] { new(150, 240), new(200, 240), new(180, 240), new(300, 240) })
            Assert.Equal(SnapStripGeometry.IndexAt(strip.Bounds, point, strip.ScrollOffset, 100, strip.Metrics), strip.ItemAt(point));
        Assert.Equal(50, strip.ItemAt(new(150, 240)));
        Assert.Equal(51, strip.ItemAt(new(200, 240)));
        Assert.Equal(-1, strip.ItemAt(new(180, 240)));
        Assert.Equal(-1, strip.ItemAt(new(300, 240)));

        strip.CenterOn(0);
        Assert.False(strip.CanStepPrevious);
        Assert.True(strip.CanStepNext);
        strip.CenterOn(99);
        Assert.True(strip.CanStepPrevious);
        Assert.False(strip.CanStepNext);
        strip.Configure(Band, 3, new(40, 10));
        Assert.False(strip.CanStepPrevious);
        Assert.False(strip.CanStepNext);
    }

    [Fact]
    public void Idle_translation_and_identical_layout_preserve_ease_progress()
    {
        var f = new Fixture();
        f.Strip.CenterOn(0, false);
        f.Strip.CenterOn(50);
        f.Frame(new(200, 240), false, dt: 0.09f);
        f.Strip.Configure(Band, 100, new(40, 10));
        Assert.Equal(2117.5f, f.Strip.ScrollOffset);
        Assert.True(f.Strip.IsAnimating);
        f.Strip.Configure(new(110, 210, 200, 80), 100, new(40, 10));
        Assert.Equal(2117.5f, f.Strip.ScrollOffset);
        Assert.True(f.Strip.IsAnimating);
        f.Frame(new(200, 240), false, dt: 0.09f);
        AssertSettled(f, 50, 2420f);
        f.Strip.Configure(Band, 100, new(40, 10));
        AssertSettled(f, 50, 2420f);
    }

    [Fact]
    public void Identical_layout_preserves_active_drag()
    {
        var f = Dragging();
        f.Strip.Configure(Band, 100, new(40, 10));
        Assert.True(f.Strip.IsDragging);
        Assert.Equal(2450f, f.Strip.ScrollOffset);
        f.Frame(new(160, 240), true, dt: 0.18f);
        Assert.True(f.Strip.IsDragging);
        Assert.Equal(2460f, f.Strip.ScrollOffset);
    }

    [Theory]
    [InlineData(0, 54, 2620f)]
    [InlineData(1, 49, 2370f)]
    [InlineData(2, 51, 2470f)]
    public void Public_commands_end_ownership_and_preserve_consumed_release(int command, int target, float offset)
    {
        var f = Dragging();
        if (command == 0) f.Strip.CenterOn(54);
        if (command == 1) Assert.True(f.Strip.StepPrevious());
        if (command == 2) Assert.True(f.Strip.StepNext());
        Assert.False(f.Strip.IsDragging);
        Assert.Equal(2450f, f.Strip.ScrollOffset);
        Assert.Equal(target, f.Strip.TargetIndex);
        f.Frame(new(150, 240), true, dt: 0.18f);
        AssertSettled(f, target, offset);
        f.Frame(new(140, 240), false, dt: 0.18f);
        Assert.True(f.Pointer.IsConsumed);
        Assert.False(f.Pointer.IsTapIn(Band));
    }

    [Fact]
    public void Synthetic_unfocused_release_does_not_clear_cancelled_contact()
    {
        var f = Dragging();
        f.Focused = false;
        f.Frame(new(170, 240), false);
        AssertSettled(f, 51, 2470f);
        f.Focused = true;
        AssertReleaseAndFreshAdmission(f, 2470f);
    }

    static Fixture Dragging()
    {
        var f = new Fixture();
        f.Frame(new(200, 240), true);
        f.Frame(new(170, 240), true);
        Assert.True(f.Strip.IsDragging);
        Assert.Equal(2450f, f.Strip.ScrollOffset);
        return f;
    }

    static void AssertSettled(Fixture f, int target, float offset)
    {
        Assert.False(f.Strip.IsDragging);
        Assert.False(f.Strip.IsAnimating);
        Assert.Equal(target, f.Strip.TargetIndex);
        Assert.Equal(offset, f.Strip.ScrollOffset);
        Assert.InRange(f.Strip.ScrollOffset, 0f, f.Strip.MaxScroll);
    }

    static void AssertReleaseAndFreshAdmission(Fixture f, float offset)
    {
        f.Frame(new(150, 240), true, dt: 0.18f);
        Assert.False(f.Strip.IsDragging);
        Assert.Equal(offset, f.Strip.ScrollOffset);
        f.Frame(new(140, 240), false, dt: 0.18f);
        Assert.True(f.Pointer.IsConsumed);
        Assert.False(f.Pointer.IsTapIn(Band));
        Assert.Equal(offset, f.Strip.ScrollOffset);
        f.Frame(new(200, 240), true);
        f.Frame(new(210, 240), true, dt: 0.18f);
        Assert.True(f.Strip.IsDragging);
        Assert.Equal(offset - 10f, f.Strip.ScrollOffset);
    }

    sealed class Fixture
    {
        readonly MouseFrames _mouse = new();
        public Pointer Pointer { get; } = new();
        public SnapStrip Strip { get; } = new();
        public bool Focused { get; set; } = true;
        public bool Captured { get; set; }
        public Vector2 FramebufferScale { get; set; } = Vector2.One;
        public DesignViewport? Viewport { get; set; }

        public Fixture()
        {
            Strip.Configure(Band, 100, new(40, 10));
            Strip.CenterOn(50, false);
        }

        public void Frame(Vector2 at, bool down, float dt = 0f, bool receivesInput = true, Action<Pointer>? gate = null)
        {
            var (pressed, released) = _mouse.Advance(MouseButton.Left, down);
            var input = new InputState(Vector2.Zero, FramebufferScale,
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                down ? new HashSet<MouseButton> { MouseButton.Left } : new HashSet<MouseButton>(), pressed,
                Viewport?.DesignToScreen(at) ?? at, Vector2.Zero, 0, 2000, 2000, windowFocused: Focused,
                mouseReleased: released, pointerCaptured: Captured);
            Pointer.Update(input, Viewport);
            gate?.Invoke(Pointer);
            Strip.Update(Pointer, input, dt, receivesInput);
        }
    }
}
