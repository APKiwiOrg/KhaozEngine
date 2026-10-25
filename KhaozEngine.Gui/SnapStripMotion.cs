using System;

namespace KhaozEngine.Gui;

/// <summary>Anchored strip motion with direct dragging, distinct clamped stops and elapsed-time easing.</summary>
/// <remarks>The logical target remains independent of the displayed offset during animation.
/// This model owns no drawing, selection or input policy.</remarks>
public sealed class SnapStripMotion
{
    readonly float _snapSeconds;
    float _snapOrigin;
    double _snapElapsed;
    bool _dragMoved;

    /// <summary>Creates an empty strip with valid default metrics and no pending animation.</summary>
    /// <param name="snapSeconds">The finite, nonnegative duration of each snap. Zero snaps immediately.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="snapSeconds"/> is negative or nonfinite.</exception>
    public SnapStripMotion(float snapSeconds = 0.18f)
    {
        if (!float.IsFinite(snapSeconds) || snapSeconds < 0f)
            throw new ArgumentOutOfRangeException(nameof(snapSeconds));
        _snapSeconds = snapSeconds;
    }

    /// <summary>Gets the number of items in the strip.</summary>
    public int ItemCount { get; private set; }

    /// <summary>Gets the viewport width in the caller's drawing and pointer coordinate units.</summary>
    public float ViewportWidth { get; private set; }

    /// <summary>Gets the item dimensions and viewport anchor.</summary>
    public SnapStripMetrics Metrics { get; private set; } = new(1f);

    /// <summary>Gets the displayed horizontal scroll offset.</summary>
    public float Offset { get; private set; }

    /// <summary>Gets the clamped scroll stop for the logical target.</summary>
    public float TargetOffset { get; private set; }

    /// <summary>Gets the requested logical item index, or -1 when the strip is empty.</summary>
    public int TargetIndex { get; private set; } = -1;

    /// <summary>Gets whether elapsed-time motion is still approaching its target.</summary>
    public bool IsAnimating { get; private set; }

    /// <summary>Gets whether a direct drag is active.</summary>
    public bool IsDragActive { get; private set; }

    /// <summary>Gets whether an earlier index has a stop distinct from the pending target's stop.</summary>
    public bool CanStepPrevious => FindStep(previous: true) >= 0;

    /// <summary>Gets whether a later index has a stop distinct from the pending target's stop.</summary>
    public bool CanStepNext => FindStep(previous: false) >= 0;

    /// <summary>Configures the strip, retaining and clamping its logical target.</summary>
    /// <param name="viewportWidth">The finite, nonnegative viewport width.</param>
    /// <param name="itemCount">The nonnegative number of items.</param>
    /// <param name="metrics">The valid item dimensions and viewport anchor, even for empty content.</param>
    /// <remarks>Changed layouts settle immediately. Identical layouts preserve ongoing motion.
    /// Invalid layouts leave all state unchanged.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The viewport, count or metrics are invalid,
    /// or the derived pitch or extent exceeds a finite float.</exception>
    public void Configure(float viewportWidth, int itemCount, in SnapStripMetrics metrics)
    {
        _ = SnapStripGeometry.MaxScroll(viewportWidth, itemCount, metrics);
        if (ViewportWidth == viewportWidth && ItemCount == itemCount && Metrics == metrics) return;
        ViewportWidth = viewportWidth;
        ItemCount = itemCount;
        Metrics = metrics;
        CenterOn(TargetIndex, false);
    }

    /// <summary>Targets an item at the viewport anchor without canonicalizing its logical index.</summary>
    /// <param name="index">The requested index, clamped to the available items.</param>
    /// <param name="animated">Whether to ease from the currently displayed offset.</param>
    /// <remarks>Commands end active drags without reinterpreting the displayed offset as the logical target.
    /// Outside a drag, an identical animated request does not restart elapsed time.
    /// An immediate request settles even when that same target is already pending.</remarks>
    public void CenterOn(int index, bool animated = true)
    {
        int targetIndex = ItemCount == 0 ? -1 : Math.Clamp(index, 0, ItemCount - 1);
        float targetOffset = SnapStripGeometry.CenterOffset(targetIndex, ViewportWidth, ItemCount, Metrics);
        bool wasDragging = IsDragActive;
        IsDragActive = false;
        _dragMoved = false;
        if (animated && !wasDragging && targetIndex == TargetIndex && targetOffset == TargetOffset) return;
        TargetIndex = targetIndex;
        TargetOffset = targetOffset;
        _snapOrigin = Offset;
        _snapElapsed = 0d;
        IsAnimating = animated && _snapSeconds > 0f && Offset != TargetOffset;
        if (!IsAnimating) Offset = TargetOffset;
    }

