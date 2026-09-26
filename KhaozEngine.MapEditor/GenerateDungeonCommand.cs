using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Dungeon;
using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEditor;

/// <summary>Bakes one deterministic dungeon into a map as a single reversible editor edit. The first apply stages
/// the whole bake before changing the target, and redo reuses that exact staged content.</summary>
public sealed class GenerateDungeonCommand : EditorCommand
{
    private readonly DungeonConfig _config;
    private readonly ulong _seed;
    private readonly DungeonKitMap _kit;
    private readonly DungeonPlotTransform _plot;
    private readonly string _spawnArchetypeId;
    private readonly MapTileRect? _loadedWindow;
    private BakePatch? _patch;
    private MapDocument? _target;
    private MapBounds? _previousBounds;
    private bool _applied;

    /// <summary>Creates one bake. A partially loaded tiled document requires <paramref name="loadedWindow"/> so
    /// generated content cannot cross into tiles the editor did not load.</summary>
    public GenerateDungeonCommand(DungeonConfig config, ulong seed, DungeonKitMap kit, DungeonPlotTransform plot,
        string spawnArchetypeId = "dungeon-spawn", MapTileRect? loadedWindow = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _kit = kit ?? throw new ArgumentNullException(nameof(kit));
        ArgumentException.ThrowIfNullOrWhiteSpace(spawnArchetypeId);
        ValidateFinite(plot);

        _config = DungeonJson.LoadConfig(DungeonJson.SaveConfig(config));
        _seed = seed;
        _plot = plot;
        _spawnArchetypeId = spawnArchetypeId;
        _loadedWindow = loadedWindow;
    }

    /// <inheritdoc/>
    public override string Label => "Generate dungeon";

    /// <inheritdoc/>
    internal override bool AffectsWorld => true;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (_applied) throw new InvalidOperationException("The dungeon bake is already applied.");
        if (_target is not null && !ReferenceEquals(_target, doc))
            throw new InvalidOperationException("A dungeon bake command cannot be reused on another document.");

        BakePatch patch = _patch ?? BuildPatch(doc);
        ValidateUniqueIds(doc, patch);
        AppendAtomically(doc, patch);
        _patch ??= patch;
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (!_applied || !ReferenceEquals(_target, doc) || _patch is null || _previousBounds is null)
            throw new InvalidOperationException("The dungeon bake is not applied to this document.");

        VerifyTail(doc.Placements, _patch.Placements);
        VerifyTail(doc.Spawns, _patch.Spawns);
        VerifyTail(doc.Regions, _patch.Regions);
        VerifyTail(doc.Terrain.Features, _patch.Features);

