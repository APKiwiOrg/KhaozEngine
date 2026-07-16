using System;
using System.Collections.Generic;
using System.Globalization;

namespace KhaozEngine.MapEditor;

/// <summary>A togglable draw / pick group in the editor viewport. Each group gates a whole class of authored
/// content at once (independent of the per-element hide set), so the operator can peel back one kind of clutter
/// while editing a layered scene. <see cref="Water"/> gates the single water plane, and the others gate their
/// matching document collection.</summary>
public enum VisibilityGroup
{
    /// <summary>Authored prop / building placements.</summary>
    Placements,
    /// <summary>NPC spawn markers.</summary>
    Spawns,
    /// <summary>The document's water plane.</summary>
    Water,
    /// <summary>Scatter exclusion shapes.</summary>
    Exclusions,
    /// <summary>Scatter override shapes (region-scoped density / kind tweaks).</summary>
    ScatterOverrides,
    /// <summary>Named gameplay regions.</summary>
    Regions,
    /// <summary>Terrain feature center markers.</summary>
    FeatureMarkers,
    /// <summary>Player start markers.</summary>
    PlayerSpawns,
}

/// <summary>Editor-session visibility state: which whole groups (<see cref="VisibilityGroup"/>) draw, which named
/// scatter layers stream, and which individual elements are hidden. It is view-only, NOT part of the
/// <see cref="MapDoc.MapDocument"/> and never saved, so hiding a thing never mutates the document (it stays in the
/// outline). The draw and pick paths consult <see cref="IsElementVisible"/> (group gate AND per-element hide), the
/// water draw consults <see cref="GetGroup"/> with <see cref="VisibilityGroup.Water"/>, and the streamed-world
/// rebuild consults <see cref="GetLayer"/>. Plain and headless: every default is "visible", so an untouched
/// instance shows everything.</summary>
public sealed class EditorVisibility
{
    // Only overridden groups / layers / elements are stored: an absent group or layer defaults to visible, and an
    // absent element is not hidden. So a fresh instance shows everything without pre-populating any collection.
    readonly Dictionary<VisibilityGroup, bool> _groups = new();
    readonly Dictionary<string, bool> _layers = new(StringComparer.Ordinal);
    readonly HashSet<(SelectionKind Kind, string Id)> _hidden = new();

    /// <summary>Whether <paramref name="group"/> is visible (the default for a group never toggled).</summary>
    public bool GetGroup(VisibilityGroup group) => !_groups.TryGetValue(group, out bool visible) || visible;

    /// <summary>Sets whether <paramref name="group"/> is visible.</summary>
    public void SetGroup(VisibilityGroup group, bool visible) => _groups[group] = visible;

    /// <summary>Whether the scatter layer named <paramref name="name"/> is visible (streamed). An unknown or
    /// never-toggled layer defaults to visible. The streamed-world rebuild skips a hidden layer's prop layers.</summary>
    public bool GetLayer(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return !_layers.TryGetValue(name, out bool visible) || visible;
    }

    /// <summary>Sets whether the scatter layer named <paramref name="name"/> is visible (streamed).</summary>
    public void SetLayer(string name, bool visible)
    {
        ArgumentNullException.ThrowIfNull(name);
        _layers[name] = visible;
    }

    /// <summary>Moves the visibility override for a renamed scatter layer from <paramref name="from"/> to
    /// <paramref name="to"/>, so a hidden layer stays hidden across a rename (the layer keys visibility by name).
    /// A no-op when the layer had no explicit override (it defaulted to visible under either name, so nothing to
    /// carry) or when the names match. The editor calls this alongside a <see cref="RenameScatterLayerCommand"/>
    /// so the Layers visibility panel and the streamed-world filter both follow the rename. Undo/redo of the
    /// rename does NOT re-follow the override (the same v1 residual as the reorder-hide remap), since visibility
    /// is view-only and never part of the reversible document.</summary>
    public void RenameLayer(string from, string to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (string.Equals(from, to, StringComparison.Ordinal)) return;
        if (!_layers.TryGetValue(from, out bool visible)) return;
        _layers.Remove(from);
        _layers[to] = visible;
    }

