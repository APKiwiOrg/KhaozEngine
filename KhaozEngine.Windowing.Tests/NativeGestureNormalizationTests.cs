using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

public sealed class NativeGestureNormalizationTests
{
    static NativeMacOSGestureEvent Scroll() => new(22, 12, 20, 24, 30, true, 0, 1UL << 20, 4, 0);

    [Fact]
    public void Cocoa_preserves_precise_points_and_inverts_only_the_anchor_origin()
    {
        Assert.True(NativeGestureNormalization.TryMacOS(Scroll(), 100, new Vector2(2, 3), out var precise));
        Assert.Equal(new Vector2(24, 30), precise.ScrollDelta);
        Assert.Equal(new Vector2(24, 240), precise.PositionPixels);
        Assert.Equal(PointerGestureKind.PreciseScroll, precise.Kind);
        Assert.Equal(PointerGestureSource.MacOSNative, precise.Source);
        Assert.True(precise.CommandDown);
    }

    [Theory]
    [InlineData(1UL, PointerGestureInputPhase.Began)]
    [InlineData(2UL, PointerGestureInputPhase.Changed)]
    [InlineData(4UL, PointerGestureInputPhase.Changed)]
    [InlineData(8UL, PointerGestureInputPhase.Ended)]
    [InlineData(16UL, PointerGestureInputPhase.Cancelled)]
    [InlineData(32UL, PointerGestureInputPhase.Began)]
    public void Zero_delta_phases_survive(ulong phase, PointerGestureInputPhase expected)
    {
        var input = Scroll() with { DeltaX = 0, DeltaY = 0, Phase = phase };
        Assert.True(NativeGestureNormalization.TryMacOS(input, 100, Vector2.One, out var sample));
        Assert.Equal(expected, sample.Phase);
        Assert.Equal(Vector2.Zero, sample.ScrollDelta);
    }

    [Fact]
    public void Native_momentum_uses_its_own_phase_and_event_time_control()
    {
        var input = Scroll() with { Phase = 0, MomentumPhase = 8, Modifiers = 1UL << 18 };
        Assert.True(NativeGestureNormalization.TryMacOS(input, 100, Vector2.One, out var sample));
        Assert.Equal(PointerGestureInputPhase.Ended, sample.Phase);
        Assert.True(sample.IsMomentum);
        Assert.True(sample.CommandDown);
    }

    [Theory]
    [InlineData(0.25, 1.25f)]
    [InlineData(-0.25, 0.75f)]
    [InlineData(0, 1f)]
    public void Magnify_is_one_plus_magnification(double magnification, float expected)
    {
        var input = Scroll() with { Type = 30, Magnification = magnification, Phase = 8 };
        Assert.True(NativeGestureNormalization.TryMacOS(input, 100, Vector2.One, out var sample));
        Assert.Equal(PointerGestureKind.Magnify, sample.Kind);
        Assert.Equal(expected, sample.ScaleFactor);
        Assert.Equal(PointerGestureInputPhase.Ended, sample.Phase);
        Assert.Equal(Vector2.Zero, sample.ScrollDelta);
    }

