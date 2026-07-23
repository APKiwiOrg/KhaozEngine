using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEdit;

/// <summary>The <c>sculpt_*</c> MCP verbs (T3 of the terrain sculpt layer, #271): a single brush dab
/// (<see cref="SculptApply"/>), an exact region flatten (<see cref="SculptFlattenRegion"/>), and a tile clear
/// (<see cref="SculptClear"/>), all riding the T2 headless core (<see cref="TerrainSculptBrush"/>,
/// <see cref="TerrainSculptRegion"/>, <see cref="TerrainSculptTiles"/>) through the same command-backed
/// apply/validate/revert-on-error shape <see cref="FreezeZone"/> uses: check for work outside the mutation (so a
/// genuine no-op never marks the session dirty), then apply, validate, and revert-on-error inside one
/// world-affecting <see cref="MapEditSession.Mutate{T}"/> call.</summary>
public sealed partial class MutationService
{
    /// <summary>Applies one brush dab at a world point as a single-dab <see cref="TerrainSculptStrokeCommand"/>, so
    /// it lands as one undo step. <paramref name="brush"/> is one of raise/lower/smooth/flatten/set_height
    /// (case-insensitive). <paramref name="strength"/> and <paramref name="dt"/> feed
    /// <see cref="TerrainSculptBrush.ComputeDab"/> directly: meters per stroke-second (raise/lower) or a per-second
    /// blend rate (smooth/flatten/set_height), scaled by <paramref name="dt"/> seconds, so the call is
    /// deterministic regardless of wall-clock time. <paramref name="targetHeight"/> is required for the SetHeight
    /// brush (the absolute world height it blends toward) and ignored otherwise; Flatten instead captures its
    /// target live, from the field's current composited height at (<paramref name="x"/>, <paramref name="z"/>),
    /// the same capture-on-press semantics the interactive tool uses. A non-positive <paramref name="radius"/> or
    /// <paramref name="dt"/>, or a footprint that lands entirely outside the document's paintable sculpt bounds,
    /// is a clean no-op (<see cref="SculptApplyResult.Applied"/> false, nothing touched, session left
    /// clean): <see cref="TerrainSculptBrush.ComputeDab"/> already treats those as "nothing to paint" by
    /// design.</summary>
    public SculptApplyResult SculptApply(string brush, float x, float z, float radius, float strength, float dt,
        float? targetHeight = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brush);
        SculptBrush parsedBrush = ParseSculptBrush(brush);
        if (parsedBrush == SculptBrush.SetHeight && targetHeight is null)
            throw new ArgumentException("targetHeight is required for the SetHeight brush.", nameof(targetHeight));

        IReadOnlyList<TerrainSculptBrush.CellWrite> probe = session.WithDocument((doc, registry) =>
            PlanDab(doc, registry, parsedBrush, x, z, radius, strength, dt, targetHeight, out _, out _));
        if (probe.Count == 0)
            return new SculptApplyResult(brush, x, z, radius, 0, null, null, Applied: false);

