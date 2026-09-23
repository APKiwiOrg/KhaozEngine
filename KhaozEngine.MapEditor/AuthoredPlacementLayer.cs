using System;
using System.Collections.Generic;
using System.Threading;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEditor;

/// <summary>The live <see cref="IPlacementSource"/> behind the viewport's authored placement layer
/// (<c>PropLayer.PlacementLayer(source, ...)</c>), so authored placements stream, cull and draw through the same
/// <see cref="Scene3DChunkSink"/> path as scatter instead of one whole-document draw list per frame.
/// <para>It serves the document's placements bucketed by chunk, minus the per-element hidden ones and minus the
/// selected one, which the viewport draws directly with the highlight tint. A drag moves the selected placement,
/// so a drag never changes what this source serves and never refreshes a chunk. The group gate and kit visibility
/// are NOT applied here: the sink's draw filter applies them per frame, so toggling them never refreshes a
/// chunk either. The viewport refreshes a changed chunk through <see cref="TerrainStreamer.RefreshPlacements"/>,
/// which republishes its props and leaves its terrain mesh alone.</para>
/// <para><see cref="Refresh(MapDocument, TerrainField, EditorVisibility, string, Action{ChunkCoord})"/> is
/// incremental. It diffs the current placement set against the one it last published, keyed by stable placement
/// id, republishes only the chunk buckets whose content changed, and reports exactly those chunks so the caller
/// invalidates them. It runs only when the document changed
/// (<see cref="Invalidate"/>), the selection changed, or <see cref="EditorVisibility.Version"/> moved, so an idle
/// frame costs three compares. A document or visibility change identifies the changed chunks with one pass over the
/// document's placements, the same pass the placement cache already pays per document change. A selection-only
/// change skips that pass: it moves just the old and new selected placements between the layer and the direct
/// highlight draw.</para>
/// <para>Threading: <see cref="PlacementsIn"/> reads one immutable bucket snapshot published with a volatile write,
/// which satisfies the build-thread contract. Every other member is frame-thread only.</para></summary>
internal sealed class AuthoredPlacementLayer : IPlacementSource
{
    readonly float _chunkSize;
    readonly PlacementCache _cache = new();

    // What PlacementsIn serves. Replaced wholesale on publish and never mutated after, so a build-thread read of
    // a stale reference still sees a coherent set.
    Dictionary<ChunkCoord, PropPlacement[]> _buckets = new();

    // The published set keyed by diff key, plus the scratch the next refresh fills. Swapped per refresh.
    Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    Dictionary<string, Entry> _next = new(StringComparer.Ordinal);
    readonly List<Entry> _included = new();
    readonly HashSet<string> _seenIds = new(StringComparer.Ordinal);
    readonly HashSet<ChunkCoord> _dirty = new();
    readonly Dictionary<ChunkCoord, List<PropPlacement>> _rebucket = new();

    // The placement list the published set was collected from, and its lazily built id-to-first-index map, which
    // the selection-only path reads.
    IReadOnlyList<EditorPlacement>? _collected;
    Dictionary<string, int>? _firstIndex;

    EditorVisibility? _visibility;
    string? _selectedId;
    EditorVisibility? _syncedVisibility;
    int _syncedVersion;
    string? _syncedSelectedId;
    bool _synced;

    readonly record struct Entry(PropPlacement Prop, ChunkCoord Coord);

    /// <summary>Creates an empty layer source bucketed at <paramref name="chunkSize"/>, which must match the sink's
    /// chunk size so a bucket is exactly one streamed chunk.</summary>
    internal AuthoredPlacementLayer(float chunkSize)
    {
        if (!(chunkSize > 0f)) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        _chunkSize = chunkSize;
    }

    /// <summary>The selected placement found by the last refresh that ran, or null when nothing is selected, the
    /// id is unmatched, or the placement is individually hidden. Kit visibility and the group gate are the
    /// caller's per-frame checks.</summary>
    internal EditorPlacement? Selected { get; private set; }

    /// <summary>True when the next refresh rebuilds the placement cache from the document.</summary>
    internal bool IsDirty => _cache.IsDirty;

    /// <summary>Marks the document side dirty. The editor scene routes <see cref="EditorDocument.DocumentChanged"/>
    /// here, and a field swap calls it so ground-snapped placements pick up the new height.</summary>
    internal void Invalidate() => _cache.Invalidate();

    /// <summary>Refreshes against the visibility and selection last passed to
    /// <see cref="Refresh(MapDocument, TerrainField, EditorVisibility, string, Action{ChunkCoord})"/>, for the
    /// world rebuild paths that run outside a draw.</summary>
    internal bool Refresh(MapDocument doc, TerrainField field, Action<ChunkCoord>? invalidate) =>
        RefreshCore(doc, field, _visibility, _selectedId, invalidate);

