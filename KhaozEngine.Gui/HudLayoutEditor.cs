using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui;

/// <summary>
/// A storage-independent HUD layout draft. The caller draws live or ghost regions and owns modal input,
/// localized controls and persistence. Region identifiers must be unique and stable across frames.
/// </summary>
public sealed class HudLayoutEditor
{
    readonly Dictionary<string, HudLayoutPosition> _draft = new(StringComparer.Ordinal);
    Vector2 _dragViewport;

    public bool IsEditing { get; private set; }
    public string? DraggingId { get; private set; }

    /// <summary>Start a fresh draft, copying valid saved entries and preserving unknown region identifiers.</summary>
    public void Begin(IReadOnlyDictionary<string, HudLayoutPosition>? saved)
    {
        Cancel();
        if (saved is not null)
            foreach (var entry in saved)
                if (!string.IsNullOrWhiteSpace(entry.Key) && HudLayout.IsValid(entry.Value))
                    _draft[entry.Key] = entry.Value;
        IsEditing = true;
    }

    /// <summary>The same resolved rectangle to use for a region's drawing and pointer queries.</summary>
    public Rect Resolve(string id, in Rect defaultBounds, Vector2 viewport)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        HudLayoutPosition? position = _draft.TryGetValue(id, out var saved) ? saved : null;
        return HudLayout.Resolve(defaultBounds, viewport, position);
    }

    /// <summary>
    /// Advance one captured left-button drag. A fresh press belongs to the last drawn region beneath it.
    /// Returns true only when the draft position changed. Update before layers beneath the editor.
    /// </summary>
    public bool Update(Pointer pointer, IReadOnlyList<HudLayoutRegion> regions, Vector2 viewport)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        ArgumentNullException.ThrowIfNull(regions);
        if (!IsEditing) return false;

        if (DraggingId is { } active)
        {
            pointer.ConsumeGesture();
            if (!pointer.IsDown || !pointer.WindowFocused || !HudLayout.HasExtent(viewport))
            {
                DraggingId = null;
                return false;
            }
            int index = IndexOf(regions, active);
            if (index < 0)
            {
                DraggingId = null;
                return false;
            }
            // A new canvas remaps the cursor too. Rebase that frame instead of treating the coordinate
            // change as mouse travel, and leave the saved normalized center untouched.
            if (_dragViewport != viewport)
            {
                _dragViewport = viewport;
                return false;
            }
            Vector2 delta = pointer.Delta;
            if (!float.IsFinite(delta.X) || !float.IsFinite(delta.Y)) return false;
            Rect current = Resolve(active, regions[index].DefaultBounds, viewport);
            Rect proposed = new(current.X + delta.X, current.Y + delta.Y, current.Width, current.Height);
            Rect moved = HudLayout.Resolve(proposed, viewport, null);
            if (moved.X == current.X && moved.Y == current.Y) return false;
            _draft[active] = HudLayout.FromBounds(moved, viewport);
            return true;
        }

        if (!pointer.IsPressOriginFresh || pointer.IsConsumed || !pointer.WindowFocused
            || !HudLayout.HasExtent(viewport)) return false;

        for (int index = regions.Count - 1; index >= 0; index--)
        {
            HudLayoutRegion region = regions[index];
            Rect bounds = Resolve(region.Id, region.DefaultBounds, viewport);
            if (!pointer.IsDown)
            {
                if (!pointer.IsTapIn(bounds)) continue;
                pointer.ConsumeGesture();
                return false;
            }
            if (!pointer.IsDragStartIn(bounds)) continue;
            DraggingId = region.Id;
            _dragViewport = viewport;
            pointer.ConsumeGesture();
            // Travel before the press is not part of this drag.
            return false;
        }
        return false;
    }

    /// <summary>Remove only the supplied region overrides from this draft. Cancel can still undo the reset.</summary>
    public void Reset(IEnumerable<string> regionIds)
    {
        ArgumentNullException.ThrowIfNull(regionIds);
        RequireEditing();
        foreach (string id in regionIds)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            _draft.Remove(id);
            if (StringComparer.Ordinal.Equals(DraggingId, id)) DraggingId = null;
        }
    }

    /// <summary>Return a detached settings snapshot and end the edit session. This method does not save it.</summary>
    public Dictionary<string, HudLayoutPosition> Commit()
    {
        RequireEditing();
        var result = new Dictionary<string, HudLayoutPosition>(_draft, StringComparer.Ordinal);
        Cancel();
        return result;
    }

    /// <summary>Discard the draft and any capture. Safe on repeated close, disconnect or disposal paths.</summary>
    public void Cancel()
    {
        IsEditing = false;
        DraggingId = null;
        _dragViewport = default;
        _draft.Clear();
    }

    void RequireEditing()
    {
        if (!IsEditing) throw new InvalidOperationException("No HUD layout is being edited.");
    }

    static int IndexOf(IReadOnlyList<HudLayoutRegion> regions, string id)
    {
        for (int index = 0; index < regions.Count; index++)
            if (StringComparer.Ordinal.Equals(regions[index].Id, id)) return index;
        return -1;
    }
}
