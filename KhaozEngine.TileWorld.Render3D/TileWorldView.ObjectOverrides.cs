using System;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld;

/// <summary>
/// The per-object archetype override: drawing ONE placed object as a different archetype than the one it was
/// authored as, without touching the document and without rebuilding a region-plane's ground mesh.
/// <para>Before this the only way to draw a chopped tree as a stump was to mutate the client's own
/// <c>TileWorldDocument</c>, mark the tile dirty and flush, which remeshes and re-uploads the whole
/// region-plane's ground for a change that moved no vertex. Two things were wrong with that and only one of them
/// is the cost: a client that edits its own copy of the world document has a world that no longer matches the
/// server's, and every later question asked of it (a pick, a reach test, a save) is asked of the edit.</para>
/// <para>An override is a VIEW fact. The document is untouched, so collision, pathing and the server's own copy
/// are all unaffected, which is what makes this safe to drive straight off
/// <c>TileWorldClient.ObjectStateChanged</c>. It is not woodcutting-shaped either: a damage state, a seasonal or
/// day-night look and an editor's preview of a swap all want the same seam.</para>
/// </summary>
public sealed partial class TileWorldView
{
    readonly Dictionary<long, string> _archetypeOverrides = new();
    // The lookup handed to TileObjectProps.Build, built once. A method group converted at each call site would
    // allocate a delegate per region-plane per load, and a region load builds one per plane.
    Func<long, string?>? _overrideLookup;

    /// <summary>How many objects this view is drawing as something other than their authored archetype.</summary>
    public int ArchetypeOverrideCount => _archetypeOverrides.Count;

    /// <summary>
    /// Draws one placed object as <paramref name="archetypeId"/> from now on, replacing any override it already
    /// had. The document is not touched.
    /// <para>Rebuilds only that object's placement when it can (see
    /// <see cref="TileObjectProps.TryReplaceObject"/>), and falls back to rebuilding the region-plane's PROP
    /// LIST when the swap changes whether the object is a roof, or when the object was not being drawn at all.
    /// Neither path remeshes or re-uploads the ground, which is what a <see cref="MarkDirty(RegionCoord, int)"/>
    /// plus <see cref="Flush()"/> would have cost for a change that moves no vertex.</para>
    /// <para>An id no loaded region holds is RECORDED and draws nothing this frame, the
    /// <see cref="SetSilhouettedObject"/> contract: the override applies by itself when the region streams in,
    /// so a caller may set one optimistically from a server message that arrived before the region did.</para>
    /// </summary>
    /// <param name="objectId">The object's document id.</param>
    /// <param name="archetypeId">The archetype to draw it as. One the catalogs do not hold draws NOTHING for
    /// that object, which is the same answer an object whose authored archetype is missing already gets.</param>
    /// <returns>True when a drawn placement was rewritten, false when the override was recorded and there was
    /// nothing loaded to apply it to yet.</returns>
    public bool OverrideArchetype(long objectId, string archetypeId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(archetypeId);
        if (_archetypeOverrides.TryGetValue(objectId, out string? already) && already == archetypeId) return false;
        _archetypeOverrides[objectId] = archetypeId;
        return RebuildObjectProp(objectId);
    }

    /// <summary>Puts one object back to the archetype the document authored it as. Safe for an object that has
    /// no override.</summary>
    /// <param name="objectId">The object's document id.</param>
    /// <returns>True when a drawn placement was rewritten, false when the object had no override or nothing
    /// loaded was drawing it.</returns>
    public bool ClearOverride(long objectId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_archetypeOverrides.Remove(objectId)) return false;
        return RebuildObjectProp(objectId);
    }

    /// <summary>Drops every override at once and rebuilds the prop list of every loaded region-plane, which is
    /// what a head does on a disconnect or a world change rather than walking its own list of them. Cheap when
    /// there were none: it does nothing at all.</summary>
    public void ClearOverrides()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_archetypeOverrides.Count == 0) return;
        _archetypeOverrides.Clear();
        RebuildAllProps();
    }

    /// <summary>The archetype one object is being drawn as, when it is overridden at all.</summary>
    /// <param name="objectId">The object's document id.</param>
    /// <param name="archetypeId">The override, when the answer is true.</param>
    /// <returns>False when the object draws as the document authored it.</returns>
    public bool TryGetOverride(long objectId, out string archetypeId)
    {
        if (_archetypeOverrides.TryGetValue(objectId, out string? found)) { archetypeId = found; return true; }
        archetypeId = string.Empty;
        return false;
    }

    // The archetype one object DRAWS as: its override, or the one the document authored. The single answer to
    // that question, so the prop build, the narrow splice and the silhouette cannot disagree about which mesh an
    // object is wearing, which they would the moment two of them read the document directly.
    internal string ArchetypeFor(TileObject o)
        => _archetypeOverrides.TryGetValue(o.Id, out string? over) ? over : o.ArchetypeId;

    // Null while nothing is overridden, so an untouched world pays no delegate call per object. Build treats a
    // null lookup and a null answer identically.
    Func<long, string?>? OverrideLookup()
    {
        if (_archetypeOverrides.Count == 0) return null;
        return _overrideLookup ??= id => _archetypeOverrides.TryGetValue(id, out string? over) ? over : null;
    }

    // One object's placement, rewritten in the region-plane it lives in. False when nothing loaded is drawing
    // it, which covers an unknown id, an unloaded region and a plane the document does not have: the override is
    // already recorded by then, so a later LoadRegion picks it up through OverrideLookup.
    bool RebuildObjectProp(long objectId)
    {
        if (_doc.FindObject(objectId) is not { } o) return false;
        if (o.Plane < 0 || o.Plane >= _planes) return false;
        RegionCoord region = RegionCoord.Of(o.X, o.Z);
        if (!_loaded.TryGetValue(region, out RegionHandles? handles)) return false;

        TileRegionProps? spliced =
            TileObjectProps.TryReplaceObject(_doc, _catalogs, handles.Props[o.Plane], o, ArchetypeFor(o));
        // The region-plane's PROPS alone, never its mesh: an archetype swap changes no ground vertex, so the
        // fallback is still far cheaper than the MarkDirty plus Flush a head would otherwise have written.
        handles.Props[o.Plane] = spliced
            ?? TileObjectProps.Build(_doc, _catalogs, region, o.Plane, OverrideLookup());
        return true;
    }

    // Every loaded region-plane's props, for the wholesale clear. The mesh is untouched here too.
    void RebuildAllProps()
    {
        Func<long, string?>? lookup = OverrideLookup();
        foreach (KeyValuePair<RegionCoord, RegionHandles> entry in _loaded)
            for (int plane = 0; plane < _planes; plane++)
                entry.Value.Props[plane] = TileObjectProps.Build(_doc, _catalogs, entry.Key, plane, lookup);
    }
}
