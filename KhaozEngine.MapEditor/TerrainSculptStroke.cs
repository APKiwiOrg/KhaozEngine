using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEditor;

/// <summary>One sculpt tile's before/after state for a stroke: the delta grid as it was before the stroke first
/// touched the tile (<see cref="Prior"/>, null when the stroke created the tile, so undo removes it) and after the
/// stroke (<see cref="Final"/>). Both grids are <see cref="TerrainSculpt.TileSize"/> squared, row-major meters.</summary>
public readonly struct SculptTileDelta
{
    /// <summary>Tile X index.</summary>
    public int TileX { get; }
    /// <summary>Tile Z index.</summary>
    public int TileZ { get; }
    /// <summary>The tile's delta grid before the stroke, or null when the stroke created the tile.</summary>
    public float[]? Prior { get; }
    /// <summary>The tile's delta grid after the stroke.</summary>
    public float[] Final { get; }

    /// <summary>Captures a tile's before/after grids. <paramref name="final"/> is required;
    /// <paramref name="prior"/> is null for a tile the stroke created.</summary>
    public SculptTileDelta(int tileX, int tileZ, float[]? prior, float[] final)
    {
        TileX = tileX;
        TileZ = tileZ;
        Prior = prior;
        Final = final ?? throw new ArgumentNullException(nameof(final));
    }
}

/// <summary>One undoable terrain-sculpt stroke: the whole press-drag-release gesture collapsed into a single undo
/// step (T2 of the sculpt program, #271). Each frame's brush dab is executed as a fresh stroke command that the
/// on-stack one absorbs through <see cref="TryMerge"/>, so a stroke lands one history entry no matter how many dabs
/// it spans, exactly like the transform-gizmo drag coalescing.
///
/// <para>The command tracks, per touched tile, the delta grid as it was before the stroke first reached that tile
/// (kept from the earliest dab that touched it, so it is the true pre-stroke state even for a tile several dabs
/// revisit) and the grid the stroke leaves. <see cref="Apply"/> writes the final grids (creating the sculpt layer
/// if the stroke created it), and <see cref="Revert"/> restores each prior grid or removes a tile the stroke
/// created, so undo returns the document exactly to its pre-stroke state (an empty created layer is dropped back to
/// null, byte-identical to no sculpting). Affects the streamed world, and reports a bounded
/// <see cref="DirtyRegion"/> covering the stroke's footprint so the viewport re-meshes only the chunks the stroke
/// touched (the dirty-region path, not a full rebuild).</para></summary>
public sealed class TerrainSculptStrokeCommand : EditorCommand
{
    readonly bool _createdLayer;
    readonly float _cellSize;
    readonly Dictionary<long, SculptTileDelta> _tiles = new();
    RectArea _dirty;

    /// <summary>Creates the stroke command for one dab. <paramref name="createdLayer"/> is true when this dab was
    /// the first sculpt on a document with no <see cref="MapDocument.TerrainOverrides"/> layer yet (so undo drops
    /// the layer back to null once empty). <paramref name="cellSize"/> is the layer's sculpt cell size,
    /// <paramref name="tiles"/> the dab's touched tiles with their captured before/after grids, and
    /// <paramref name="dabBounds"/> the world-space footprint of this dab (unioned across the stroke for the dirty
    /// region).</summary>
    public TerrainSculptStrokeCommand(bool createdLayer, float cellSize,
        IReadOnlyList<SculptTileDelta> tiles, RectArea dabBounds)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        _createdLayer = createdLayer;
        _cellSize = cellSize;
        for (int i = 0; i < tiles.Count; i++)
        {
            SculptTileDelta t = tiles[i];
            _tiles[Key(t.TileX, t.TileZ)] = t;
        }
        _dirty = dabBounds;
    }

    /// <inheritdoc/>
    public override string Label => "Sculpt terrain";

    internal override bool AffectsWorld => true;

    /// <inheritdoc/>
    /// <remarks>The stroke's whole footprint, unioned across every merged dab, so undo/redo re-mesh exactly the
    /// chunks the stroke touched (and no more).</remarks>
    internal override RectArea? DirtyRegion => _dirty;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        MapTerrainOverrides overrides = doc.TerrainOverrides ??= new MapTerrainOverrides(_cellSize);
        foreach (SculptTileDelta t in _tiles.Values)
            overrides.PutTile(new MapSculptTile(t.TileX, t.TileZ, (float[])t.Final.Clone()));
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        MapTerrainOverrides? overrides = doc.TerrainOverrides;
        if (overrides is null) return;
        foreach (SculptTileDelta t in _tiles.Values)
        {
            if (t.Prior is { } prior) overrides.PutTile(new MapSculptTile(t.TileX, t.TileZ, (float[])prior.Clone()));
            else overrides.RemoveTile(t.TileX, t.TileZ);
        }
        // A layer this stroke created returns to null once its last stroke's tiles are gone, so an undone
        // first-stroke document deep-equals its pre-sculpt self (absent block, not a present-but-empty one).
        if (_createdLayer && overrides.IsEmpty) doc.TerrainOverrides = null;
    }

    /// <inheritdoc/>
    public override bool TryMerge(IEditorCommand next)
    {
        if (next is not TerrainSculptStrokeCommand dab) return false;
        foreach (SculptTileDelta t in dab._tiles.Values)
        {
            long key = Key(t.TileX, t.TileZ);
            // Keep the earliest prior (the true pre-stroke grid for a tile earlier dabs already touched) and take
            // the latest final. A tile this dab reaches first carries its own true pre-stroke prior.
            _tiles[key] = _tiles.TryGetValue(key, out SculptTileDelta existing)
                ? new SculptTileDelta(t.TileX, t.TileZ, existing.Prior, t.Final)
                : t;
        }
        _dirty = FeatureGeometry.Union(_dirty, dab._dirty);
        return true;
    }

    static long Key(int tileX, int tileZ) => ((long)tileX << 32) | (uint)tileZ;
}
