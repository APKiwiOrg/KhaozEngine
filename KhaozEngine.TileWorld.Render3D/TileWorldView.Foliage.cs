using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Terrain;

namespace KhaozEngine.TileWorld;

public sealed partial class TileWorldView
{
    /// <summary>How many generated cover instances are cached across loaded regions.</summary>
    public int GeneratedCoverCount { get; private set; }

    /// <summary>How many cover placements the last draw submitted after distance and quality thinning.</summary>
    public int LastDrawnCover { get; private set; }

    internal IReadOnlyList<GroundCoverInstance> CoverIn(RegionCoord region) =>
        _loaded.TryGetValue(region, out RegionHandles? handles) ? handles.Cover : Array.Empty<GroundCoverInstance>();

    IReadOnlyList<GroundCoverInstance> BuildCover(RegionCoord region)
    {
        if (_doc.FoliageLayers.Count == 0 || _doc.GetRegion(region) is null)
            return Array.Empty<GroundCoverInstance>();
        float minX = TileWorldSpace.WorldX(region.OriginX, _doc.TileSize);
        float maxX = TileWorldSpace.WorldX(region.OriginX + TileRegion.Size, _doc.TileSize);
        float minZ = TileWorldSpace.WorldZ(region.OriginZ + TileRegion.Size, _doc.TileSize);
        float maxZ = TileWorldSpace.WorldZ(region.OriginZ, _doc.TileSize);
        var area = new RectArea(minX, minZ, maxX, maxZ);
        var result = new List<GroundCoverInstance>();
        foreach (TileFoliageLayer layer in _doc.FoliageLayers)
        {
            var settings = new GroundCoverSettings
            {
                Seed = layer.Seed,
                Spacing = layer.Spacing,
                ScaleMin = layer.ScaleMin,
                ScaleMax = layer.ScaleMax,
                RootOffset = layer.RootOffset,
                Models = Models(layer.Archetypes),
            };
            var surface = new TileFoliageSurface(_doc, _catalogs, layer, _options.Mesher.SmoothNormals);
            result.AddRange(GroundCoverDistribution.Generate(area, settings, surface.Sample));
        }
        return new GroundCoverBatch(result);
    }

    static GroundCoverModel[] Models(IReadOnlyList<TileFoliageArchetype> archetypes)
    {
        var result = new GroundCoverModel[archetypes.Count];
        for (int i = 0; i < result.Length; i++)
            result[i] = new GroundCoverModel(archetypes[i].Id, archetypes[i].Weight);
        return result;
    }

    void RebuildCover(RegionCoord region, RegionHandles handles)
    {
        GeneratedCoverCount -= handles.Cover.Count;
        handles.Cover = BuildCover(region);
        GeneratedCoverCount += handles.Cover.Count;
    }

    void DrawCover(Vector3 focus)
    {
        int drawn = 0;
        foreach (RegionHandles handles in _loaded.Values)
            if (handles.Cover.Count > 0)
                drawn += _scene.DrawGroundCover(handles.Cover, _propMeshes, focus, _options.GroundCover);
        LastDrawnCover = drawn;
    }

    void QueueFoliageDependencies(TileRect changed, int plane, RegionCoord? excluded)
    {
        float clearance = 0f;
        foreach (TileFoliageLayer layer in _doc.FoliageLayers)
            if (layer.Plane == plane) clearance = MathF.Max(clearance, layer.DoorClearance);
        if (clearance > 0f) QueueLoadedWithin(changed, plane, clearance, excluded);
    }

    internal void MarkRegionStreamed(RegionCoord region)
    {
        var regionRect = new TileRect(region.OriginX, region.OriginZ, TileRegion.Size, TileRegion.Size);
        for (int plane = 0; plane < _planes; plane++)
            QueueBaseDependencies(regionRect, plane, region);

        TileRegion? data = _doc.GetRegion(region);
        if (data is null || _doc.FoliageLayers.Count == 0) return;
        foreach (TileObject obj in data.Objects)
        {
            TileObjectArchetype? archetype = _catalogs.Archetype(obj.ArchetypeId);
            if (archetype is null) continue;
            TileRect footprint = TileFootprint.Of(archetype, obj.X, obj.Z, obj.Rotation);
            foreach (TileFoliageLayer layer in _doc.FoliageLayers)
            {
                if (layer.ExcludeSolidObjects && obj.Plane == layer.Plane &&
                    archetype.CollisionKind == TileCollisionKind.Solid)
                    QueueLoadedWithin(footprint, layer.Plane, 0f, region);
                if (layer.ExcludeIndoors && archetype.IsRoof && obj.Plane > layer.Plane)
                    QueueLoadedWithin(footprint, layer.Plane, 0f, region);
                if (layer.DoorClearance > 0f && obj.Plane == layer.Plane && FoliageDoor(obj, archetype))
                    QueueLoadedWithin(footprint, layer.Plane, layer.DoorClearance, region);
            }
        }
    }

    void QueueLoadedWithin(TileRect changed, int plane, float reachMetres, RegionCoord? excluded)
    {
        double reachTiles = reachMetres / _doc.TileSize;
        double reachSquared = reachTiles * reachTiles;
        foreach (RegionCoord region in _loaded.Keys)
        {
            if (region == excluded) continue;
            double dx = Gap(changed.X, changed.X1, region.OriginX, region.OriginX + TileRegion.Size);
            double dz = Gap(changed.Z, changed.Z1, region.OriginZ, region.OriginZ + TileRegion.Size);
            if ((dx * dx) + (dz * dz) <= reachSquared) Queue(region, plane);
        }
    }

    static double Gap(int a0, int a1, int b0, int b1)
    {
        if (a1 < b0) return b0 - (double)a1;
        if (b1 < a0) return a0 - (double)b1;
        return 0d;
    }

    static bool FoliageDoor(TileObject obj, TileObjectArchetype archetype) =>
        FoliageTag(obj.Tags, "door") || FoliageTag(archetype.Tags, "door");

    static bool FoliageTag(IReadOnlyList<string>? tags, string wanted)
    {
        for (int i = 0; i < (tags?.Count ?? 0); i++)
            if (string.Equals(tags![i], wanted, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
