using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui;

public sealed class SnapStripInputTests
{
    static readonly Rect Band = new(100, 200, 200, 80);

    [Fact]
    public void Horizontal_slop_applies_full_displacement()
    {
        var f = new Fixture();
        f.Frame(new(200, 240), false);
        f.Frame(new(200, 240), true);
        f.Frame(new(195, 240), true);
        Assert.Equal(2420f, f.Strip.ScrollOffset);
        f.Frame(new(190, 240), true);
        Assert.Equal(2430f, f.Strip.ScrollOffset);
        Assert.True(f.Strip.IsDragging);
        f.Frame(new(180, 240), true);
        Assert.Equal(2440f, f.Strip.ScrollOffset);
        f.Frame(new(170, 240), false, dt: 0.18f);
        Assert.Equal(2450f, f.Strip.ScrollOffset);
        Assert.Equal(51, f.Strip.TargetIndex);
        Assert.True(f.Pointer.IsConsumed);
        f.Frame(new(170, 240), false, dt: 0.18f);
        Assert.Equal(2470f, f.Strip.ScrollOffset);
    }

    [Fact]
    public void Final_release_movement_precedes_snap()
    {
        var f = new Fixture();
        f.Frame(new(200, 240), true);
        f.Frame(new(170, 240), false, dt: 0.18f);
        Assert.Equal(2450f, f.Strip.ScrollOffset);
        Assert.Equal(51, f.Strip.TargetIndex);
        Assert.True(f.Strip.IsAnimating);
        Assert.False(f.Strip.IsDragging);
        Assert.True(f.Pointer.IsConsumed);
        Assert.False(f.Pointer.IsTapIn(Band));
        f.Frame(new(170, 240), false, dt: 0.18f);
        Assert.Equal(2470f, f.Strip.ScrollOffset);
    }

    [Theory]
    [InlineData(0)] // Outside origin.
    [InlineData(1)] // Held before widget creation.
    [InlineData(2)] // Reserved origin.
    [InlineData(3)] // Occluded origin.
    [InlineData(4)] // Consumed origin.
    [InlineData(5)] // Unfocused press.
    [InlineData(6)] // Captured pointer.
    [InlineData(7)] // Input disabled.
    public void Rejected_origin_stays_rejected(int reason)
    {
        var f = new Fixture(heldBeforeCreation: reason == 1);
        f.Focused = reason != 5;
        f.Captured = reason == 6;
        f.Frame(new(reason == 0 ? 90 : 200, 240), true, receivesInput: reason != 7, gate: p =>
        {
            if (reason == 2) p.BlockRegion(Band);
            if (reason == 3) p.Occlude(Band);
            if (reason == 4) p.ConsumeGesture();
        });
        f.Focused = true;
        f.Captured = false;
        f.Frame(new(190, 240), true);
        f.Frame(new(150, 240), true);
        Assert.Equal(2420f, f.Strip.ScrollOffset);
        Assert.False(f.Strip.IsDragging);
        f.Frame(new(150, 240), false);
        f.Frame(new(200, 240), true);
        f.Frame(new(190, 240), true);
        Assert.Equal(2430f, f.Strip.ScrollOffset);
        Assert.True(f.Strip.IsDragging);
    }

    [Fact]
    public void Owned_drag_consumes_outside_release()
    {
        var f = new Fixture();
        f.Frame(new(200, 240), true);
        f.Frame(new(90, 240), true);
        Assert.Equal(2530f, f.Strip.ScrollOffset);
        Assert.True(f.Strip.IsDragging);
        Assert.True(f.Pointer.IsConsumed);
        f.Frame(new(80, 240), false);
        Assert.Equal(2540f, f.Strip.ScrollOffset);
        Assert.True(f.Pointer.IsConsumed);
        Assert.False(f.Pointer.IsTapIn(Band));
        Assert.False(f.Pointer.IsTapIn(new(0, 0, 1000, 1000)));
        Assert.False(f.Strip.IsDragging);
    }

    [Fact]
    public void Fast_tap_leaves_no_ownership()
    {
        var f = new Fixture();
        f.Frame(new(200, 240), false);
        f.FastTap(new(200, 240), 0.18f);
        Assert.False(f.Strip.IsDragging);
        Assert.False(f.Pointer.IsConsumed);
        Assert.True(f.Pointer.IsTapIn(Band));
        f.Frame(new(150, 240), false);
        Assert.Equal(2420f, f.Strip.ScrollOffset);
        f.Frame(new(200, 240), true);
        f.Frame(new(190, 240), true);
        Assert.Equal(2430f, f.Strip.ScrollOffset);
    }

