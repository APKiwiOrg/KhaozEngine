using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui;

/// <summary>Headless detent resolution, exact content handoff and elapsed-time sheet snapping.</summary>
public sealed class BottomSheetMotion
{
    const float SnapSeconds = .18f;
    readonly BottomSheetDetent[] _detents;
    readonly float[] _heights;
    readonly DragMotionTracker _drag;
    readonly string[]? _toggleIds;
    float _available, _contentHeight, _snapOrigin;
    double _snapElapsed;
    bool _resolved, _handle;

    public BottomSheetOptions Options { get; }
    public float Height { get; private set; }
    public float TargetHeight => _heights[Index(TargetDetentId)];
    public float ScrollOffset { get; private set; }
    public float MaxScroll => MathF.Max(0, _contentHeight - MathF.Max(0, Height - Options.HandleHeight));
    public string CurrentDetentId { get; private set; }
    public string TargetDetentId { get; private set; }
    public bool IsAnimating { get; private set; }
    public bool IsDragging => _drag.IsDragging;
    public bool IsActive => _drag.IsActive;
    public bool TapEligible => _drag.TapEligible;
    /// <summary>Fires when a different ID settles, including an immediate SetDetent.</summary>
    public event Action<string>? DetentChanged;
    /// <summary>Fires on arrival from a non-dismissible ID into an explicitly dismissible ID.</summary>
    public event Action? Dismissed;

