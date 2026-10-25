using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui;

/// <summary>A drawing-free horizontal strip with anchored snapping and single-pointer drag admission.</summary>
/// <remarks>Update every open frame before lower pointer consumers, even without input eligibility.
/// Layout and item geometry use Pointer coordinates. The caller owns drawing, clipping, selection and wheel
/// policy. Draw only the visible range inside the caller's clip, then restore it before drawing tooltips.</remarks>
public sealed class SnapStrip
{
    readonly SnapStripMotion _motion;
    readonly DragMotionTracker _drag;
    PointerCoordinates _coordinates;
    bool _hasCoordinates, _sequence, _consumeUntilRelease;
    bool _hoverEnabled = true;

    /// <summary>Creates an empty strip with valid default metrics.</summary>
    /// <param name="snapSeconds">The finite, nonnegative snap duration. Zero snaps immediately.</param>
    /// <param name="dragSlop">The finite, nonnegative horizontal travel required before committing a drag.</param>
    /// <exception cref="ArgumentOutOfRangeException">A duration or slop is negative or nonfinite.</exception>
    public SnapStrip(float snapSeconds = 0.18f, float dragSlop = 8f)
    {
        _motion = new(snapSeconds);
        _drag = new(dragSlop);
    }

    /// <summary>Gets the caller-owned viewport in drawing and pointer coordinates.</summary>
    public Rect Bounds { get; private set; }

    /// <summary>Gets the number of items.</summary>
    public int ItemCount => _motion.ItemCount;

    /// <summary>Gets the item dimensions and viewport anchor.</summary>
    public SnapStripMetrics Metrics => _motion.Metrics;

    /// <summary>Gets the displayed horizontal scroll offset.</summary>
    public float ScrollOffset => _motion.Offset;

    /// <summary>Gets the content width without trailing spacing.</summary>
    public float ContentWidth => SnapStripGeometry.ContentWidth(ItemCount, Metrics);

    /// <summary>Gets the maximum scroll offset.</summary>
    public float MaxScroll => SnapStripGeometry.MaxScroll(Bounds.Width, ItemCount, Metrics);

    /// <summary>Gets the requested logical target, or -1 for empty content.</summary>
    public int TargetIndex => _motion.TargetIndex;

    /// <summary>Gets whether the displayed offset is easing toward its target.</summary>
    public bool IsAnimating => _motion.IsAnimating;

    /// <summary>Gets whether the admitted contact has crossed horizontal drag slop.</summary>
    public bool IsDragging => _drag.IsDragging;

    /// <summary>Gets whether an earlier index has a stop distinct from the pending target's stop.</summary>
    public bool CanStepPrevious => _motion.CanStepPrevious;

    /// <summary>Gets whether a later index has a stop distinct from the pending target's stop.</summary>
    public bool CanStepNext => _motion.CanStepNext;

    /// <summary>Gets or sets whether Update reserves the viewport for lower pointer consumers. Defaults to true.</summary>
    /// <remarks>Reservation does not consume taps. Committed drags consume gestures regardless of this setting.</remarks>
    public bool BlocksPointer { get; set; } = true;

    /// <summary>Configures the viewport and content, preserving and clamping the logical target.</summary>
    /// <param name="bounds">The finite viewport with nonnegative dimensions.</param>
    /// <param name="itemCount">The nonnegative number of items.</param>
    /// <param name="metrics">The valid item dimensions and anchor, even for empty content.</param>
    /// <remarks>Width, count or metrics changes cancel contact ownership and settle the logical target immediately.
    /// Other bounds changes cancel contact ownership, preserving independent animation.
    /// Identical layouts preserve interaction and animation. Invalid layouts change nothing.
    /// Cancelled held contacts cannot restart, and committed gestures remain consumed through release.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The layout is invalid or its derived extent is nonfinite.</exception>
    public void Configure(in Rect bounds, int itemCount, in SnapStripMetrics metrics)
    {
        _ = SnapStripGeometry.VisibleRange(bounds, ScrollOffset, itemCount, metrics);
        if (Bounds == bounds && ItemCount == itemCount && Metrics == metrics) return;
        if (Bounds.Width != bounds.Width || ItemCount != itemCount || Metrics != metrics)
        {
            _motion.Configure(bounds.Width, itemCount, metrics);
            _drag.Reset();
        }
        else CancelInteraction();
        Bounds = bounds;
    }