    /// <summary>Brings the served set in line with <paramref name="doc"/>, <paramref name="visibility"/> and
    /// <paramref name="selectedId"/>, calling <paramref name="invalidate"/> once for every chunk whose served
    /// content changed, after the new snapshot is published. A null <paramref name="invalidate"/> is for a
    /// caller that rebuilds every chunk anyway. Returns false without touching the document when nothing changed
    /// since the last refresh, or while the placement group is hidden (the refresh is deferred until it shows,
    /// so a hidden group never pays for cache rebuilds).</summary>
    internal bool Refresh(MapDocument doc, TerrainField field, EditorVisibility visibility, string? selectedId,
        Action<ChunkCoord>? invalidate)
    {
        ArgumentNullException.ThrowIfNull(visibility);
        _visibility = visibility;
        _selectedId = selectedId;
        return RefreshCore(doc, field, visibility, selectedId, invalidate);
    }

    /// <inheritdoc/>
    public void PlacementsIn(RectArea area, List<PropPlacement> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        Dictionary<ChunkCoord, PropPlacement[]> buckets = Volatile.Read(ref _buckets);
        if (buckets.Count == 0) return;
        ChunkCoord min = ChunkGrid.CoordOf(area.MinX, area.MinZ, _chunkSize);
        ChunkCoord max = ChunkGrid.CoordOf(area.MaxX, area.MaxZ, _chunkSize);
        long span = ((long)max.X - min.X + 1) * ((long)max.Z - min.Z + 1);
        if (span > buckets.Count)
        {
            // A query wider than the populated set walks the buckets instead of the coord range. The sink only
            // asks for single chunks, so this is the tooling path.
            foreach (PropPlacement[] bucket in buckets.Values) AppendInside(bucket, area, into);
            return;
        }
        for (int z = min.Z; z <= max.Z; z++)
        for (int x = min.X; x <= max.X; x++)
            if (buckets.TryGetValue(new ChunkCoord(x, z), out PropPlacement[]? bucket))
                AppendInside(bucket, area, into);
    }

    static void AppendInside(PropPlacement[] bucket, RectArea area, List<PropPlacement> into)
    {
        foreach (PropPlacement p in bucket)
            if (p.X >= area.MinX && p.X < area.MaxX && p.Z >= area.MinZ && p.Z < area.MaxZ)
                into.Add(p);
    }

    bool RefreshCore(MapDocument doc, TerrainField field, EditorVisibility? visibility, string? selectedId,
        Action<ChunkCoord>? invalidate)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(field);
        if (visibility is not null && !visibility.GetGroup(VisibilityGroup.Placements)) return false;
        bool sameInputs = _synced && !_cache.IsDirty
            && ReferenceEquals(visibility, _syncedVisibility)
            && (visibility is null || visibility.Version == _syncedVersion);
        bool sameSelection = string.Equals(selectedId, _syncedSelectedId, StringComparison.Ordinal);
        if (sameInputs && sameSelection) return false;

        if (sameInputs)
        {
            SwapSelection(visibility, _syncedSelectedId, selectedId);
        }
        else
        {
            _collected = _cache.Get(doc, field);
            _firstIndex = null;
            Collect(_collected, visibility, selectedId);
            Diff();
            if (_dirty.Count > 0) Publish();
            (_entries, _next) = (_next, _entries);
        }

        _synced = true;
        _syncedVisibility = visibility;
        _syncedVersion = visibility?.Version ?? 0;
        _syncedSelectedId = selectedId;

