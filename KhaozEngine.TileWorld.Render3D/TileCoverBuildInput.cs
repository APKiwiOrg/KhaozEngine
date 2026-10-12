using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Terrain;

namespace KhaozEngine.TileWorld;

/// <summary>Exclusively owned cover inputs. Workers never read the source document, catalog or index.</summary>
internal sealed record TileCoverBuildInput : TileProfileBuildInput
{
    readonly TileWorldDocument _document;
    readonly TileWorldCatalogs _catalogs;
    readonly TileFoliageLayer[] _layers;
    readonly TileCoverObject[] _exclusions;
    readonly RegionCoord _region;
    readonly bool _smoothNormals;

    TileCoverBuildInput(TileWorldDocument document, TileWorldCatalogs catalogs, TileFoliageLayer[] layers,
        TileCoverObject[] exclusions, RegionCoord region, bool smoothNormals)
    {
        _document = document;
        _catalogs = catalogs;
        _layers = layers;
        _exclusions = exclusions;
        _region = region;
        _smoothNormals = smoothNormals;
    }

    internal static TileCoverBuildInput Capture(TileWorldDocument document, TileWorldCatalogs catalogs,
        RegionCoord region, bool smoothNormals, TileCoverObjectIndex objects)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentNullException.ThrowIfNull(objects);
        TileFoliageLayer[] layers = document.FoliageLayers.ToArray();
        return new TileCoverBuildInput(TileWorldBuildSnapshot.CaptureGround(document, region),
            TileWorldBuildSnapshot.CaptureMaterials(catalogs), layers, objects.Query(region, layers).ToArray(),
            region, smoothNormals);
    }

    internal IReadOnlyList<GroundCoverInstance> Build() => Generate(_document, _catalogs, _region,
        _smoothNormals, _layers, _exclusions);

    internal static IReadOnlyList<GroundCoverInstance> Generate(TileWorldDocument document,
        TileWorldCatalogs catalogs, RegionCoord region, bool smoothNormals,
        IReadOnlyList<TileFoliageLayer> layers, IReadOnlyList<TileCoverObject>? exclusions = null)
    {
        if (layers.Count == 0 || document.GetRegion(region) is null)
            return Array.Empty<GroundCoverInstance>();
        float minX = TileWorldSpace.WorldX(region.OriginX, document.TileSize);
        float maxX = TileWorldSpace.WorldX(region.OriginX + TileRegion.Size, document.TileSize);
        float minZ = TileWorldSpace.WorldZ(region.OriginZ + TileRegion.Size, document.TileSize);
        float maxZ = TileWorldSpace.WorldZ(region.OriginZ, document.TileSize);
        var area = new RectArea(minX, minZ, maxX, maxZ);
        var result = new List<GroundCoverInstance>();
        foreach (TileFoliageLayer layer in layers)
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
            var surface = exclusions is null
                ? new TileFoliageSurface(document, catalogs, layer, smoothNormals)
                : new TileFoliageSurface(document, catalogs, layer, smoothNormals, exclusions, region.Rect.Expand(1));
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
}
