using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEditor;

/// <summary>One tile a clear removes: its coordinate and the delta grid it carried before the clear, captured for
/// undo. Mirrors <see cref="SculptTileDelta"/>'s before/after split, but a clear has only a "before" (the tile is
/// gone afterward, not rewritten).</summary>
public readonly struct SculptTileClear
{
    /// <summary>Tile X index.</summary>
    public int TileX { get; }
    /// <summary>Tile Z index.</summary>
    public int TileZ { get; }
    /// <summary>The tile's delta grid before the clear, restored by <see cref="TerrainSculptClearCommand.Revert"/>.</summary>
    public float[] Prior { get; }

    /// <summary>Captures a tile's pre-clear grid.</summary>
    public SculptTileClear(int tileX, int tileZ, float[] prior)
    {
        TileX = tileX;
        TileZ = tileZ;
        Prior = prior ?? throw new ArgumentNullException(nameof(prior));
    }
}

/// <summary>One undoable clear of the document's sculpt tiles (T3, #271): drops the tiles
/// <see cref="TerrainSculptRegion.SelectClearTiles"/> selected out of <see cref="MapDocument.TerrainOverrides"/>,
/// restoring the cells they covered to analytic terrain. <see cref="Apply"/> removes each tile and drops the whole
/// layer back to null once it empties (byte-identical to a document that was never sculpted there, the same
/// null-when-empty convention <see cref="TerrainSculptStrokeCommand"/> uses for a layer it created).
/// <see cref="Revert"/> restores each tile's captured prior grid, recreating the layer if <see cref="Apply"/>
/// nulled it. Affects the streamed world.</summary>
public sealed class TerrainSculptClearCommand : EditorCommand
{
    readonly float _cellSize;
    readonly IReadOnlyList<SculptTileClear> _tiles;
    readonly RectArea? _dirty;

    /// <summary>Creates the clear command from the tiles it will remove (<paramref name="tiles"/>, from
    /// <see cref="TerrainSculptRegion.SelectClearTiles"/>), the layer's <paramref name="cellSize"/> (needed to
    /// recreate the layer on <see cref="Revert"/> if the clear empties it), and the world-space
    /// <paramref name="dirty"/> region the clear invalidates (null for a whole-layer clear, whose reach is the
    /// entire streamed world).</summary>
    public TerrainSculptClearCommand(float cellSize, IReadOnlyList<SculptTileClear> tiles, RectArea? dirty)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        _cellSize = cellSize;
        _tiles = tiles;
        _dirty = dirty;
    }

    /// <inheritdoc/>
    public override string Label => "Clear sculpt terrain";

    internal override bool AffectsWorld => true;

    /// <inheritdoc/>
    internal override RectArea? DirtyRegion => _dirty;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        MapTerrainOverrides? overrides = doc.TerrainOverrides;
        if (overrides is null) return;
        foreach (SculptTileClear t in _tiles) overrides.RemoveTile(t.TileX, t.TileZ);
        if (overrides.IsEmpty) doc.TerrainOverrides = null;
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        MapTerrainOverrides overrides = doc.TerrainOverrides ??= new MapTerrainOverrides(_cellSize);
        foreach (SculptTileClear t in _tiles)
            overrides.PutTile(new MapSculptTile(t.TileX, t.TileZ, (float[])t.Prior.Clone()));
    }
}
