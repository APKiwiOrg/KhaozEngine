using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Water;

namespace KhaozEngine.Tests.MapDoc.Foliage;

/// <summary>The invented wire preview fixture. Linked into other test projects, so nothing here reads a committed file.</summary>
internal static partial class FoliageFixtures
{
    internal const string WireLayerId = "wire-cover";
    internal const string WireFloorId = "wire-floor";
    internal const string WireModelId = "wire-reed";
    internal const string WireMaterialId = "wire-material";

    /// <summary>Layer wire-cover on Surface wire-floor, a native floor at a constant 250 cm (2.5 m) over world X and Z
    /// [-4, 4] on the imported metre and centimetre lattice, with underlay 10, in a resolver (1, 2) AuthoredBindingsV2
    /// document. No indoor cells, rules or masks exist.</summary>
    internal static FoliageFixture ReachableWirePreview()
    {
        var (roots, source) = WireAssetInputs();
        var set = new MapSurfaceSet();
        WaterFixtures.Surface(set, WireFloorId, MapSurfaceRole.SupportFloor, MapLatticeFrame.ImportedMetreCentimetre, -4, -4, 4, 4, (_, _) => 250);
        foreach (MapSurfacePatch patch in set.Patches.Values)
            for (int i = 0; i < patch.Cells.Length; i++) patch.Cells[i] = patch.Cells[i] with { Underlay = 10 };
        MapDocument document = MixedResolutionFixtures.Document(set);
        document.Id = "foliage-wire-fixture";
        document.NativeAssets = roots.ToList();
        document.Foliage = new MapFoliageSet(1, [WireLayer(WireLayerId)]);
        return new(document, MapAssetClosure.Load(roots, source));
    }

    /// <summary>A layer on wire-floor: raster origin (0, 0), cell size 1, 2 by 2 samples of 255 in chunk (0, 0) with every
    /// other byte zero, seed 17, spacing 0.5, scale 1.2 to 1.2, root offset 0, one model wire-reed of weight 1, ordinary
    /// visible material 10, DoorClearance and EdgeFade 0, and compatibility row wire-floor to ordinal 1.</summary>
    internal static MapFoliageLayer WireLayer(string id) => new(1, id, Surface(WireFloorId), 0, 0, 1, 2, 2, MapRowDirection.PositiveZ,
        17, 0.5f, 1.2f, 1.2f, 0, [new(WireModelId, 1)], [10], [new(10, WireMaterialId, MapFoliageMaterialKind.Ordinary)],
        new("legacy-tile", 1, 0), false, false, 0, 0, [new(WireFloorId, 1)], [], [], [], [new MapFoliageChunk(new(0, 0), WireDensity())]);

    /// <summary>The 1024 bytes of wire chunk (0, 0): samples (0, 0), (1, 0), (0, 1) and (1, 1) are 255.</summary>
    internal static byte[] WireDensity()
    {
        var bytes = new byte[MapFoliageChunk.ByteLength];
        foreach (int index in new[] { 0, 1, MapFoliageChunk.Size, MapFoliageChunk.Size + 1 }) bytes[index] = 255;
        return bytes;
    }

    // The shared native fixture closure plus model wire-reed and one invented Material resource.
    static (IReadOnlyList<MapAssetRef> Roots, NativeMemoryAssetSource Source) WireAssetInputs()
    {
        var (roots, source) = NativeAssetFixtures.Change(root =>
        {
            JsonObject asset = NativeAssetFixtures.Asset();
            asset["id"] = WireModelId;
            root["assets"]!.AsArray().Add(asset);
        });
        MapAssetRef material = source.Add(WireMaterialId, Encoding.UTF8.GetBytes("bytes:" + WireMaterialId));
        MapAssetRef root = source.Add("root-foliage-wire", new JsonObject
        {
            ["payloadVersion"] = 1,
            ["assets"] = new JsonArray(),
            ["resources"] = new JsonArray(NativeAssetFixtures.Resource(material, "Material")),
        }.ToJsonString());
        return (roots.Append(root).ToArray(), source);
    }
}