    [Fact]
    public void Fractional_wheel_never_implies_precision()
    {
        var input = Scroll() with { Precise = false, DeltaX = 0.2, DeltaY = 0.3 };
        Assert.True(NativeGestureNormalization.TryMacOS(input, 100, Vector2.One, out var sample));
        Assert.Equal(PointerGestureKind.Wheel, sample.Kind);
        Assert.Equal(PointerGestureSource.ModifierWheelCompatibility, sample.Source);
        var fallback = NativeGestureNormalization.Wheel(new Vector2(0.2f, 0.3f), Vector2.Zero, false);
        Assert.Equal(PointerGestureKind.Wheel, fallback.Kind);
        Assert.Equal(PointerGestureSource.UnknownWheel, fallback.Source);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.MaxValue)]
    public void Nonfinite_or_unrepresentable_values_are_rejected(double bad)
    {
        Assert.False(NativeGestureNormalization.TryMacOS(Scroll() with { X = bad }, 100, Vector2.One, out _));
        Assert.False(NativeGestureNormalization.TryMacOS(Scroll() with { DeltaY = bad }, 100, Vector2.One, out _));
        Assert.False(NativeGestureNormalization.TryMacOS(Scroll() with { Type = 30, Magnification = bad }, 100, Vector2.One, out _));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    public void Nonpositive_magnify_factors_are_rejected(double magnification) =>
        Assert.False(NativeGestureNormalization.TryMacOS(Scroll() with { Type = 30, Magnification = magnification },
            100, Vector2.One, out _));

    [Fact]
    public void Windows_uses_message_control_even_after_key_release_and_preserves_fractional_units()
    {
        Assert.True(NativeGestureNormalization.TryWindowsWheel(0x020A, (60U << 16) | 8U,
            new Vector2(-12, 30), new Vector2(2, 3), out var sample));
        var accumulator = new InputAccumulator();
        accumulator.OnKeyDown(Key.LeftControl);
        accumulator.OnPointerGesture(sample);
        accumulator.OnKeyUp(Key.LeftControl);
        var snapshot = accumulator.Snapshot(Vector2.Zero, true, 800, 600);
        Assert.True(sample.CommandDown);
        Assert.Empty(snapshot.KeysDown);
        Assert.Single(snapshot.Gestures.Samples);
        Assert.Equal(PointerGestureSource.ModifierWheelCompatibility, sample.Source);
        Assert.Equal(new Vector2(0, 0.5f), sample.ScrollDelta);
        Assert.Equal(new Vector2(-24, 90), sample.PositionPixels);
    }

    [Fact]
    public void Windows_signed_words_and_horizontal_sign_match_glfw()
    {
        nint coordinates = unchecked((nint)(((uint)(ushort)-20 << 16) | (ushort)-300));
        Assert.Equal(new Vector2(-300, -20), NativeGestureNormalization.WindowsScreenPosition(coordinates));
        Assert.True(NativeGestureNormalization.TryWindowsWheel(0x020E, unchecked((nuint)((uint)(ushort)-60 << 16)),
            Vector2.Zero, Vector2.One, out var sample));
        Assert.Equal(new Vector2(0.5f, 0), sample.ScrollDelta);
        Assert.Equal(PointerGestureSource.UnknownWheel, sample.Source);
        Assert.False(NativeGestureNormalization.TryWindowsWheel(0x0100, 0, Vector2.Zero, Vector2.One, out _));
    }

    [Fact]
    public void Authoritative_empty_survives_copies_with_retained_legacy_scroll()
    {
        var state = State(PointerGestureFrame.Empty, 5);
        var copied = State(state.Gestures, state.ScrollDelta);
        Assert.True(copied.Gestures.IsAvailable);
        Assert.Empty(copied.Gestures.Samples);
        Assert.Equal(5, copied.ScrollDelta);
        Assert.True(copied.WithoutScroll().Gestures.IsAvailable);
        Assert.True(copied.WithoutPointerGestures().Gestures.IsAvailable);
    }

    [Fact]
    public void Old_synthetic_frames_keep_unavailable_identity_through_typed_copies()
    {
        var old = new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
            new HashSet<MouseButton>(), new HashSet<MouseButton>(), Vector2.Zero, Vector2.Zero, 5, 800, 600);
        var copy = State(old.Gestures, old.ScrollDelta);
        Assert.False(old.Gestures.IsAvailable);
        Assert.Same(old.Gestures, copy.Gestures);
        Assert.False(copy.WithoutScroll().Gestures.IsAvailable);
        Assert.True(new PointerGestureFrame(Array.Empty<PointerGestureSample>()).IsAvailable);
    }

    static InputState State(PointerGestureFrame frame, float scroll) => new(frame, 0, Vector2.Zero, Vector2.One,
        new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(), new HashSet<MouseButton>(),
        new HashSet<MouseButton>(), Vector2.Zero, Vector2.Zero, scroll, 800, 600);
}