    /// <summary>Targets the nearest earlier index whose clamped stop differs from the pending target's.</summary>
    /// <param name="animated">Whether to ease from the currently displayed offset.</param>
    /// <returns>True if a distinct stop exists. A failed step preserves the logical target.</returns>
    /// <remarks>Ends an active drag. Otherwise a failed step leaves all motion state unchanged.</remarks>
    public bool StepPrevious(bool animated = true) => Step(previous: true, animated);

    /// <summary>Targets the nearest later index whose clamped stop differs from the pending target's.</summary>
    /// <param name="animated">Whether to ease from the currently displayed offset.</param>
    /// <returns>True if a distinct stop exists. A failed step preserves the logical target.</returns>
    /// <remarks>Ends an active drag. Otherwise a failed step leaves all motion state unchanged.</remarks>
    public bool StepNext(bool animated = true) => Step(previous: false, animated);

    /// <summary>Begins direct dragging and pauses animation while retaining the pending logical target.</summary>
    /// <remarks>Repeated calls during an active drag leave it unchanged.</remarks>
    public void BeginDrag()
    {
        if (IsDragActive) return;
        IsDragActive = true;
        _dragMoved = false;
        IsAnimating = false;
    }

    /// <summary>Subtracts pointer travel from the displayed offset and clamps immediately without overscroll.</summary>
    /// <param name="pointerDeltaX">Finite horizontal pointer travel. Positive travel decreases the scroll offset.</param>
    /// <remarks>Without an active drag, valid travel leaves motion unchanged.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pointerDeltaX"/> is nonfinite.</exception>
    public void DragBy(float pointerDeltaX)
    {
        if (!float.IsFinite(pointerDeltaX))
            throw new ArgumentOutOfRangeException(nameof(pointerDeltaX));
        if (!IsDragActive) return;
        float maxScroll = SnapStripGeometry.MaxScroll(ViewportWidth, ItemCount, Metrics);
        float offset = (float)Math.Clamp((double)Offset - pointerDeltaX, 0d, maxScroll);
        _dragMoved |= offset != Offset;
        Offset = offset;
    }

    /// <summary>Ends an active drag and eases to its nearest canonical stop after actual movement.</summary>
    /// <remarks>An unmoved drag resumes the pending target. Fitting content retains its logical anchor.
    /// Without an active drag, motion remains unchanged. Release uses no velocity projection.</remarks>
    public void EndDrag() => SettleDrag(animated: true);

    /// <summary>Immediately settles an active drag at its nearest canonical stop after actual movement.</summary>
    /// <remarks>An unmoved drag settles at the pending target. Without an active drag, motion remains unchanged.</remarks>
    public void CancelDrag() => SettleDrag(animated: false);

    /// <summary>Advances cubic ease-out motion from its fixed origin and arrives exactly at the target.</summary>
    /// <param name="dtSeconds">The finite, nonnegative elapsed seconds. Zero leaves motion unchanged.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="dtSeconds"/> is negative or nonfinite.</exception>
    public void Advance(float dtSeconds)
    {
        if (!float.IsFinite(dtSeconds) || dtSeconds < 0f)
            throw new ArgumentOutOfRangeException(nameof(dtSeconds));
        if (!IsAnimating || dtSeconds == 0f) return;
        _snapElapsed += dtSeconds;
        if (_snapElapsed >= _snapSeconds)
        {
            Offset = TargetOffset;
            IsAnimating = false;
            return;
        }

        double remaining = 1d - _snapElapsed / _snapSeconds;
        double eased = 1d - remaining * remaining * remaining;
        Offset = (float)(_snapOrigin + ((double)TargetOffset - _snapOrigin) * eased);
    }

    bool Step(bool previous, bool animated)
    {
        int index = FindStep(previous);
        if (index < 0)
        {
            if (IsDragActive) CenterOn(TargetIndex, animated);
            return false;
        }
        CenterOn(index, animated);
        return true;
    }

    void SettleDrag(bool animated)
    {
        if (!IsDragActive) return;
        int index = _dragMoved
            ? SnapStripGeometry.NearestIndex(Offset, ViewportWidth, ItemCount, Metrics)
            : TargetIndex;
        CenterOn(index, animated);
    }

    int FindStep(bool previous)
    {
        if (TargetIndex < 0) return -1;
        int lower = previous ? 0 : TargetIndex + 1;
        int upper = previous ? TargetIndex - 1 : ItemCount - 1;
        int candidate = -1;
        // Stops are monotone, so skip even very large clamped plateaus without scanning each item.
        while (lower <= upper)
        {
            int middle = lower + (upper - lower) / 2;
            float offset = SnapStripGeometry.CenterOffset(middle, ViewportWidth, ItemCount, Metrics);
            if (offset == TargetOffset)
            {
                if (previous) upper = middle - 1;
                else lower = middle + 1;
            }
            else
            {
                candidate = middle;
                if (previous) lower = middle + 1;
                else upper = middle - 1;
            }
        }

        return candidate;
    }
}