    /// <summary>Targets an item at the viewport anchor and cancels contact ownership.</summary>
    /// <param name="index">The requested index, clamped to the available items.</param>
    /// <param name="animated">Whether to ease from the displayed offset.</param>
    /// <remarks>The command retains its requested logical index, including during an interrupted drag.</remarks>
    public void CenterOn(int index, bool animated = true)
    {
        _drag.Reset();
        _motion.CenterOn(index, animated);
    }

    /// <summary>Targets the nearest earlier index with a distinct clamped stop and cancels contact ownership.</summary>
    /// <param name="animated">Whether to ease from the displayed offset.</param>
    /// <returns>True if a distinct earlier stop exists.</returns>
    public bool StepPrevious(bool animated = true)
    {
        _drag.Reset();
        return _motion.StepPrevious(animated);
    }

    /// <summary>Targets the nearest later index with a distinct clamped stop and cancels contact ownership.</summary>
    /// <param name="animated">Whether to ease from the displayed offset.</param>
    /// <returns>True if a distinct later stop exists.</returns>
    public bool StepNext(bool animated = true)
    {
        _drag.Reset();
        return _motion.StepNext(animated);
    }

    /// <summary>Processes one already-updated Pointer and its matching immutable input snapshot.</summary>
    /// <param name="pointer">The frame's Pointer. This widget never updates it.</param>
    /// <param name="input">The same snapshot used to update Pointer.</param>
    /// <param name="dtSeconds">Finite, nonnegative elapsed seconds.</param>
    /// <param name="receivesInput">Whether this widget may admit input and report hover.</param>
    /// <remarks>Admission precedes self-reservation and requires a fresh, unconsumed, unblocked, unoccluded
    /// origin. Rejected contacts stay rejected until physical release. Admitted contacts pause easing.
    /// A committed drag remains owned outside the viewport and consumes through release. Final release
    /// travel precedes snapping, which starts advancing on the next update. Motion advances without input.
    /// Input loss, focus loss, pointer capture, origin/current-point occlusion, nonfinite positions and mapping
    /// changes cancel interaction until the held contact releases. Unrelated reservations do not cancel an
    /// owned drag. An independent animation continues during cancellation.</remarks>
    /// <exception cref="ArgumentNullException">Pointer or input is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Elapsed seconds are negative or nonfinite.</exception>
    public void Update(Pointer pointer, InputState input, float dtSeconds, bool receivesInput = true)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        ArgumentNullException.ThrowIfNull(input);
        if (!float.IsFinite(dtSeconds) || dtSeconds < 0f)
            throw new ArgumentOutOfRangeException(nameof(dtSeconds));

        bool held = input.IsDown(MouseButton.Left);
        bool sameFrameTap = pointer.IsPressOriginFresh && pointer.IsJustReleased;
        bool mappingChanged = _hasCoordinates && !_coordinates.HasSameMapping(pointer.Coordinates);
        _coordinates = pointer.Coordinates;
        _hasCoordinates = true;
        _hoverEnabled = receivesInput && input.WindowFocused && !input.PointerCaptured
            && ValidMapping(_coordinates) && Finite(pointer.Position);

