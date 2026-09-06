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
            var surface = new TileFoliageSurface(_doc, _catalogs, layer);
            result.AddRange(GroundCoverDistribution.Generate(area, settings, surface.Sample));
        }
        return result;
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
}
