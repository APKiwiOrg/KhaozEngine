using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.TileWorld;

namespace KhaozEngine.Tests.MapDocCompatibility;

internal sealed record LegacyWaterSource(string BodyId, RegionCoord Region, int Plane, int Index, TileWaterBody Body);

internal static class LegacyWaterConverter
{
    internal static string BodyId(RegionCoord region, int plane, int index)
        => string.Create(CultureInfo.InvariantCulture, $"legacy-water/{region.Rx}/{region.Rz}/{plane}/{index}");

    internal static (MapDocument Document, IReadOnlyList<LegacyWaterSource> Sources) ToNative(
        TileWorldDocument document, TileWorldCatalogs catalogs)
    {
        var surfaces = new MapSurfaceSet();
        var sources = new List<LegacyWaterSource>();
        foreach (var (region, plane) in LegacyOracleConverter.RegionPlanes(document))
        {
            var (surface, patch) = LegacyOracleConverter.ToNative(document, region, plane);
            if (surfaces.Refs.All(s => s.Id != surface.Id)) surfaces.Refs.Add(surface);
            surfaces.Patches.Add(patch.Key, patch);
            IReadOnlyList<TileWaterBody> bodies = TileWaterBodies.Collect(document, catalogs, region, plane);
            if (plane != 0 && bodies.Count != 0)
                throw new MapDocumentException(string.Create(CultureInfo.InvariantCulture,
                    $"water on plane {plane} is outside the R4 comparison"));
            for (int i = 0; i < bodies.Count; i++)
                sources.Add(new(BodyId(region, plane, i), region, plane, i, bodies[i]));
        }

        MapSurfacePatch[] ground = surfaces.Patches.Values.Where(p => p.Key.SurfaceId == "plane-0").ToArray();
        if (ground.Length == 0) throw new MapDocumentException("legacy water comparison requires a plane-0 region");
        MapRecordRef cover = new("legacy-world", ground[0].Key);
        var space = new MapSpaceDoc(cover.Id, MapSpaceKind.Exterior, null, null, Array.Empty<string>(),
            Array.Empty<MapBoundaryRef>(), Array.Empty<MapBoundaryRef>(), Array.Empty<MapRecordRef>());
        ground[0].Records.Add(space);
        foreach (MapSurfacePatch patch in ground)
        {
            var footprint = new MapSpaceFootprint(string.Create(CultureInfo.InvariantCulture,
                $"legacy-world/{patch.Key.SlotX}/{patch.Key.SlotZ}"), cover, patch.Key,
                Enumerable.Range(0, TileRegion.TileCount).ToArray(),
                new(MapBoundKind.LegacyExteriorV1, patch.Key.SurfaceId, null), new(MapBoundKind.OpenTop, null, null));
            string? failure = MapLegacyExteriorRecipe.Check(footprint, space,
                surfaces.Refs.Single(s => s.Id == patch.Key.SurfaceId));
            if (failure is not null) throw new MapDocumentException(failure);
            patch.Records.Add(footprint);
        }
        for (int i = 0; i < surfaces.Refs.Count; i++)
        {
            MapSurfaceRef surface = surfaces.Refs[i];
            surfaces.Refs[i] = surface with
            {
                SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface, surfaces.Patches
                    .Where(p => p.Key.SurfaceId == surface.Id)
                    .Select(p => new KeyValuePair<MapPatchKey, string>(p.Key, MapSurfaceSemantics.PatchDigest(p.Value)))),
            };
        }

        var water = new MapWaterSet();
        water.Media.Add(new("legacy", MapWaterMediumKind.Water, 1f));
        foreach (LegacyWaterSource source in sources)
            water.Bodies.Add(new(source.BodyId, source.Body.SurfaceY, "legacy", "legacy-water",
                new(MapLatticeFrame.ImportedMetreCentimetre, source.Body.Rects
                    .Select(r => new MapWaterRect(r.X, r.Z, (long)r.X + r.Width, (long)r.Z + r.Height)).ToArray(),
                    Array.Empty<MapWaterRing>()), new[] { cover }));

        MapAssetClosure assets = Assets();
        var bounds = new MapBounds
        {
            MinX = document.Regions.Values.Min(r => r.Coord.OriginX),
            MaxX = document.Regions.Values.Max(r => r.Coord.OriginX + TileRegion.Size),
            MinZ = -document.Regions.Values.Max(r => r.Coord.OriginZ + TileRegion.Size),
            MaxZ = -document.Regions.Values.Min(r => r.Coord.OriginZ),
        };
        var native = new MapDocument
        {
            Id = document.Id,
            Bounds = bounds,
            PlayableBounds = new() { MinX = bounds.MinX, MaxX = bounds.MaxX, MinZ = bounds.MinZ, MaxZ = bounds.MaxZ },
            TileSize = TileRegion.Size,
            SupportRecipe = MapSupportRecipe.AuthoredBindingsV2,
            ResolverIdentity = new(1, 2),
            Surfaces = surfaces,
            Water = water,
            NativeAssets = assets.Roots.ToList(),
        };
        MapBoundDocumentValidation.Validate(native, assets);
        return (native, sources.AsReadOnly());
    }

    // The material closure belongs to the conversion, so linked private comparisons need no fixture asset source.
    static MapAssetClosure Assets()
    {
        var source = new WaterAssetSource();
        MapAssetRef material = source.Add("legacy-water", Encoding.UTF8.GetBytes("legacy-water"));
        MapAssetRef root = source.Add("legacy-water-root", Encoding.UTF8.GetBytes(new JsonObject
        {
            ["payloadVersion"] = 1,
            ["assets"] = new JsonArray(),
            ["resources"] = new JsonArray(new JsonObject
            {
                ["reference"] = new JsonObject
                {
                    ["id"] = material.Id,
                    ["path"] = material.Path,
                    ["sha256"] = material.Sha256,
                    ["payloadVersion"] = material.PayloadVersion,
                },
                ["kind"] = "Material",
                ["dependencies"] = new JsonArray(),
            }),
        }.ToJsonString()));
        return MapAssetClosure.Load(new[] { root }, source);
    }

    sealed class WaterAssetSource : IMapAssetSource
    {
        readonly Dictionary<string, byte[]> _bytes = new(StringComparer.Ordinal);
        internal MapAssetRef Add(string id, byte[] bytes)
        {
            _bytes.Add(id, bytes);
            return new(id, "assets/" + id, Convert.ToHexStringLower(SHA256.HashData(bytes)), 1);
        }
        public ReadOnlyMemory<byte> Read(MapAssetRef reference) => _bytes[reference.Id];
    }
}