        bool contactOccluded = _drag.IsActive
            && (pointer.IsOccluded(pointer.PressOrigin) || pointer.IsOccluded(pointer.Position));
        if (!_hoverEnabled || mappingChanged || contactOccluded)
        {
            CancelInteraction();
            _sequence |= held || sameFrameTap;
        }
        else if (!_sequence && (held || sameFrameTap))
        {
            _sequence = true;
            if (pointer.IsPressOriginFresh && !pointer.IsConsumed && Finite(pointer.PressOrigin)
                && !pointer.IsBlocked(pointer.PressOrigin)
                && (held ? pointer.IsDragStartIn(Bounds) : pointer.IsTapIn(Bounds)))
            {
                _drag.Begin(new(pointer.PressOrigin.X, 0f));
                _motion.BeginDrag();
            }
        }

        bool direct = _drag.IsActive;
        if (direct) UpdateContact(pointer.Position.X, held, dtSeconds);
        else _motion.Advance(dtSeconds);

        if (_consumeUntilRelease) pointer.ConsumeGesture();
        // An unfocused snapshot may contain a synthetic release while the physical button stays held.
        if (!held && input.WindowFocused)
        {
            _sequence = false;
            _consumeUntilRelease = false;
        }
        if (BlocksPointer && Bounds.Width > 0f && Bounds.Height > 0f) pointer.BlockRegion(Bounds);
    }

    /// <summary>Cancels contact ownership and immediately settles an active drag.</summary>
    /// <remarks>Unmoved contacts settle at the pending target. Moved drags settle at their nearest stop.
    /// Held contacts cannot restart until release, and committed gestures remain consumed through release.
    /// An independent animation continues unchanged.</remarks>
    public void CancelInteraction()
    {
        _drag.Reset();
        _motion.CancelDrag();
    }

    /// <summary>Gets an item's full, unclipped rectangle at the displayed offset.</summary>
    /// <param name="index">An index within the available items.</param>
    /// <returns>The item's width and viewport height, even when offscreen.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the available items.</exception>
    public Rect ItemBounds(int index) => SnapStripGeometry.ItemBounds(Bounds, index, ScrollOffset, ItemCount, Metrics);

    /// <summary>Finds an item using half-open viewport and item bounds at the displayed offset.</summary>
    /// <param name="point">A finite point in Pointer coordinates.</param>
    /// <returns>The item index, or -1 for gaps and points outside the viewport.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The point is nonfinite.</exception>
    public int ItemAt(Vector2 point) => SnapStripGeometry.IndexAt(Bounds, point, ScrollOffset, ItemCount, Metrics);

    /// <summary>Gets the item under an eligible hovering pointer at the displayed offset.</summary>
    /// <param name="pointer">The current frame's Pointer.</param>
    /// <returns>The item index, or -1 when input, focus, capture, occlusion or a held contact prevents hover.</returns>
    /// <exception cref="ArgumentNullException">Pointer is null.</exception>
    public int HoveredItemIndex(Pointer pointer)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        return _hoverEnabled && !IsDragging && pointer.IsHoveringIn(Bounds) ? ItemAt(pointer.Position) : -1;
    }

    /// <summary>Gets the contiguous range of items intersecting the viewport with positive area.</summary>
    public (int First, int Count) VisibleRange => SnapStripGeometry.VisibleRange(Bounds, ScrollOffset, ItemCount, Metrics);

    void UpdateContact(float x, bool held, float dtSeconds)
    {
        Vector2 position = new(x, 0f);
        if (held)
        {
            _drag.Move(position, dtSeconds);
            _consumeUntilRelease |= _drag.IsDragging;
        }
        else
        {
            bool wasDragging = _drag.IsDragging;
            _drag.Release(position, dtSeconds);
            _consumeUntilRelease |= wasDragging || _drag.DragDelta != Vector2.Zero;
        }
        _motion.DragBy(_drag.DragDelta.X);
        if (!held) _motion.EndDrag();
    }

    static bool Finite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);

    static bool ValidMapping(PointerCoordinates coordinates) => Finite(coordinates.ViewportScale)
        && coordinates.ViewportScale.X > 0f && coordinates.ViewportScale.Y > 0f && Finite(coordinates.ViewportOffset);
}