        RemoveTail(doc.Placements, _patch.Placements.Length);
        RemoveTail(doc.Spawns, _patch.Spawns.Length);
        RemoveTail(doc.Regions, _patch.Regions.Length);
        RemoveTail(doc.Terrain.Features, _patch.Features.Length);
        CopyBounds(_previousBounds, doc.Bounds);
        _applied = false;
    }

    private BakePatch BuildPatch(MapDocument doc)
    {
        if (doc.Bounds is null || doc.Terrain is null)
            throw new ArgumentException("The map needs bounds and terrain before a dungeon can be generated.", nameof(doc));
        if (doc.Tiles is { IsPartial: true } && _loadedWindow is null)
            throw new InvalidOperationException("A windowed map needs its loaded tile window to generate a dungeon.");

        DungeonLayout layout = DungeonGenerator.Generate(_config, _seed);
        ValidatePlotExtent(layout, doc.TileSize);

        var scratch = new MapDocument { Bounds = CloneBounds(doc.Bounds) };
        DungeonMapDocEmitter.Emit(layout, _kit, _plot, scratch, _spawnArchetypeId);
        if (!BoundsFinite(scratch.Bounds))
            throw new ArgumentException("The generated dungeon bounds are not finite.", nameof(doc));
        ValidateStagedGeometry(scratch);

        return new BakePatch(
            scratch.Placements.ToArray(), scratch.Spawns.ToArray(), scratch.Regions.ToArray(),
            scratch.Terrain.Features.ToArray(), CloneBounds(scratch.Bounds));
    }

    private void ValidatePlotExtent(DungeonLayout layout, float tileSize)
    {
        float width = layout.Width * layout.CellSizeMeters;
        float depth = layout.Depth * layout.CellSizeMeters;
        if (!float.IsFinite(width) || !float.IsFinite(depth) || width <= 0f || depth <= 0f)
            throw new ArgumentException("The generated dungeon plot has an invalid extent.", nameof(layout));

        float cos = MathF.Cos(_plot.YawRadians);
        float sin = MathF.Sin(_plot.YawRadians);
        Span<Vector2> local = stackalloc Vector2[] { Vector2.Zero, new(width, 0f), new(width, depth), new(0f, depth) };
        float minX = float.PositiveInfinity, minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxZ = float.NegativeInfinity;
        foreach (Vector2 corner in local)
        {
            float x = _plot.OriginX + corner.X * cos - corner.Y * sin;
            float z = _plot.OriginZ + corner.X * sin + corner.Y * cos;
            if (!float.IsFinite(x) || !float.IsFinite(z))
                throw new ArgumentException("The rotated dungeon plot has non-finite coordinates.", nameof(layout));
            minX = MathF.Min(minX, x); maxX = MathF.Max(maxX, x);
            minZ = MathF.Min(minZ, z); maxZ = MathF.Max(maxZ, z);
        }

        if (_loadedWindow is not MapTileRect window) return;
        if (!float.IsFinite(tileSize) || tileSize <= 0f)
            throw new ArgumentException("The map tile size must be positive and finite.", nameof(tileSize));
        float minTileX = MathF.Floor(minX / tileSize), minTileZ = MathF.Floor(minZ / tileSize);
        float maxTileX = MathF.Floor(maxX / tileSize), maxTileZ = MathF.Floor(maxZ / tileSize);
        if (minTileX < int.MinValue || minTileZ < int.MinValue ||
            maxTileX > int.MaxValue || maxTileZ > int.MaxValue ||
            !window.Contains(MapTileGrid.CoordOf(minX, minZ, tileSize)) ||
            !window.Contains(MapTileGrid.CoordOf(maxX, maxZ, tileSize)))
            throw new InvalidOperationException(
                "The dungeon plot crosses the loaded map window. Open the needed tiles or load the whole map first.");
    }

    private static void ValidateFinite(DungeonPlotTransform plot)
    {
        if (!float.IsFinite(plot.OriginX) || !float.IsFinite(plot.OriginZ) ||
            !float.IsFinite(plot.BaseY) || !float.IsFinite(plot.YawRadians))
            throw new ArgumentException("Dungeon plot coordinates and yaw must be finite.", nameof(plot));
    }

    private static bool BoundsFinite(MapBounds bounds) =>
        float.IsFinite(bounds.MinX) && float.IsFinite(bounds.MinZ) &&
        float.IsFinite(bounds.MaxX) && float.IsFinite(bounds.MaxZ);

    private static void ValidateStagedGeometry(MapDocument scratch)
    {
        foreach (MapPlacement placement in scratch.Placements)
            if (!float.IsFinite(placement.X) || !float.IsFinite(placement.Z) ||
                (placement.Y.HasValue && !float.IsFinite(placement.Y.Value)) ||
                !float.IsFinite(placement.Yaw) || !float.IsFinite(placement.Scale))
                throw new ArgumentException("A generated dungeon placement has non-finite geometry.");

        foreach (MapSpawn spawn in scratch.Spawns)
            if (!float.IsFinite(spawn.X) || !float.IsFinite(spawn.Z))
                throw new ArgumentException("A generated dungeon spawn has non-finite coordinates.");

        foreach (MapRegion region in scratch.Regions)
        {
            switch (region.Shape)
            {
                case DiscShapeDoc disc when float.IsFinite(disc.CenterX) && float.IsFinite(disc.CenterZ) &&
                                            float.IsFinite(disc.Radius):
                    break;
                case PolygonShapeDoc polygon when polygon.Points.All(static p =>
                    p.Length == 2 && float.IsFinite(p[0]) && float.IsFinite(p[1])):
                    break;
                default:
                    throw new ArgumentException("A generated dungeon region has invalid geometry.");
            }
        }

        foreach (MapFeature feature in scratch.Terrain.Features)
            if (feature is not FlattenFeatureDoc flatten ||
                !float.IsFinite(flatten.CenterX) || !float.IsFinite(flatten.CenterZ) ||
                !float.IsFinite(flatten.Radius) || !float.IsFinite(flatten.TargetHeight) ||
                !float.IsFinite(flatten.Blend))
                throw new ArgumentException("A generated dungeon terrain feature has non-finite geometry.");
    }

    private static void ValidateUniqueIds(MapDocument doc, BakePatch patch)
    {
        var placements = new HashSet<string>(doc.Placements.Select(static p => p.Id), StringComparer.Ordinal);
        foreach (MapPlacement placement in patch.Placements)
            if (!placements.Add(placement.Id))
                throw new InvalidOperationException($"Dungeon placement id '{placement.Id}' already exists.");

        var spawns = new HashSet<string>(doc.Spawns.Select(static s => s.Id), StringComparer.Ordinal);
        foreach (MapSpawn spawn in patch.Spawns)
            if (!spawns.Add(spawn.Id))
                throw new InvalidOperationException($"Dungeon spawn id '{spawn.Id}' already exists.");

        var regions = new HashSet<string>(doc.Regions.Select(static r => r.Name), StringComparer.Ordinal);
        foreach (MapRegion region in patch.Regions)
            if (!regions.Add(region.Name))
                throw new InvalidOperationException($"Dungeon region name '{region.Name}' already exists.");
    }

    private void AppendAtomically(MapDocument doc, BakePatch patch)
    {
        int placementCount = doc.Placements.Count;
        int spawnCount = doc.Spawns.Count;
        int regionCount = doc.Regions.Count;
        int featureCount = doc.Terrain.Features.Count;
        MapBounds previous = CloneBounds(doc.Bounds);
        try
        {
            doc.Placements.AddRange(patch.Placements);
            doc.Spawns.AddRange(patch.Spawns);
            doc.Regions.AddRange(patch.Regions);
            doc.Terrain.Features.AddRange(patch.Features);
            CopyBounds(patch.Bounds, doc.Bounds);
        }
        catch
        {
            doc.Placements.RemoveRange(placementCount, doc.Placements.Count - placementCount);
            doc.Spawns.RemoveRange(spawnCount, doc.Spawns.Count - spawnCount);
            doc.Regions.RemoveRange(regionCount, doc.Regions.Count - regionCount);
            doc.Terrain.Features.RemoveRange(featureCount, doc.Terrain.Features.Count - featureCount);
            CopyBounds(previous, doc.Bounds);
            throw;
        }
        _previousBounds = previous;
        _target = doc;
        _applied = true;
    }

    private static void VerifyTail<T>(List<T> list, T[] expected) where T : class
    {
        if (list.Count < expected.Length)
            throw new InvalidOperationException("The dungeon bake no longer occupies the document tail.");
        int start = list.Count - expected.Length;
        for (int i = 0; i < expected.Length; i++)
            if (!ReferenceEquals(list[start + i], expected[i]))
                throw new InvalidOperationException("The dungeon bake no longer occupies the document tail.");
    }

    private static void RemoveTail<T>(List<T> list, int count) => list.RemoveRange(list.Count - count, count);

    private static MapBounds CloneBounds(MapBounds bounds) => new()
    {
        MinX = bounds.MinX, MinZ = bounds.MinZ, MaxX = bounds.MaxX, MaxZ = bounds.MaxZ,
    };

    private static void CopyBounds(MapBounds source, MapBounds target)
    {
        target.MinX = source.MinX;
        target.MinZ = source.MinZ;
        target.MaxX = source.MaxX;
        target.MaxZ = source.MaxZ;
    }

    private sealed record BakePatch(MapPlacement[] Placements, MapSpawn[] Spawns, MapRegion[] Regions,
        MapFeature[] Features, MapBounds Bounds);
}
