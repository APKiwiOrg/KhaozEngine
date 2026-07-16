using System;
using System.Collections.Generic;
using KhaozEngine.App;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui;

/// <summary>
/// Headless, retained model for a stack of transient/sticky toast notifications. Holds no rendering or input
/// state: a view widget drives <see cref="Update"/> and reads <see cref="Active"/> to draw. Newest toast is
/// always <c>Active[0]</c>. A toast with a non-null <see cref="Toast.Key"/> replaces any existing toast sharing
/// that key in place, which lets a caller keep re-showing a status message ("server down", then "back online")
/// without the stack growing or the toast changing position.
/// </summary>
public sealed class ToastStack
{
    readonly List<Toast> _active = new();

    /// <summary>Currently visible toasts, newest first (<c>Active[0]</c> is the most recently shown).</summary>
    public IReadOnlyList<Toast> Active => _active;

    /// <summary>
    /// Cap on how many toasts can be visible at once. Default <c>5</c>. When exceeded, the oldest non-sticky
    /// toast is evicted first (see <see cref="Show"/>/<see cref="Update"/>). Only once every remaining toast is
    /// sticky does the oldest sticky toast get evicted. Lowering this at runtime trims on the next
    /// <see cref="Update"/> call, not immediately.
    /// </summary>
    public int MaxVisible { get; set; } = 5;

    /// <summary>Lifetime in seconds used by <see cref="Show"/> when its <c>duration</c> argument is <c>null</c>. Default <c>6</c>.</summary>
    public float DefaultDuration { get; set; } = 6f;

    /// <summary>
    /// Show a toast. When <paramref name="duration"/> is <c>null</c> it defaults to <see cref="DefaultDuration"/>.
    /// Pass <c>&lt;= 0</c> for a sticky toast that never expires on its own (see <see cref="Toast.IsSticky"/>).
    /// When <paramref name="key"/> is non-null and a currently active toast already carries that key, the new
    /// toast replaces it IN PLACE (same index, no reordering, no eviction), which is how a repeated status
    /// update (e.g. "reconnecting" then "connected") stays pinned at its original slot. A <c>null</c> key never
    /// replaces anything: unkeyed toasts always coexist and the new one is inserted at the front.
    /// </summary>
    public Toast Show(LocalizedText message, ToastKind kind = ToastKind.Standard, float? duration = null, string? key = null)
    {
        float resolvedDuration = duration ?? DefaultDuration;
        var toast = new Toast
        {
            Message = message,
            Kind = kind,
            Duration = resolvedDuration,
            Remaining = resolvedDuration,
            Key = key,
        };

        if (key is not null)
        {
            for (int i = 0; i < _active.Count; i++)
            {
                if (string.Equals(_active[i].Key, key, StringComparison.Ordinal))
                {
                    _active[i] = toast;
                    return toast;
                }
            }
        }

        _active.Insert(0, toast);
        TrimToCap();
        return toast;
    }

    /// <summary>Convenience for <see cref="Show"/> with a sticky (non-expiring) duration. See <see cref="Toast.IsSticky"/>.</summary>
    public Toast ShowSticky(LocalizedText message, ToastKind kind = ToastKind.Standard, string? key = null) =>
        Show(message, kind, 0f, key);

    /// <summary>Remove this exact toast instance. Returns <c>false</c> if it is not currently active.</summary>
    public bool Dismiss(Toast toast) => _active.Remove(toast);

    /// <summary>Remove the toast at <paramref name="index"/> (0 = newest). Out-of-range is a no-op returning <c>false</c>.</summary>
    public bool Dismiss(int index)
    {
        if (index < 0 || index >= _active.Count) return false;
        _active.RemoveAt(index);
        return true;
    }

    /// <summary>Remove the active toast with this <paramref name="key"/> (ordinal comparison). Returns <c>false</c> when no toast carries it.</summary>
    public bool Clear(string key)
    {
        for (int i = 0; i < _active.Count; i++)
        {
            if (string.Equals(_active[i].Key, key, StringComparison.Ordinal))
            {
                _active.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    /// <summary>Remove every active toast.</summary>
    public void ClearAll() => _active.Clear();

    /// <summary>
    /// Advance every non-sticky toast's <see cref="Toast.Remaining"/> by <paramref name="realDt"/> and drop any
    /// that expire, then enforce <see cref="MaxVisible"/>. <paramref name="realDt"/> MUST be a raw, unscaled
    /// frame delta (<see cref="Frame.Dt"/> or <see cref="GameClock.RealDeltaSeconds"/>), never the scaled
    /// simulation delta, so toasts keep counting down at real speed while the game is paused or slowed. A no-op
    /// on an empty stack.
    /// </summary>
    public void Update(float realDt)
    {
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            Toast toast = _active[i];
            if (toast.IsSticky) continue;
            toast.Remaining -= realDt;
            if (toast.Remaining <= 0f) _active.RemoveAt(i);
        }
        TrimToCap();
    }

    /// <summary>
    /// Enforce <see cref="MaxVisible"/> (clamped to a minimum of 1): while over the cap, evict the oldest
    /// non-sticky toast (scanning from the end). Once every remaining toast is sticky, evict the oldest sticky
    /// toast instead, so the stack stays bounded even under an all-sticky flood.
    /// </summary>
    void TrimToCap()
    {
        int cap = Math.Max(1, MaxVisible);
        while (_active.Count > cap)
        {
            int victim = -1;
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                if (!_active[i].IsSticky)
                {
                    victim = i;
                    break;
                }
            }
            if (victim < 0) victim = _active.Count - 1;
            _active.RemoveAt(victim);
        }
    }
}