        return session.Mutate((doc, registry) =>
        {
            IReadOnlyList<TerrainSculptBrush.CellWrite> writes = PlanDab(doc, registry, parsedBrush, x, z, radius,
                strength, dt, targetHeight, out MapTerrainOverrides? overrides, out float cellSize);
            if (writes.Count == 0) return new SculptApplyResult(brush, x, z, radius, 0, null, null, Applied: false);

            bool createdLayer = overrides is null;
            List<SculptTileDelta> tiles =
                TerrainSculptTiles.BuildTileDeltas(overrides, cellSize, writes, out RectArea dabBounds);
            var command = new TerrainSculptStrokeCommand(createdLayer, cellSize, tiles, dabBounds);
            command.Apply(doc);

            IReadOnlyList<string> errors = MapDocumentValidator.Validate(doc, registry);
            if (errors.Count > 0)
            {
                command.Revert(doc);
                throw new InvalidOperationException("mutation rejected: " + string.Join("; ", errors));
            }

            return new SculptApplyResult(brush, x, z, radius, writes.Count,
                writes.Min(w => w.Delta), writes.Max(w => w.Delta), Applied: true);
        }, worldChanged: true);
    }

    /// <summary>Flattens every sculpt cell whose centre falls inside the inclusive world rect
    /// [<paramref name="minX"/>..<paramref name="maxX"/>] x [<paramref name="minZ"/>..<paramref name="maxZ"/>] to
    /// <paramref name="targetHeight"/> in one command: a direct delta computation
    /// (<see cref="TerrainSculptRegion.ComputeFlattenRegion"/>) over the region's cells through the T1 authoring
    /// API, not repeated dabs, so the result is exact and deterministic (no falloff, no dt blending). Lands as one
    /// undo step, the same <see cref="TerrainSculptStrokeCommand"/> a brush dab uses. A degenerate or
    /// already-flat region is a clean no-op.</summary>
    public SculptFlattenRegionResult SculptFlattenRegion(float minX, float minZ, float maxX, float maxZ,
        float targetHeight)
    {
        IReadOnlyList<TerrainSculptBrush.CellWrite> probe = session.WithDocument((doc, registry) =>
            PlanRegionFlatten(doc, registry, minX, minZ, maxX, maxZ, targetHeight, out _, out _));
        if (probe.Count == 0)
            return new SculptFlattenRegionResult(minX, minZ, maxX, maxZ, targetHeight, 0, null, null, Applied: false);

        return session.Mutate((doc, registry) =>
        {
            IReadOnlyList<TerrainSculptBrush.CellWrite> writes = PlanRegionFlatten(doc, registry, minX, minZ, maxX,
                maxZ, targetHeight, out MapTerrainOverrides? overrides, out float cellSize);
            if (writes.Count == 0)
                return new SculptFlattenRegionResult(minX, minZ, maxX, maxZ, targetHeight, 0, null, null, Applied: false);

            bool createdLayer = overrides is null;
            List<SculptTileDelta> tiles =
                TerrainSculptTiles.BuildTileDeltas(overrides, cellSize, writes, out RectArea dabBounds);
            var command = new TerrainSculptStrokeCommand(createdLayer, cellSize, tiles, dabBounds);
            command.Apply(doc);

            IReadOnlyList<string> errors = MapDocumentValidator.Validate(doc, registry);
            if (errors.Count > 0)
            {
                command.Revert(doc);
                throw new InvalidOperationException("mutation rejected: " + string.Join("; ", errors));
            }

            return new SculptFlattenRegionResult(minX, minZ, maxX, maxZ, targetHeight, writes.Count,
                writes.Min(w => w.Delta), writes.Max(w => w.Delta), Applied: true);
        }, worldChanged: true);
    }

    /// <summary>Removes sculpt tiles, restoring the cells they covered to analytic terrain, in one undo step
    /// (<see cref="TerrainSculptClearCommand"/>). With every rect argument null, clears the whole sculpt layer.
    /// With all four supplied, clears only the tiles whose world extent intersects the inclusive rect. The four
    /// rect arguments must be supplied together or not at all. A document with no sculpt layer, or a region that
    /// touches no stored tile, is a clean no-op.</summary>
    public SculptClearResult SculptClear(float? minX = null, float? minZ = null, float? maxX = null, float? maxZ = null)
    {
        RequireWholeRectOrNone(minX, minZ, maxX, maxZ);

        bool hasWork = session.WithDocument((doc, _) =>
            doc.TerrainOverrides is { } ov && TerrainSculptRegion.SelectClearTiles(ov, minX, minZ, maxX, maxZ).Count > 0);
        if (!hasWork) return new SculptClearResult(0, Applied: false);

        return session.Mutate((doc, registry) =>
        {
            MapTerrainOverrides? overrides = doc.TerrainOverrides;
            IReadOnlyList<SculptTileClear> tiles = overrides is null
                ? Array.Empty<SculptTileClear>()
                : TerrainSculptRegion.SelectClearTiles(overrides, minX, minZ, maxX, maxZ);
            if (tiles.Count == 0) return new SculptClearResult(0, Applied: false);

            float cellSize = overrides!.CellSize;
            RectArea? dirty = minX is null
                ? null
                : new RectArea(minX.Value - cellSize, minZ!.Value - cellSize, maxX!.Value + cellSize, maxZ!.Value + cellSize);

            var command = new TerrainSculptClearCommand(cellSize, tiles, dirty);
            command.Apply(doc);

            IReadOnlyList<string> errors = MapDocumentValidator.Validate(doc, registry);
            if (errors.Count > 0)
            {
                command.Revert(doc);
                throw new InvalidOperationException("mutation rejected: " + string.Join("; ", errors));
            }

            return new SculptClearResult(tiles.Count, Applied: true);
        }, worldChanged: true);
    }

    // Computes one brush dab's writes against an already-locked document/registry: the layer's cell size and
    // paintable sculpt bounds, the live composited field for flatten's capture-at-point target (session.Field()
    // re-enters the session lock, safe since MapEditSession's lock is thread-reentrant), and a fresh analytic-only
    // field for the base height ComputeDab needs for flatten/set_height's world-space target. Hands back the
    // overrides layer read (null when the document has none yet) and the cell size, both of which a non-empty
    // result needs to build tiles.
    IReadOnlyList<TerrainSculptBrush.CellWrite> PlanDab(MapDocument doc, MapDocRegistry registry, SculptBrush brush,
        float x, float z, float radius, float strength, float dt, float? targetHeight,
        out MapTerrainOverrides? overrides, out float cellSize)
    {
        overrides = doc.TerrainOverrides;
        cellSize = overrides?.CellSize ?? MapTerrainOverrides.DefaultCellSize;
        var bounds = SculptBounds.FromBounds(doc.Bounds.MinX, doc.Bounds.MinZ, doc.Bounds.MaxX, doc.Bounds.MaxZ, cellSize);
        if (!bounds.HasArea) return Array.Empty<TerrainSculptBrush.CellWrite>();

        Func<int, int, float> currentDelta = overrides is { } ov ? (cx, cz) => ov.GetDelta(cx, cz) : static (_, _) => 0f;
        var baseField = new TerrainField(MapRuntime.BuildTerrainConfig(doc, registry), null);
        float flattenTarget = brush == SculptBrush.Flatten ? session.Field().SampleHeight(x, z) : 0f;

        return TerrainSculptBrush.ComputeDab(brush, x, z, radius, strength, dt, targetHeight ?? 0f, flattenTarget,
            cellSize, bounds, currentDelta, (wx, wz) => baseField.SampleHeight(wx, wz));
    }

    // Computes a region flatten's writes against an already-locked document/registry: same layer/bounds read as
    // PlanDab, then the exact (no-falloff) region computation instead of a brush dab.
    static IReadOnlyList<TerrainSculptBrush.CellWrite> PlanRegionFlatten(MapDocument doc, MapDocRegistry registry,
        float minX, float minZ, float maxX, float maxZ, float targetHeight,
        out MapTerrainOverrides? overrides, out float cellSize)
    {
        overrides = doc.TerrainOverrides;
        cellSize = overrides?.CellSize ?? MapTerrainOverrides.DefaultCellSize;
        var bounds = SculptBounds.FromBounds(doc.Bounds.MinX, doc.Bounds.MinZ, doc.Bounds.MaxX, doc.Bounds.MaxZ, cellSize);
        if (!bounds.HasArea) return Array.Empty<TerrainSculptBrush.CellWrite>();

        Func<int, int, float> currentDelta = overrides is { } ov ? (cx, cz) => ov.GetDelta(cx, cz) : static (_, _) => 0f;
        var baseField = new TerrainField(MapRuntime.BuildTerrainConfig(doc, registry), null);

        return TerrainSculptRegion.ComputeFlattenRegion(minX, minZ, maxX, maxZ, targetHeight, cellSize, bounds,
            currentDelta, (wx, wz) => baseField.SampleHeight(wx, wz));
    }

    // sculpt_clear's four rect arguments are all-or-nothing: a null minX is the sole "whole-layer clear" signal
    // TerrainSculptRegion.SelectClearTiles reads, so a partially-supplied rect would silently be misread as one.
    static void RequireWholeRectOrNone(float? minX, float? minZ, float? maxX, float? maxZ)
    {
        int given = (minX is null ? 0 : 1) + (minZ is null ? 0 : 1) + (maxX is null ? 0 : 1) + (maxZ is null ? 0 : 1);
        if (given != 0 && given != 4)
        {
            throw new ArgumentException(
                "sculpt_clear needs minX/minZ/maxX/maxZ supplied together (a region clear) or all left null (a whole-layer clear).");
        }
    }

    /// <summary>Parses a sculpt brush name (case-insensitive, underscore-insensitive so <c>set_height</c> matches
    /// <see cref="SculptBrush.SetHeight"/> the same way the tool description spells it) into a
    /// <see cref="SculptBrush"/>. Throws <see cref="ArgumentException"/> naming every valid value when
    /// <paramref name="brush"/> does not match one, the same convention <c>ParseBiome</c> uses for biome
    /// names.</summary>
    static SculptBrush ParseSculptBrush(string brush)
    {
        if (Enum.TryParse(brush.Replace("_", ""), ignoreCase: true, out SculptBrush parsed)) return parsed;
        throw new ArgumentException(
            $"brush '{brush}' is not a recognized SculptBrush. Valid values: raise, lower, smooth, flatten, set_height.",
            nameof(brush));
    }
}
