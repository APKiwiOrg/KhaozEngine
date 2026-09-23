using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEditor;

/// <summary>The in-place refresh seams: the edits that keep the sink, streamer, ring, kit meshes and splat material and
/// rebuild only what changed. Cheapest first: <see cref="RefreshLayerProps(MapDocument, RectArea)"/> re-serves the
/// props of the chunks an exclusion or scatter-override edit covers, <see cref="PartialRebuild(MapDocument,
/// MapDocRegistry, RectArea, bool)"/> re-meshes the chunks a terrain edit covers, and <see cref="RefreshLoaded"/>
/// re-meshes every loaded chunk. Each returns false, touching nothing, when it cannot serve the edit, so the caller
/// falls back to the next one and finally to <see cref="Rebuild"/>.</summary>
public sealed partial class ViewportWorld
{
    /// <summary>The live streamer, or null before <see cref="Build"/>. Internal so a device test can read its build
    /// counters.</summary>
    internal TerrainStreamer? Streamer => _streamer;

    /// <summary>The props-only partial path, for a bounded edit that changes captured scatter or companion configs
    /// and leaves the terrain field alone (every exclusion and scatter-override command, see
    /// <see cref="EditorDocument.PendingFieldChange"/>). It hands the sink the document's rebuilt layer list and
    /// re-serves every prop layer of the loaded chunks overlapping <paramref name="dirty"/>
    /// (<see cref="TerrainStreamer.RefreshProps"/>). The field is not rebuilt, no terrain is re-meshed and the
    /// authored placements are not re-snapped, so a drag frame costs the scatter work of the chunks it covers.
    /// <para>Correct only while the field is unchanged and <paramref name="dirty"/> covers every chunk whose
    /// placements the edit can change. The commands guarantee the second by padding their shape bounds with the
    /// document's largest scatter jitter (<see cref="ShapeGeometry.BoundsMarginFor"/>), since a candidate belongs
    /// to the chunk of its un-jittered cell centre while the shape test reads its jittered position. Returns false,
    /// touching nothing, when the world is not built or when the new layer list does not keep the sink's layer
    /// shape (<see cref="Scene3DChunkSink.KeepsLayerShape"/>), so the caller falls back to the re-meshing paths.
    /// Throws <see cref="ObjectDisposedException"/> after <see cref="Dispose"/>.</para></summary>
    public bool RefreshLayerProps(MapDocument doc, RectArea dirty)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(doc);
        if (!_built) return false;
        if (!RefreshLayerProps(_sink!, _streamer!, BuildSinkLayers(doc), dirty)) return false;
        _doc = doc;
        return true;
    }

    /// <summary>The props-only step on an explicit sink and streamer: flush, check the layer shape, swap the layers,
    /// re-serve the chunks <paramref name="dirty"/> overlaps. Shared by
    /// <see cref="RefreshLayerProps(MapDocument, RectArea)"/> and the headless tests, which drive it over a
    /// device-free streamer.</summary>
    internal static bool RefreshLayerProps(Scene3DChunkSink sink, TerrainStreamer streamer,
        IReadOnlyList<PropLayer> layers, RectArea dirty)
    {
        streamer.FlushPendingBuilds();
        if (!sink.KeepsLayerShape(layers)) return false;
        sink.UpdateLayers(layers);
        streamer.RefreshProps(dirty);
        return true;
    }

    /// <summary>Rebuilds ONLY the loaded chunks overlapping <paramref name="dirty"/> after a localized terrain edit,
    /// instead of the whole streamed world: it swaps in the new field, tells the sink to sample from it, and asks the
    /// streamer to re-mesh just the chunks the dirty rect touches (<see cref="TerrainStreamer.Invalidate(RectArea)"/>).
    /// Nothing is torn down (not the sink, streamer, ring, kit meshes, nor the splat material), so this is far cheaper
    /// than <see cref="Rebuild"/>. The placement cache is invalidated so authored placements re-ground-snap to the new
    /// field. The water plane needs nothing here: <see cref="Draw"/> derives it live from the document each frame.
    /// <para>Returns false (a no-op) when the world is not built, so the caller can fall back to a full
    /// <see cref="Rebuild"/> or skip. Throws <see cref="ObjectDisposedException"/> after <see cref="Dispose"/>, like
    /// its siblings.</para>
    /// <para>The four-argument overload can refresh captured scatter and companion configs before invalidation,
    /// for a bounded batch that mixes a field change with an exclusion or scatter-override edit. It also returns
    /// false, touching nothing, when the refreshed layer list would not keep the sink's layer shape
    /// (<see cref="Scene3DChunkSink.KeepsLayerShape"/>): a layer-count or companion-host change needs
    /// <see cref="Rebuild"/>.</para></summary>
    public bool PartialRebuild(MapDocument doc, MapDocRegistry registry, RectArea dirty) =>
        PartialRebuild(doc, registry, dirty, refreshLayers: false);

    /// <summary>Partial rebuild with an optional refresh of captured scatter and companion configs.</summary>
    public bool PartialRebuild(MapDocument doc, MapDocRegistry registry, RectArea dirty, bool refreshLayers)
    {
        ThrowIfDisposed();
        if (!_built) return false;
        TerrainField field = MapRuntime.BuildField(doc, registry);
        IReadOnlyList<PropLayer>? layers = refreshLayers ? BuildSinkLayers(doc) : null;
        if (layers is not null && !_sink!.KeepsLayerShape(layers)) return false;
        _streamer!.FlushPendingBuilds();
        if (layers is not null) _sink!.UpdateLayers(layers);
        _field = field;
        _doc = doc;
        _sink!.UpdateField(field);         // future chunk builds sample the new field
        // Authored placements re-ground-snap to the new field BEFORE the re-mesh, so the dirty chunks rebuild once
        // with the new snapshot. A placement chunk outside the dirty rect keeps its terrain and gets a props-only
        // refresh.
        _authored.Invalidate();
        ChunkCoord min = ChunkGrid.CoordOf(dirty.MinX, dirty.MinZ, _streamer.Config.ChunkSize);
        ChunkCoord max = ChunkGrid.CoordOf(dirty.MaxX, dirty.MaxZ, _streamer.Config.ChunkSize);
        _authored.Refresh(doc, field, coord =>
        {
            if (coord.X < min.X || coord.X > max.X || coord.Z < min.Z || coord.Z > max.Z) RefreshChunkPlacements(coord);
        });
        _streamer!.Invalidate(dirty);      // re-mesh the loaded chunks the dirty rect overlaps, in place
        return true;
    }

    /// <summary>Refreshes the field and optional captured generation config, then invalidates every loaded chunk
    /// in place. A companion may change its host here, since no loaded chunk keeps state from the old list.
    /// Returns false before the first build.</summary>
    public bool RefreshLoaded(MapDocument doc, MapDocRegistry registry, bool refreshLayers)
    {
        ThrowIfDisposed();
        if (!_built) return false;
        TerrainField field = MapRuntime.BuildField(doc, registry);
        IReadOnlyList<PropLayer>? layers = refreshLayers ? BuildSinkLayers(doc) : null;
        _streamer!.FlushPendingBuilds();
        if (layers is not null) _sink!.UpdateLayers(layers);
        _sink!.UpdateField(field);
        _field = field;
        _doc = doc;
        _authored.Invalidate();
        _authored.Refresh(doc, field, invalidate: null);   // every loaded chunk rebuilds just below
        _streamer.InvalidateAll();
        return true;
    }
}
