using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEditor;

// The terrain-sculpt tool mode (EditorToolMode.SculptTerrain): a press-drag-release stroke paints the document's
// authored height deltas through the undoable command layer, one TerrainSculptStrokeCommand per stroke. The brush
// math is TerrainSculptBrush (GPU-free, headless-tested); this partial glues it to the picking, the document, and
// the history so the GUI/MCP surfaces stay thin.
public sealed partial class EditorToolController
{
    /// <summary>Default brush radius in world units.</summary>
    const float DefaultBrushRadius = 4f;
    /// <summary>Default brush strength (meters per stroke-second for raise/lower, per-second blend rate otherwise).</summary>
    const float DefaultBrushStrength = 3f;
    /// <summary>Smallest brush radius the tool accepts, so a zero radius never produces an empty stroke on drag.</summary>
    internal const float MinBrushRadius = 0.1f;

    // Stroke state, live only between the press and release of one sculpt gesture.
    bool _sculpting;
    float _flattenTarget;
    TerrainField? _sculptBase;

    /// <summary>The active sculpt brush (raise / lower / smooth / flatten / set-height). Read by
    /// <see cref="EditorToolMode.SculptTerrain"/> strokes and surfaced in the inspector.</summary>
    public SculptBrush Brush { get; set; } = SculptBrush.Raise;

    /// <summary>The sculpt brush radius in world units (clamped to <see cref="MinBrushRadius"/>).</summary>
    public float BrushRadius
    {
        get => _brushRadius;
        set => _brushRadius = MathF.Max(MinBrushRadius, value);
    }
    float _brushRadius = DefaultBrushRadius;

    /// <summary>The sculpt brush strength: meters per stroke-second for raise/lower, and a per-second blend rate
    /// (fraction toward the target per second, at the brush centre) for smooth/flatten/set-height.</summary>
    public float BrushStrength { get; set; } = DefaultBrushStrength;

    /// <summary>The absolute world height the <see cref="SculptBrush.SetHeight"/> brush blends the surface toward.</summary>
    public float SetHeight { get; set; }

    /// <summary>True while a sculpt stroke is in flight (press captured, release pending).</summary>
    public bool IsSculpting => _sculpting;

    // Press-drag-release: the press captures the analytic base (for flatten/set-height targets) and the composited
    // height under the cursor (the flatten target), then every held frame applies a dab. The stroke is sealed as a
    // fresh undo step on press so its dabs coalesce into one entry via TerrainSculptStrokeCommand.TryMerge, and
    // sealed again on release so the next stroke never merges into it. A dab that touches nothing (footprint off
    // bounds, or dt 0) executes no command, so an empty stroke lands no phantom undo entry.
    void UpdateSculpt(in EditorFrameInput input)
    {
        if (Field is null) return;

        if (input.PointerPressed)
        {
            if (!EditorPicking.PickTerrain(Field, input.RayOrigin, input.RayDirection, PickDistance, out Vector3 p)) return;
            _sculptBase = new TerrainField(MapRuntime.BuildTerrainConfig(_document.Doc, _document.Registry), null);
            _flattenTarget = Field.SampleHeight(p.X, p.Z);
            _document.SealGesture();
            _sculpting = true;
            ApplyDab(p, input.Dt);
            return;
        }

        if (!_sculpting) return;

        if (input.PointerReleased)
        {
            if (EditorPicking.PickTerrain(Field, input.RayOrigin, input.RayDirection, PickDistance, out Vector3 rel))
                ApplyDab(rel, input.Dt);
            _document.SealGesture();
            _sculpting = false;
            return;
        }

        if (input.PointerDown
            && EditorPicking.PickTerrain(Field, input.RayOrigin, input.RayDirection, PickDistance, out Vector3 hit))
            ApplyDab(hit, input.Dt);
    }

    // Applies one brush dab centred on the terrain hit `p` for a frame of `dt` seconds, as a merging stroke command.
    // Reads the live delta layer (0 outside every tile) and the stroke's captured analytic base, clamps the
    // footprint to the tiles that lie wholly within the document bounds (so no stored tile ever leaves bounds), and
    // executes the touched tiles' before/after grids through the document choke point. No-op when the dab touches
    // nothing.
    void ApplyDab(Vector3 p, float dt)
    {
        MapDocument doc = _document.Doc;
        MapTerrainOverrides? overrides = doc.TerrainOverrides;
        bool createdLayer = overrides is null;
        float cellSize = overrides?.CellSize ?? MapTerrainOverrides.DefaultCellSize;

        var bounds = SculptBounds.FromBounds(doc.Bounds.MinX, doc.Bounds.MinZ, doc.Bounds.MaxX, doc.Bounds.MaxZ, cellSize);
        if (!bounds.HasArea) return;

        Func<int, int, float> currentDelta = overrides is { } ov
            ? (cx, cz) => ov.GetDelta(cx, cz)
            : static (_, _) => 0f;
        TerrainField baseField = _sculptBase!;

        IReadOnlyList<TerrainSculptBrush.CellWrite> writes = TerrainSculptBrush.ComputeDab(
            Brush, p.X, p.Z, BrushRadius, BrushStrength, dt, SetHeight, _flattenTarget, cellSize, bounds,
            currentDelta, (x, z) => baseField.SampleHeight(x, z));
        if (writes.Count == 0) return;

        List<SculptTileDelta> tiles = TerrainSculptTiles.BuildTileDeltas(overrides, cellSize, writes, out RectArea dabBounds);
        _document.Execute(new TerrainSculptStrokeCommand(createdLayer, cellSize, tiles, dabBounds));
    }
}