    public BottomSheetMotion(IReadOnlyList<BottomSheetDetent> detents, string initialDetentId,
        BottomSheetOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(detents);
        if (detents.Count == 0) throw new ArgumentException("At least one detent is required.", nameof(detents));
        Options = options ?? new();
        Options.Validate();
        _detents = new BottomSheetDetent[detents.Count];
        _heights = new float[detents.Count];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < detents.Count; i++)
        {
            var detent = detents[i] ?? throw new ArgumentException("Null detent.", nameof(detents));
            if (!ids.Add(detent.Id)) throw new ArgumentException("Duplicate detent ID.", nameof(detents));
            _detents[i] = detent;
        }
        Index(initialDetentId);
        CurrentDetentId = TargetDetentId = initialDetentId;
        if (Options.HandleToggleDetentIds is { } pair)
        {
            if (pair.Count != 2 || pair[0] == pair[1]) throw new ArgumentException("A distinct detent pair is required.", nameof(options));
            Index(pair[0]);
            Index(pair[1]);
            _toggleIds = new[] { pair[0], pair[1] };
        }
        _drag = new(Options.DragSlop, Options.TapMaxDuration, Options.StationaryReleaseCutoff);
    }

    /// <summary>Resolve against safe height and body-only content height. Changes settle the same target ID.</summary>
    public void Resolve(float availableHeight, float contentHeight)
    {
        BottomSheetOptions.Nonnegative(availableHeight, nameof(availableHeight));
        BottomSheetOptions.Nonnegative(contentHeight, nameof(contentHeight));
        if (_resolved && _available == availableHeight && _contentHeight == contentHeight) return;
        float total = contentHeight + Options.HandleHeight;
        BottomSheetOptions.Nonnegative(total, nameof(contentHeight));
        var resolved = new float[_detents.Length];
        for (int i = 0; i < resolved.Length; i++)
        {
            float height = _detents[i].ResolveHeight(availableHeight, total);
            BottomSheetOptions.Nonnegative(height, nameof(availableHeight));
            resolved[i] = MathF.Min(height, availableHeight);
        }
        resolved.CopyTo(_heights, 0);
        _available = availableHeight;
        _contentHeight = contentHeight;
        _resolved = true;
        Cancel();
        Height = TargetHeight;
        ClampScroll();
        Settle();
    }

    public void SetDetent(string id, bool animated)
    {
        Index(id);
        _drag.Reset();
        TargetDetentId = id;
        _snapOrigin = Height;
        _snapElapsed = 0;
        IsAnimating = animated && _resolved && Height != TargetHeight;
        if (!IsAnimating)
        {
            Height = TargetHeight;
            ClampScroll();
            Settle();
        }
    }

    /// <summary>Caller-driven handle action for keyboard accessibility without global focus ownership.</summary>
    public void ToggleHandle(bool animated = true)
    {
        string? low = _toggleIds?[0], high = _toggleIds?[1];
        if (_toggleIds == null)
        {
            for (int i = 0; i < _detents.Length; i++)
            {
                if (_detents[i].Dismissible) continue;
                if (low == null || _heights[i] < _heights[Index(low)]) low = _detents[i].Id;
                if (high == null || _heights[i] > _heights[Index(high)]) high = _detents[i].Id;
            }
            if (low == high && low != null)
                foreach (var detent in _detents)
                    if (!detent.Dismissible && detent.Id != low) { high = detent.Id; break; }
        }
        if (low == null || high == null || low == high) return;
        SetDetent(TargetDetentId == high ? low : high, animated);
    }

    public void Begin(Vector2 position, bool handle)
    {
        IsAnimating = false;
        _handle = handle;
        _drag.Begin(position);
    }

    public void Move(Vector2 position, float dtSeconds)
    {
        _drag.Move(position, dtSeconds);
        ApplyDrag();
    }

    public void Release(Vector2 position, float dtSeconds)
    {
        _drag.Release(position, dtSeconds);
        ApplyDrag();
        if (!_drag.TapEligible) Snap(_drag.ReleaseVelocity.Y);
    }

    void ApplyDrag()
    {
        if (_handle) ApplyHandleDelta(_drag.DragDelta.Y);
        else ApplyContentDelta(_drag.DragDelta.Y);
    }

    /// <summary>Positive movement goes downward. Consumed plus residual equals the supplied delta.</summary>
    public (float Height, float ScrollOffset, float Consumed, float Residual) ApplyContentDelta(float deltaY)
    {
        ValidateDelta(deltaY);
        IsAnimating = false;
        float remaining = MathF.Abs(deltaY), used = 0;
        if (deltaY < 0)
        {
            float expand = MathF.Min(remaining, Highest() - Height);
            Height += expand;
            remaining -= expand;
            ClampScroll();
            float scroll = MathF.Min(remaining, MaxScroll - ScrollOffset);
            ScrollOffset += scroll;
            used = expand + scroll;
        }
        else
        {
            float scroll = MathF.Min(remaining, ScrollOffset);
            ScrollOffset -= scroll;
            remaining -= scroll;
            float collapse = MathF.Min(remaining, Height - Lowest());
            Height -= collapse;
            used = scroll + collapse;
        }
        float consumed = MathF.CopySign(used, deltaY);
        return (Height, ScrollOffset, consumed, deltaY - consumed);
    }

    /// <summary>Move the sheet alone. Content offset is retained and never receives handle residual.</summary>
    public (float Height, float ScrollOffset, float Consumed, float Residual) ApplyHandleDelta(float deltaY)
    {
        ValidateDelta(deltaY);
        IsAnimating = false;
        float next = (float)Math.Clamp((double)Height - deltaY, Lowest(), Highest());
        float consumed = Height - next;
        Height = next;
        ClampScroll();
        return (Height, ScrollOffset, consumed, deltaY - consumed);
    }

    /// <summary>Discrete wheels scroll only a fully expanded body, with no sheet movement or fling.</summary>
    public void ScrollExpandedContent(float deltaY)
    {
        ValidateDelta(deltaY);
        if (Height == Highest()) ScrollOffset = (float)Math.Clamp((double)ScrollOffset - deltaY, 0, MaxScroll);
    }

    /// <summary>Select nearest bounded height minus vertical velocity divided by eight. Ties use declaration order.</summary>
    public void Snap(float velocityY)
    {
        ValidateDelta(velocityY);
        double projected = Math.Clamp((double)Height - velocityY / 8d, Lowest(), Highest());
        int chosen = 0;
        for (int i = 1; i < _heights.Length; i++)
            if (Math.Abs(_heights[i] - projected) < Math.Abs(_heights[chosen] - projected)) chosen = i;
        SetDetent(_detents[chosen].Id, true);
    }

    public void Advance(float dtSeconds)
    {
        BottomSheetOptions.Nonnegative(dtSeconds, nameof(dtSeconds));
        if (!IsAnimating || dtSeconds == 0) return;
        _snapElapsed += dtSeconds;
        float t = (float)Math.Min(1, _snapElapsed / SnapSeconds);
        Height = _snapOrigin + (TargetHeight - _snapOrigin) * (1 - MathF.Pow(1 - t, 3));
        ClampScroll();
        if (t < 1) return;
        Height = TargetHeight;
        IsAnimating = false;
        Settle();
    }

    public void Cancel() { _drag.Reset(); IsAnimating = false; }

    void Settle()
    {
        if (CurrentDetentId == TargetDetentId) return;
        bool wasDismissible = _detents[Index(CurrentDetentId)].Dismissible;
        CurrentDetentId = TargetDetentId;
        bool dismissed = !wasDismissible && _detents[Index(CurrentDetentId)].Dismissible;
        DetentChanged?.Invoke(CurrentDetentId);
        if (dismissed) Dismissed?.Invoke();
    }

    void ClampScroll() => ScrollOffset = Math.Clamp(ScrollOffset, 0, MaxScroll);
    float Lowest() { float height = float.MaxValue; foreach (float value in _heights) height = MathF.Min(height, value); return height; }
    float Highest() { float height = 0; foreach (float value in _heights) height = MathF.Max(height, value); return height; }
    static void ValidateDelta(float delta) { if (!float.IsFinite(delta)) throw new ArgumentOutOfRangeException(nameof(delta)); }
    int Index(string id)
    {
        for (int i = 0; i < _detents.Length; i++) if (_detents[i].Id == id) return i;
        throw new ArgumentException("Unknown detent ID.", nameof(id));
    }
}