    /// <summary>Whether the individual element (<paramref name="kind"/>, <paramref name="id"/>) is hidden,
    /// independent of its group. The key is the pair, so the same id under two kinds is two distinct elements.</summary>
    public bool IsElementHidden(SelectionKind kind, string id) => _hidden.Contains((kind, id ?? ""));

    /// <summary>Hides (<paramref name="hidden"/> true) or shows the individual element (<paramref name="kind"/>,
    /// <paramref name="id"/>). Independent of the element's group toggle.</summary>
    public void SetElementHidden(SelectionKind kind, string id, bool hidden)
    {
        (SelectionKind Kind, string Id) key = (kind, id ?? "");
        if (hidden) _hidden.Add(key);
        else _hidden.Remove(key);
    }

    /// <summary>Whether the element (<paramref name="kind"/>, <paramref name="id"/>) both draws and picks: its
    /// group (when it has one) must be visible AND it must not be individually hidden. A kind with no group
    /// (<see cref="SelectionKind.Terrain"/>, <see cref="SelectionKind.None"/>) has no group gate. The draw and
    /// pick paths call this so a hidden element is neither drawn nor selectable from the viewport.</summary>
    public bool IsElementVisible(SelectionKind kind, string id)
    {
        if (TryGroupFor(kind, out VisibilityGroup group) && !GetGroup(group)) return false;
        return !IsElementHidden(kind, id);
    }

    /// <summary>The visibility group that gates a selection kind, or false for a kind with no group
    /// (<see cref="SelectionKind.Terrain"/> / <see cref="SelectionKind.None"/>).</summary>
    public static bool TryGroupFor(SelectionKind kind, out VisibilityGroup group)
    {
        switch (kind)
        {
            case SelectionKind.Placement: group = VisibilityGroup.Placements; return true;
            case SelectionKind.Spawn: group = VisibilityGroup.Spawns; return true;
            case SelectionKind.PlayerSpawn: group = VisibilityGroup.PlayerSpawns; return true;
            case SelectionKind.Exclusion: group = VisibilityGroup.Exclusions; return true;
            case SelectionKind.ScatterOverride: group = VisibilityGroup.ScatterOverrides; return true;
            case SelectionKind.Region: group = VisibilityGroup.Regions; return true;
            case SelectionKind.Feature: group = VisibilityGroup.FeatureMarkers; return true;
            default: group = default; return false;
        }
    }