        if (invalidate is not null)
            foreach (ChunkCoord coord in _dirty) invalidate(coord);
        return true;
    }

    // The selection-only path: nothing but the selected id changed since the last refresh, so exactly two
    // placements can change membership. The old selection rejoins the layer unless it is hidden, and the new one
    // leaves it. Only their chunks' buckets are rewritten, so a selection click costs a dictionary copy and two
    // small arrays instead of a pass over the document. A first-occurrence id is its own diff key (see KeyFor), which
    // is what lets the published entries be edited by id here.
    void SwapSelection(EditorVisibility? visibility, string? oldId, string? newId)
    {
        _dirty.Clear();
        IReadOnlyList<EditorPlacement> placements = _collected!;
        Dictionary<string, int> firstIndex = _firstIndex ??= FirstIndexOf(placements);
        var buckets = new Dictionary<ChunkCoord, PropPlacement[]>(_buckets);
        if (oldId is not null && firstIndex.TryGetValue(oldId, out int oldIndex)
            && (visibility is null || !visibility.IsElementHidden(SelectionKind.Placement, oldId)))
        {
            PropPlacement prop = placements[oldIndex].Prop;
            var entry = new Entry(prop, ChunkGrid.CoordOf(prop.X, prop.Z, _chunkSize));
            _entries[oldId] = entry;
            buckets[entry.Coord] = buckets.TryGetValue(entry.Coord, out PropPlacement[]? bucket)
                ? [.. bucket, prop] : [prop];
            _dirty.Add(entry.Coord);
        }
        Selected = null;
        if (newId is not null && firstIndex.TryGetValue(newId, out int newIndex))
        {
            if (_entries.Remove(newId, out Entry left))
            {
                RemoveOne(buckets, left);
                _dirty.Add(left.Coord);
            }
            if (visibility is null || !visibility.IsElementHidden(SelectionKind.Placement, newId))
                Selected = placements[newIndex];
        }
        if (_dirty.Count > 0) Volatile.Write(ref _buckets, buckets);
    }

    static Dictionary<string, int> FirstIndexOf(IReadOnlyList<EditorPlacement> placements)
    {
        var index = new Dictionary<string, int>(placements.Count, StringComparer.Ordinal);
        for (int i = 0; i < placements.Count; i++) index.TryAdd(placements[i].Id, i);
        return index;
    }

    static void RemoveOne(Dictionary<ChunkCoord, PropPlacement[]> buckets, Entry entry)
    {
        if (!buckets.TryGetValue(entry.Coord, out PropPlacement[]? bucket)) return;
        int at = Array.FindIndex(bucket, p => SameTransform(p, entry.Prop));
        if (at < 0) return;
        if (bucket.Length == 1)
        {
            buckets.Remove(entry.Coord);
            return;
        }
        var trimmed = new PropPlacement[bucket.Length - 1];
        Array.Copy(bucket, 0, trimmed, 0, at);
        Array.Copy(bucket, at + 1, trimmed, at, bucket.Length - at - 1);
        buckets[entry.Coord] = trimmed;
    }

    // Fills _next and _included (document order) with every placement this source should serve, and records the
    // selected placement. Only the FIRST id match is the selected one, matching ViewportWorld.Partition, so a
    // duplicate id still draws. A duplicate id gets an occurrence-suffixed key, keeping the diff total.
    void Collect(IReadOnlyList<EditorPlacement> placements, EditorVisibility? visibility, string? selectedId)
    {
        _next.Clear();
        _included.Clear();
        _seenIds.Clear();
        Selected = null;
        bool selectedTaken = false;
        for (int i = 0; i < placements.Count; i++)
        {
            EditorPlacement placement = placements[i];
            string key = KeyFor(placement.Id);
            bool hidden = visibility is not null && visibility.IsElementHidden(SelectionKind.Placement, placement.Id);
            if (!selectedTaken && selectedId is not null
                && string.Equals(placement.Id, selectedId, StringComparison.Ordinal))
            {
                selectedTaken = true;
                if (!hidden) Selected = placement;
                continue;
            }
            if (hidden) continue;
            var entry = new Entry(placement.Prop, ChunkGrid.CoordOf(placement.Prop.X, placement.Prop.Z, _chunkSize));
            _next[key] = entry;
            _included.Add(entry);
        }
    }

    string KeyFor(string id)
    {
        if (_seenIds.Add(id)) return id;
        for (int n = 1; ; n++)
        {
            string key = id + "\u0000" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (_seenIds.Add(key)) return key;
        }
    }

    // Marks every chunk whose served content differs between _entries (published) and _next: an added
    // placement dirties its chunk, a removed one its old chunk, and a changed one both its old and new chunk.
    void Diff()
    {
        _dirty.Clear();
        foreach (KeyValuePair<string, Entry> kv in _next)
        {
            if (!_entries.TryGetValue(kv.Key, out Entry old))
            {
                _dirty.Add(kv.Value.Coord);
            }
            else if (!SameTransform(old.Prop, kv.Value.Prop))
            {
                _dirty.Add(old.Coord);
                _dirty.Add(kv.Value.Coord);
            }
        }
        foreach (KeyValuePair<string, Entry> kv in _entries)
            if (!_next.ContainsKey(kv.Key)) _dirty.Add(kv.Value.Coord);
    }

    // Republishes the dirty chunks' buckets from _included (document order) on a copy of the current snapshot.
    void Publish()
    {
        _rebucket.Clear();
        foreach (Entry entry in _included)
        {
            if (!_dirty.Contains(entry.Coord)) continue;
            if (!_rebucket.TryGetValue(entry.Coord, out List<PropPlacement>? list))
                _rebucket[entry.Coord] = list = new List<PropPlacement>();
            list.Add(entry.Prop);
        }
        var buckets = new Dictionary<ChunkCoord, PropPlacement[]>(_buckets);
        foreach (ChunkCoord coord in _dirty)
        {
            if (_rebucket.TryGetValue(coord, out List<PropPlacement>? list)) buckets[coord] = list.ToArray();
            else buckets.Remove(coord);
        }
        Volatile.Write(ref _buckets, buckets);
    }

    static bool SameTransform(in PropPlacement a, in PropPlacement b) =>
        string.Equals(a.Id, b.Id, StringComparison.Ordinal)
        && a.X.Equals(b.X) && a.Y.Equals(b.Y) && a.Z.Equals(b.Z)
        && a.Scale.Equals(b.Scale) && a.Yaw.Equals(b.Yaw) && a.Variant == b.Variant;
}
