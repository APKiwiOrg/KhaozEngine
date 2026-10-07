using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>Public synthetic worlds and corruption against the writer's actual emitted page envelopes.</summary>
internal static class SurfaceStorageFixtures
{
    internal static readonly MapPatchKey Wide0 = new("wide", 0, 0), Edge1 = new("edge", 1, 0),
        Yard0 = new("ground", 0, 0), YardFar = new("ground", 300, 0);

    static MapDocument Document()
    {
        var assets = NativeAssetFixtures.Valid();
        MapDocument doc = MapDocumentFile.LoadText(NativeFixtures.AnalyticV3Json());
        doc.Id = "surface-storage-synthetic";
        doc.ResolverIdentity = new(1, 2);
        doc.SupportRecipe = MapSupportRecipe.AuthoredBindingsV2;
        doc.NativeAssets = assets.Roots.ToList();
        doc.TileSize = 64;
        doc.Bounds = new() { MinX = -22000, MinZ = -22000, MaxX = 22000, MaxZ = 22000 };
        doc.Placements.Clear();
        return doc;
    }
    static void Surface(MapDocument doc, string id, MapRational? cell = null, MapRational? height = null)
    {
        doc.Surfaces.Refs.Add(new(id, new(cell ?? new(1, 1), height ?? new(1, 100), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0),
            MapSurfaceRole.SupportFloor, MapPresencePolicy.Native, null, null, new string('0', 64)));
    }
    internal static MapDocument ThreeSurfaces()
    {
        MapDocument doc = Document();
        foreach (string id in new[] { "ground", "ridge", "far" }) Surface(doc, id);
        var ground0 = Patch(new("ground", 0, 0), 1000, 60, 0, 4, 4);
        var ground1 = Patch(new("ground", 1, 0), 1000, 0, 0, 64, 4);
        for (int z = 0; z <= 4; z++) ground1.CornerDependencies.Add(new(0, z, new(ground0.Key, MapLatticeAddress.Corner(64, z))));
        var ridge = Patch(new("ridge", 0, 0), 1500, 60, 0, 4, 4);
        ridge.Records.Add(new MapBoundaryChain("ridge-rim", MapChainKind.Authored, null, new[]
        {
            new MapChainVertex(new("ridge", MapLatticeAddress.Corner(60, 0)), 1500),
            new MapChainVertex(new("ridge", MapLatticeAddress.Corner(64, 0)), 1500),
        }));
        foreach (MapSurfacePatch patch in new[] { ground0, ground1, ridge, FlatPatch(new("far", 300, 0), 2000), FlatPatch(new("far", -300, 0), 2000) })
            doc.Surfaces.Patches.Add(patch.Key, patch);
        return doc;
    }
    internal static void Apply(MapDocument doc, string change)
    {
        switch (change)
        {
            case "height":
                doc.Surfaces.Patches[new("ground", 0, 0)].Heights[5]++;
                break;
            case "unit":
                {
                    int index = doc.Surfaces.Refs.FindIndex(s => s.Id == "ridge");
                    MapSurfaceRef surface = doc.Surfaces.Refs[index];
                    doc.Surfaces.Refs[index] = surface with { Frame = surface.Frame with { HeightUnitMetres = new(1, 1000) } };
                    MapSurfacePatch patch = doc.Surfaces.Patches[new("ridge", 0, 0)];
                    for (int i = 0; i < patch.Heights.Length; i++) patch.Heights[i] *= 10;
                    int recordIndex = patch.Records.FindIndex(r => r.Id == "ridge-rim");
                    var chain = (MapBoundaryChain)patch.Records[recordIndex];
                    patch.Records[recordIndex] = chain with
                    {
                        Vertices = chain.Vertices.Select(v => v with { HeightUnits = v.HeightUnits * 10 }).ToArray(),
                    };
                    break;
                }
            case "record":
                {
                    MapSurfacePatch patch = doc.Surfaces.Patches[new("ridge", 0, 0)];
                    int index = patch.Records.FindIndex(r => r.Id == "ridge-rim");
                    var chain = (MapBoundaryChain)patch.Records[index];
                    MapChainVertex[] vertices = chain.Vertices.ToArray();
                    vertices[0] = vertices[0] with { HeightUnits = vertices[0].HeightUnits + 1 };
                    patch.Records[index] = chain with { Vertices = vertices };
                    break;
                }
            case "role":
                {
                    int index = doc.Surfaces.Refs.FindIndex(s => s.Id == "ridge");
                    doc.Surfaces.Refs[index] = doc.Surfaces.Refs[index] with { Role = MapSurfaceRole.Ceiling };
                    break;
                }
            case "displayName":
                doc.DisplayName += " renamed";
                break;
            default:
                throw new ArgumentException("unknown synthetic identity change", nameof(change));
        }
    }
    internal static MapDocument ReverseDependants(bool withFinePage)
    {
        MapDocument doc = Document();
        Surface(doc, "wide", new(2, 1)); Surface(doc, "edge", new(2, 1));
        MapSurfacePatch wide = Patch(Wide0, 1000, 0, 0, 64, 2), edge = Patch(Edge1, 1000, 0, 0, 2, 2);
        for (int z = 0; z <= 2; z++) edge.CornerDependencies.Add(new(0, z, new(Wide0, MapLatticeAddress.Corner(64, z))));
        doc.Surfaces.Patches.Add(Wide0, wide); doc.Surfaces.Patches.Add(Edge1, edge);
        if (withFinePage)
        {
            Surface(doc, "fine", new(1, 16), new(1, 1600));
            var key = new MapPatchKey("fine", 40, 0); doc.Surfaces.Patches.Add(key, FlatPatch(key, 0));
        }
        return doc;
    }
    internal static MapDocument RecordReferrer()
    {
        MapDocument doc = Document(); Surface(doc, "ground");
        var yard = FlatPatch(Yard0, 1000); var far = FlatPatch(YardFar, 1000);
        yard.Records.Add(new MapSpaceDoc("yard", MapSpaceKind.Exterior, null, null, Array.Empty<string>(),
            Array.Empty<MapBoundaryRef>(), Array.Empty<MapBoundaryRef>(), Array.Empty<MapRecordRef>()));
        far.Records.Add(new MapSpaceFootprint("yard-far", new("yard", Yard0), YardFar, new[] { 0 },
            new(MapBoundKind.SupportFloor, "ground", null), new(MapBoundKind.OpenTop, null, null)));
        doc.Surfaces.Patches.Add(Yard0, yard); doc.Surfaces.Patches.Add(YardFar, far);
        return doc;
    }
    internal static MapAssetClosure Assets()
    {
        var assets = NativeAssetFixtures.Valid();
        return MapAssetClosure.Load(assets.Roots, assets.Source);
    }
    internal static MapPlacement ExplicitYPlacement(string id, float x, float z) => new()
    {
        Id = id,
        Kind = "scenery",
        AssetId = "tree",
        X = x,
        Z = z,
        Y = 10,
    };
    internal static MapSurfacePatch FlatPatch(MapPatchKey key, int height) => Patch(key, height, 0, 0, 4, 4);
    static MapSurfacePatch Patch(MapPatchKey key, int height, int minX, int minZ, int width, int depth)
    {
        int count = width * depth;
        var patch = new MapSurfacePatch
        {
            Key = key,
            CellMinX = minX,
            CellMinZ = minZ,
            Width = width,
            Depth = depth,
            Heights = Enumerable.Repeat(height, (width + 1) * (depth + 1)).ToArray(),
            Cells = Enumerable.Repeat(new MapSurfaceCell(1, 0, MapOverlayCut.Full, 0, MapCellFlags.None, MapCellTopology.Auto), count).ToArray(),
            Presence = Enumerable.Repeat(ulong.MaxValue, (count + 63) / 64).ToArray(),
        };
        if (count % 64 != 0) patch.Presence[^1] = (1UL << (count % 64)) - 1;
        return patch;
    }
    internal static MapDocument FlatPatches(int count)
    {
        MapDocument doc = Document(); Surface(doc, "ground");
        for (int i = 0; i < count; i++)
        {
            var key = new MapPatchKey("ground", i, 0); doc.Surfaces.Patches.Add(key, FlatPatch(key, 1000));
        }
        return doc;
    }
    internal static string SaveToTemp(MapDocument doc)
    {
        string root = Path.Combine(Path.GetTempPath(), "mapdoc-surface-synthetic-" + Guid.NewGuid().ToString("N"));
        try { MapDocumentFile.SaveTiled(doc, root); return root; }
        catch { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); throw; }
    }
    internal static MapDocument LoadSlotWindow(string directory, int slotX) =>
        MapDocumentFile.LoadTiled(directory, new MapTileRect(new(slotX, 0), new(slotX, 0)));
    internal static string SemanticSnapshot(MapDocument doc) => KhaozEngine.MapDoc.Identity.MapAuthoredIdentityV2.Compute(
        doc, Assets(), new MapResolveOptions("headless", 1, "options", ResolverVersion: 2));
    internal static IReadOnlyList<string> SurfaceFiles(string root)
    {
        string dir = Path.Combine(root, "tiles", "surfaces");
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p)).OrderBy(p => p, StringComparer.Ordinal).ToArray() : Array.Empty<string>();
    }
    internal static byte[] PayloadBytes(string root, MapPatchKey key) => File.ReadAllBytes(PayloadPath(root, key));
    internal static void DeletePayload(string root, MapPatchKey key) => File.Delete(PayloadPath(root, key));
    internal static void FlipPayloadByte(string root, MapPatchKey key)
    {
        string path = PayloadPath(root, key); byte[] bytes = File.ReadAllBytes(path); bytes[0] ^= 1; File.WriteAllBytes(path, bytes);
    }
    static string PayloadPath(string root, MapPatchKey key)
    {
        var row = Locate(root, key); string hash = row.Entry["payloadSha256"]!.GetValue<string>();
        return Path.Combine(root, "tiles", "surfaces", "p", hash[..2], hash + ".json");
    }
    internal static void RewriteIndexSemanticDigest(string root, MapPatchKey key, string digest)
    {
        var row = Locate(root, key);
        row.Entry["semanticSha256"] = digest;
        string index = WritePage(root, 'i', row.Index);
        row.IndexRef["sha256"] = index;
        string directory = WritePage(root, 'd', row.Directory);
        row.DirectoryRef["sha256"] = directory;
        File.WriteAllText(Path.Combine(root, "map.json"), row.Manifest.ToJsonString());
    }
    static string WritePage(string root, char kind, JsonObject page)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(page.ToJsonString());
        string digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        File.WriteAllBytes(Path.Combine(root, "tiles", "surfaces", kind.ToString(), digest + ".json"), bytes);
        return digest;
    }
    static (JsonObject Manifest, JsonObject DirectoryRef, JsonObject Directory, JsonObject IndexRef, JsonObject Index, JsonObject Entry) Locate(string root, MapPatchKey key)
    {
        JsonObject manifest = JsonNode.Parse(File.ReadAllBytes(Path.Combine(root, "map.json")))!.AsObject();
        foreach (JsonObject dirRef in manifest["surfaceStorage"]!.AsArray().Select(n => n!.AsObject()))
        {
            if (dirRef["surfaceId"]!.GetValue<string>() != key.SurfaceId || !Covers(dirRef, key)) continue;
            JsonObject dir = ReadPage(root, 'd', dirRef);
            foreach (JsonObject indexRef in dir["pages"]!.AsArray().Select(n => n!.AsObject()))
            {
                if (!Covers(indexRef, key)) continue;
                JsonObject index = ReadPage(root, 'i', indexRef);
                JsonObject entry = index["entries"]!.AsArray().Select(n => n!.AsObject()).Single(e =>
                    e["key"]!["surfaceId"]!.GetValue<string>() == key.SurfaceId &&
                    e["key"]!["slotX"]!.GetValue<string>() == key.SlotX.ToString(CultureInfo.InvariantCulture) &&
                    e["key"]!["slotZ"]!.GetValue<string>() == key.SlotZ.ToString(CultureInfo.InvariantCulture));
                return (manifest, dirRef, dir, indexRef, index, entry);
            }
        }
        throw new InvalidOperationException("synthetic patch not in manifest closure");
    }
    static JsonObject ReadPage(string root, char kind, JsonObject reference) => JsonNode.Parse(File.ReadAllBytes(
        Path.Combine(root, "tiles", "surfaces", kind.ToString(), reference["sha256"]!.GetValue<string>() + ".json")))!.AsObject();
    static bool Covers(JsonObject reference, MapPatchKey key)
    {
        JsonNode r = reference["covers"]!;
        return key.SlotX >= r["minX"]!.GetValue<long>() && key.SlotX < r["maxXExclusive"]!.GetValue<long>() &&
            key.SlotZ >= r["minZ"]!.GetValue<long>() && key.SlotZ < r["maxZExclusive"]!.GetValue<long>();
    }
}