    [Fact]
    public void Hover_uses_current_geometry()
    {
        var f = new Fixture();
        f.Frame(new(200, 240), false);
        Assert.Equal(50, f.Strip.HoveredItemIndex(f.Pointer));
        f.Frame(new(225, 240), false);
        Assert.Equal(-1, f.Strip.HoveredItemIndex(f.Pointer));
        f.Strip.CenterOn(51);
        f.Frame(new(200, 240), false, dt: 0.09f);
        Assert.Equal(2463.75f, f.Strip.ScrollOffset);
        Assert.Equal(51, f.Strip.HoveredItemIndex(f.Pointer));
        Assert.Equal(f.Strip.ItemAt(f.Pointer.Position), f.Strip.HoveredItemIndex(f.Pointer));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Hover_honours_input_gates(int reason)
    {
        var f = new Fixture();
        f.Focused = reason != 1;
        f.Captured = reason == 2;
        f.Frame(new(200, 240), reason == 4, receivesInput: reason != 3,
            gate: p => { if (reason == 0) p.Occlude(Band); });
        if (reason == 4) f.Frame(new(190, 240), true);
        Assert.Equal(-1, f.Strip.HoveredItemIndex(f.Pointer));
    }

    [Fact]
    public void Vertical_travel_does_not_cross_horizontal_slop()
    {
        var f = new Fixture();
        f.Frame(new(200, 240), true);
        f.Frame(new(200, 300), true);
        Assert.False(f.Strip.IsDragging);
        Assert.False(f.Pointer.IsConsumed);
        Assert.Equal(2420f, f.Strip.ScrollOffset);
        f.Frame(new(200, 300), false);
        Assert.False(f.Pointer.IsConsumed);
    }

    [Fact]
    public void Zero_slop_applies_one_unit_horizontal_move()
    {
        var f = new Fixture(dragSlop: 0f);
        f.Frame(new(200, 240), true);
        Assert.False(f.Strip.IsDragging);
        f.Frame(new(199, 240), true);
        Assert.True(f.Strip.IsDragging);
        Assert.Equal(2421f, f.Strip.ScrollOffset);
        Assert.True(f.Pointer.IsConsumed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reservation_policy_does_not_change_geometry_or_motion(bool blocks)
    {
        var f = new Fixture();
        Assert.True(f.Strip.BlocksPointer);
        f.Strip.BlocksPointer = blocks;
        f.Frame(new(200, 240), false);
        Assert.Equal(blocks, f.Pointer.IsBlocked(new(200, 240)));
        Assert.Equal(Band, f.Strip.Bounds);
        Assert.Equal(new Rect(180, 200, 40, 80), f.Strip.ItemBounds(50));
        Assert.Equal((48, 5), f.Strip.VisibleRange);
        Assert.Equal(4990f, f.Strip.ContentWidth);
        Assert.Equal(4790f, f.Strip.MaxScroll);
        f.Frame(new(200, 240), true);
        f.Frame(new(190, 240), true);
        Assert.Equal(2430f, f.Strip.ScrollOffset);
        Assert.True(f.Pointer.IsConsumed);
    }

    [Fact]
    public void Tap_interrupts_ease_and_resumes_pending_target_without_release_frame_advance()
    {
        var f = new Fixture();
        f.Strip.CenterOn(52);
        f.Frame(new(200, 240), false, dt: 0.09f);
        Assert.Equal(2507.5f, f.Strip.ScrollOffset);
        f.Frame(new(200, 240), true, dt: 0.18f);
        Assert.False(f.Strip.IsAnimating);
        Assert.Equal(2507.5f, f.Strip.ScrollOffset);
        f.Frame(new(200, 240), false, dt: 0.18f);
        Assert.Equal(2507.5f, f.Strip.ScrollOffset);
        Assert.Equal(52, f.Strip.TargetIndex);
        Assert.False(f.Pointer.IsConsumed);
        f.Frame(new(200, 240), false, dt: 0.18f, receivesInput: false);
        Assert.Equal(2520f, f.Strip.ScrollOffset);
        Assert.False(f.Strip.IsAnimating);
    }

    [Theory]
    [InlineData(0, 54)]
    [InlineData(1, 51)]
    [InlineData(2, 53)]
    public void Commands_cancel_drag_ownership_and_use_pending_target(int command, int target)
    {
        var f = new Fixture();
        f.Strip.CenterOn(52);
        f.Frame(new(200, 240), false, dt: 0.018f);
        f.Frame(new(200, 240), true);
        f.Frame(new(190, 240), true);
        float offset = f.Strip.ScrollOffset;
        Assert.True(f.Strip.CanStepPrevious);
        Assert.True(f.Strip.CanStepNext);
        if (command == 0) f.Strip.CenterOn(54);
        if (command == 1) Assert.True(f.Strip.StepPrevious());
        if (command == 2) Assert.True(f.Strip.StepNext());
        Assert.False(f.Strip.IsDragging);
        Assert.Equal(target, f.Strip.TargetIndex);
        Assert.Equal(offset, f.Strip.ScrollOffset);
        f.Frame(new(150, 240), true);
        Assert.Equal(offset, f.Strip.ScrollOffset);
        f.Frame(new(150, 240), false);
        Assert.True(f.Pointer.IsConsumed);
    }

    [Fact]
    public void Cancellation_settles_drag_and_requires_release_before_readmission()
    {
        var f = new Fixture();
        f.Frame(new(200, 240), true);
        f.Frame(new(170, 240), true);
        f.Strip.CancelInteraction();
        Assert.False(f.Strip.IsDragging);
        Assert.False(f.Strip.IsAnimating);
        Assert.Equal(51, f.Strip.TargetIndex);
        Assert.Equal(2470f, f.Strip.ScrollOffset);
        f.Frame(new(150, 240), true);
        Assert.Equal(2470f, f.Strip.ScrollOffset);
        f.Frame(new(150, 240), false);
        Assert.True(f.Pointer.IsConsumed);
        f.Frame(new(200, 240), true);
        f.Frame(new(190, 240), true);
        Assert.Equal(2480f, f.Strip.ScrollOffset);
    }

    [Fact]
    public void Mapping_change_cancels_drag_without_accepting_the_held_contact_again()
    {
        var f = new Fixture();
        f.Frame(new(200, 240), true);
        f.Frame(new(170, 240), true);
        f.FramebufferScale = new(2, 2);
        f.Frame(new(150, 240), true);
        Assert.False(f.Strip.IsDragging);
        Assert.Equal(2470f, f.Strip.ScrollOffset);
        f.Frame(new(120, 240), true);
        Assert.Equal(2470f, f.Strip.ScrollOffset);
        f.Frame(new(120, 240), false);
        Assert.True(f.Pointer.IsConsumed);
    }

    [Fact]
    public void Invalid_configuration_preserves_layout_and_active_drag()
    {
        var f = new Fixture();
        f.Frame(new(200, 240), true);
        f.Frame(new(190, 240), true);
        Assert.Throws<ArgumentOutOfRangeException>(() => f.Strip.Configure(new(0, float.NaN, 100, 80), 1, new(20)));
        Assert.Throws<ArgumentOutOfRangeException>(() => f.Strip.Configure(new(0, 0, 100, 80), -1, new(20)));
        Assert.Throws<ArgumentOutOfRangeException>(() => f.Strip.Configure(new(0, 0, 100, 80), 1, default));
        Assert.Equal(Band, f.Strip.Bounds);
        Assert.Equal(100, f.Strip.ItemCount);
        Assert.Equal(new SnapStripMetrics(40, 10), f.Strip.Metrics);
        Assert.Equal(2430f, f.Strip.ScrollOffset);
        Assert.True(f.Strip.IsDragging);
        f.Frame(new(180, 240), true);
        Assert.Equal(2440f, f.Strip.ScrollOffset);
    }

    sealed class Fixture
    {
        readonly MouseFrames _mouse = new();
        public Pointer Pointer { get; } = new();
        public SnapStrip Strip { get; }
        public bool Focused { get; set; } = true;
        public bool Captured { get; set; }
        public Vector2 FramebufferScale { get; set; } = Vector2.One;

        public Fixture(float dragSlop = 8f, bool heldBeforeCreation = false)
        {
            if (heldBeforeCreation) Pointer.Update(State(new(200, 240), true));
            Strip = new SnapStrip(dragSlop: dragSlop);
            Strip.Configure(Band, 100, new(40, 10));
            Strip.CenterOn(50, false);
        }

        public void Frame(Vector2 at, bool down, float dt = 0f, bool receivesInput = true, Action<Pointer>? gate = null)
        {
            var input = State(at, down);
            Pointer.Update(input);
            gate?.Invoke(Pointer);
            Strip.Update(Pointer, input, dt, receivesInput);
        }

        public void FastTap(Vector2 at, float dt)
        {
            var input = State(at, false, fastTap: true);
            Pointer.Update(input);
            Strip.Update(Pointer, input, dt);
        }

        InputState State(Vector2 at, bool down, bool fastTap = false)
        {
            var (pressed, released) = _mouse.Advance(MouseButton.Left, down);
            if (fastTap)
            {
                pressed.Add(MouseButton.Left);
                released.Add(MouseButton.Left);
            }
            return new InputState(Vector2.Zero, FramebufferScale,
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                down ? new HashSet<MouseButton> { MouseButton.Left } : new HashSet<MouseButton>(), pressed,
                at, Vector2.Zero, 0, 1000, 1000, windowFocused: Focused,
                mouseReleased: released, pointerCaptured: Captured);
        }
    }
}