    /// <summary>Shifts per-element hides across a list reorder of <paramref name="kind"/> (feature, exclusion, or
    /// scatter override, the index-keyed kinds): the moved element's own hide (if any) follows it from
    /// <paramref name="fromIndex"/> to <paramref name="toIndex"/>, and every OTHER hidden index inside the
    /// shifted-through range moves one slot to make room, exactly mirroring the RemoveAt(from) + Insert(to) list
    /// move the reorder commands themselves perform (<see cref="ReorderFeatureCommand"/> /
    /// <see cref="ReorderExclusionCommand"/>), so a hide stays glued to the ELEMENT rather than the slot. Handles
    /// both drag directions (from below to, or from above to) and is a no-op when the indices are equal. Driven by
    /// the reorder command's <see cref="IVisibilityEffect"/> through the <see cref="EditorDocument"/> event path,
    /// so a hide survives undo and redo of the reorder: applying the command remaps forward, and undoing it applies
    /// the inverse (<c>RemapIndex(toIndex, fromIndex)</c>), the same swap the command's own Revert performs on the
    /// list.</summary>
    public void RemapIndex(SelectionKind kind, int fromIndex, int toIndex)
    {
        if (fromIndex == toIndex) return;

        List<(int OldIndex, int NewIndex)> moves = new();
        foreach ((SelectionKind Kind, string Id) key in _hidden)
        {
            if (key.Kind != kind) continue;
            if (!int.TryParse(key.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)) continue;
            int moved = MoveIndex(index, fromIndex, toIndex);
            if (moved != index) moves.Add((index, moved));
        }
        // Two passes (remove every old key, THEN add every new key): an interleaved remove+add per move can drop
        // an entry when one move's new index is the same string key as another move's not-yet-processed old
        // index (the hash set cannot tell the two apart), silently losing a hide.
        foreach ((int oldIndex, int _) in moves) _hidden.Remove((kind, oldIndex.ToString(CultureInfo.InvariantCulture)));
        foreach ((int _, int newIndex) in moves) _hidden.Add((kind, newIndex.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>Drops the hide entry for the element removed at <paramref name="index"/> of <paramref name="kind"/>
    /// (if it was hidden) and shifts every later hidden index of that kind down by one, mirroring the list's own
    /// RemoveAt(index) shift, so a hide stays glued to the surviving elements' identities. Driven by the remove
    /// command's <see cref="IVisibilityEffect"/> through the <see cref="EditorDocument"/> event path. Undoing the
    /// delete applies the inverse (<see cref="InsertIndex"/>), which shifts the surviving hides back up. The removed
    /// element's OWN hide is dropped here and not recovered on undo (it was never part of the reversible document),
    /// an accepted view-only residual.</summary>
    public void RemoveIndex(SelectionKind kind, int index)
    {
        _hidden.Remove((kind, index.ToString(CultureInfo.InvariantCulture)));

        List<(int OldIndex, int NewIndex)> moves = new();
        foreach ((SelectionKind Kind, string Id) key in _hidden)
        {
            if (key.Kind != kind) continue;
            if (!int.TryParse(key.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) continue;
            if (i > index) moves.Add((i, i - 1));
        }
        foreach ((int oldIndex, int _) in moves) _hidden.Remove((kind, oldIndex.ToString(CultureInfo.InvariantCulture)));
        foreach ((int _, int newIndex) in moves) _hidden.Add((kind, newIndex.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>Shifts every hidden index of <paramref name="kind"/> at or above <paramref name="index"/> up by one,
    /// making room for an element re-inserted at <paramref name="index"/>, the exact inverse of
    /// <see cref="RemoveIndex"/>'s shift-down. Drives the undo of a delete through the remove command's
    /// <see cref="IVisibilityEffect"/> (the <see cref="EditorDocument"/> event path), so a hide on a surviving
    /// element lands back on its original slot. It does NOT restore a hide for the re-inserted element itself: that
    /// hide was dropped by the forward <see cref="RemoveIndex"/> and is not part of the reversible document (the same
    /// accepted view-only residual documented there).</summary>
    public void InsertIndex(SelectionKind kind, int index)
    {
        List<(int OldIndex, int NewIndex)> moves = new();
        foreach ((SelectionKind Kind, string Id) key in _hidden)
        {
            if (key.Kind != kind) continue;
            if (!int.TryParse(key.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) continue;
            if (i >= index) moves.Add((i, i + 1));
        }
        // Two passes (remove every old key, THEN add every new key), for the same reason RemapIndex does: an
        // interleaved remove+add can drop an entry when one shifted key equals another not-yet-processed old key.
        foreach ((int oldIndex, int _) in moves) _hidden.Remove((kind, oldIndex.ToString(CultureInfo.InvariantCulture)));
        foreach ((int _, int newIndex) in moves) _hidden.Add((kind, newIndex.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>Moves the hide entry for an id/name-keyed element of <paramref name="kind"/> from
    /// <paramref name="oldKey"/> to <paramref name="newKey"/> when it is hidden, a no-op otherwise (nothing hidden
    /// under the old key, or the keys match). Drives a rename through the rename command's
    /// <see cref="IVisibilityEffect"/> (the <see cref="EditorDocument"/> event path) so a hide follows the element
    /// across execute, undo, and redo of the rename, leaving no orphan hide under the abandoned key.</summary>
    public void RenameKey(SelectionKind kind, string oldKey, string newKey)
    {
        ArgumentNullException.ThrowIfNull(oldKey);
        ArgumentNullException.ThrowIfNull(newKey);
        if (string.Equals(oldKey, newKey, StringComparison.Ordinal)) return;
        if (!_hidden.Remove((kind, oldKey))) return;
        _hidden.Add((kind, newKey));
    }

    // The RemoveAt(from) + Insert(to) list-move remap for a single index, the same formula the reorder commands
    // apply to the list itself: the moved element lands at `to`, everything strictly between the two endpoints
    // shifts one slot toward `from` to make room, and everything else is untouched.
    static int MoveIndex(int index, int from, int to)
    {
        if (index == from) return to;
        if (from < to) return index > from && index <= to ? index - 1 : index;
        return index >= to && index < from ? index + 1 : index;
    }
}
