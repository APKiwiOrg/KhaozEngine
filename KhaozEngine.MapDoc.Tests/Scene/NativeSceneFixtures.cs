using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc.Scene;

internal sealed record NativeSceneFixture(MapDocument Document, MapAssetClosure Assets) : IDisposable
{
    internal List<string> StorageDirectories { get; } = new();
    public void Dispose()
    {
        foreach (string directory in StorageDirectories) if (Directory.Exists(directory)) Directory.Delete(directory, true);
        StorageDirectories.Clear();
    }
}

internal static partial class NativeSceneFixtures
{
    internal static NativeSceneFixture Create(string variant)
    {
        if (variant.StartsWith("terrain", StringComparison.Ordinal)) return Terrain(variant);
        if (variant is not ("spanning" or "empty" or "growth" or "tile-owner")) throw new ArgumentException(variant, nameof(variant));
        var bytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var rows = new JsonArray();
        void Resource(string id, string kind, byte[] payload)
        {
            bytes.Add(id, payload);
            rows.Add(new JsonObject
            {
                ["reference"] = Reference(id, payload),
                ["kind"] = kind,
                ["dependencies"] = new JsonArray(),
            });
        }
        Resource("mesh", "Mesh", Encoding.UTF8.GetBytes("invented-scene-mesh"));
        Resource("lod", "Lod", Encoding.UTF8.GetBytes("invented-long-lod"));
        var leaf = new MapPrefabPlacementChild(new(MapPrefabChildKind.Placement, "leaf"), new MapPlacement
        { Id = "leaf", Kind = "cosmetic", AssetId = "branch", Y = 7, Scale = 1 });
        var light = new MapPrefabLightChild(new(MapPrefabChildKind.Light, "light"),
            new(1, "light", null, new(65, 2, 0), Vector3.One, 1, 4, true));
        Resource("assembly", "Prefab", MapPrefabFile.Write(new(1, "assembly", new MapPrefabChild[] { leaf, light })));
        rows[2]!["dependencies"] = new JsonArray("mesh", "lod");
        var assets = new JsonArray();
        int count = variant == "growth" ? 25 : 1;
        for (int i = 0; i < count; i++)
            assets.Add(new JsonObject
            {
                ["id"] = i == 0 ? "branch" : "distant-" + i,
                ["meshResourceId"] = "mesh",
                ["supportResourceIds"] = new JsonArray(),
                ["materialResourceIds"] = new JsonArray(),
                ["lodResourceIds"] = new JsonArray("lod"),
                ["lightResourceIds"] = new JsonArray(),
                ["sourceUnitsToMetres"] = 1,
                ["renderBounds"] = Bounds(1),
                ["lodBounds"] = Bounds(70),
                ["source"] = "invented.blend",
                ["license"] = "CC0",
                ["textured"] = false,
            });
        byte[] manifest = Encoding.UTF8.GetBytes(new JsonObject
        { ["payloadVersion"] = 1, ["assets"] = assets, ["resources"] = rows }.ToJsonString());
        bytes.Add("root", manifest);
        var root = new MapAssetRef("root", "assets/root", Digest(manifest), 1);
        var closure = MapAssetClosure.Load(new[] { root }, new SceneAssets(bytes));
        var document = new MapDocument
        {
            Id = "invented-native-scene",
            TileSize = 64,
            Bounds = new() { MinX = -32000, MinZ = -32000, MaxX = 32000, MaxZ = 32000 },
            PlayableBounds = new() { MinX = -32000, MinZ = -32000, MaxX = 32000, MaxZ = 32000 },
            ResolverIdentity = new(1, 2),
            SupportRecipe = MapSupportRecipe.AuthoredBindingsV2,
            NativeAssets = new() { root },
            NumericIdHighWaterMark = 1,
        };
        if (variant != "empty")
            document.Prefabs = new(1, 1, new[]
            {
                new MapPrefabInstance("spanning", "assembly", Digest(bytes["assembly"]), new(new(-65, 0, 0), 0, 1), null,
                    new(new[] { new MapPrefabBinding(leaf.Key, "spanning-leaf", 1, false),
                        new MapPrefabBinding(light.Key, "spanning-light", null, false) }), Array.Empty<MapPrefabOverride>()),
            }, Array.Empty<MapPrefabBindingTransfer>());
        if (variant == "tile-owner")
        {
            document.Prefabs = null;
            document.Placements.Add(new() { Id = "spanning-leaf", Kind = "cosmetic", AssetId = "branch", NumericId = 1, X = -65, Y = 7, Z = 0 });
            document.BuildingIntent = new(1, [], [], new[] { new MapLocalLightDoc(1, "spanning-light",
                new(MapPrefabReferenceScope.World, MapPrefabChildKind.Placement, "spanning-leaf"), new(65, 2, 0), Vector3.One, 1, 4, true) }, []);
        }
        for (int i = 1; i < count; i++)
            document.Placements.Add(new()
            {
                Id = "distant-" + i,
                Kind = "cosmetic",
                AssetId = "distant-" + i,
                NumericId = ++document.NumericIdHighWaterMark,
                X = 1024 + i * 128,
                Z = 1024,
                Y = 0
            });
        return new(document, closure);
    }

    static NativeSceneFixture Terrain(string variant)
    {
        var fixture = Create("empty");
        var document = fixture.Document;
        void Surface(string id, MapSurfaceRole role, ushort underlay, ushort overlay = 0,
            MapOverlayCut cut = MapOverlayCut.Full, int height = 125, int width = 1)
        {
            var patch = new MapSurfacePatch
            {
                Key = new(id, id == "last" ? 1 : 0, 0),
                Width = width,
                Depth = 1,
                Heights = Enumerable.Repeat(height, 2 * (width + 1)).ToArray(),
                Cells = Enumerable.Range(0, width).Select(i => new MapSurfaceCell(
                    (variant == "terrain-many" && id == "ground") || (variant == "terrain-many-face" && id == "many-paint")
                        ? (ushort)(underlay + i) : underlay, overlay, cut, 0,
                    overlay == 0 ? MapCellFlags.None : MapCellFlags.FeatherOverlay, MapCellTopology.ForceSwNe)).ToArray(),
                Presence = new[] { width == 64 ? ulong.MaxValue : (1UL << width) - 1 },
            };
            var surface = new MapSurfaceRef(id, MapLatticeFrame.ImportedMetreCentimetre, role,
                MapPresencePolicy.Native, role == MapSurfaceRole.PaintOverride ? "ground" : null, null, "");
            document.Surfaces.Patches.Add(patch.Key, patch);
            document.Surfaces.Refs.Add(surface with
            {
                SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
                    new[] { KeyValuePair.Create(patch.Key, MapSurfaceSemantics.PatchDigest(patch)) }),
            });
        }
        Surface("ground", MapSurfaceRole.SupportFloor, variant is "terrain-zero" or "terrain-zero-overlay" ? (ushort)0 : (ushort)10,
            variant is "terrain-feather" or "terrain-cuts" or "terrain-combined" or "terrain-zero-overlay" ? (ushort)11 : (ushort)0,
            variant == "terrain-cuts" ? MapOverlayCut.DiagonalHalf : MapOverlayCut.Full,
            width: variant == "terrain-many" ? 64 : 1);
        if (variant == "terrain-many") Surface("last", MapSurfaceRole.SupportFloor, 74);
        if (variant is "terrain-ceiling" or "terrain-posed" or "terrain-combined") Surface("ceiling", MapSurfaceRole.Ceiling, 10, height: 425);
        if (variant == "terrain-wall")
        {
            var patch = document.Surfaces.Patches[new("ground", 0, 0)];
            foreach (var (id, height) in new[] { ("lower", 125), ("upper", 425) })
                patch.Records.Add(new MapBoundaryChain(id, MapChainKind.Authored, null, new[]
                {
                    new MapChainVertex(new("ground", MapLatticeAddress.Corner(0, 0)), height),
                    new MapChainVertex(new("ground", MapLatticeAddress.Corner(1, 0)), height),
                }));
            patch.Records.Add(new MapWallStrip("wall", new("lower", patch.Key), new("upper", patch.Key), MapStripFacing.TwoSided, 10));
            var surface = document.Surfaces.Refs[0];
            document.Surfaces.Refs[0] = surface with
            {
                SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
                new[] { KeyValuePair.Create(patch.Key, MapSurfaceSemantics.PatchDigest(patch)) })
            };
        }
        if (variant is "terrain-paint" or "terrain-conflict" or "terrain-combined")
        {
            Surface("paint-b", MapSurfaceRole.PaintOverride, variant == "terrain-conflict" ? (ushort)11 : (ushort)12, height: 0);
            Surface("paint-a", MapSurfaceRole.PaintOverride, 12, height: 0);
            document.NativeResourceTransforms = new[] { "paint-b", "paint-a" }.Select(id =>
                new MapStandaloneResourceTransform(MapPrefabChildKind.Surface, id,
                    new(.25, 0, -.25, 0, .5), new string('a', 64))).ToArray();
        }
        if (variant == "terrain-many-face")
        {
            Surface("many-paint", MapSurfaceRole.PaintOverride, 11, height: 0, width: 64);
            document.NativeResourceTransforms = new[] { new MapStandaloneResourceTransform(MapPrefabChildKind.Surface, "many-paint",
                new(.1, 0, -.02, 0, .005), new string('a', 64)) };
        }
        if (variant == "terrain-posed")
            document.NativeResourceTransforms = new[] { "ground", "ceiling" }.Select(id =>
                new MapStandaloneResourceTransform(MapPrefabChildKind.Surface, id,
                    new(31999.123456, -499.5, -31999.654321, .371, 1.2), new string('a', 64))).ToArray();
        return fixture;
    }

    internal static NativeSceneFixture Reload(NativeSceneFixture fixture, bool tiled)
    {
        if (!tiled) return fixture with { Document = MapDocumentFile.LoadText(MapDocumentFile.SaveText(fixture.Document)) };
        string directory = TempDirectory();
        try
        {
            MapDocumentFile.SaveTiled(fixture.Document, directory);
            return fixture with { Document = MapDocumentFile.LoadTiled(directory) };
        }
        finally { Directory.Delete(directory, true); }
    }

    internal static MapSceneSource Open(NativeSceneFixture fixture, bool tiled)
    {
        using var source = tiled ? OpenTiled(fixture) : MapDocumentSource.FromDocument(fixture.Document);
        var result = MapSceneSource.Capture(source, fixture.Assets);
        return result.Source ?? throw new InvalidOperationException("Scene capture refused " + result.Status + " " + string.Join("\n", result.Findings));
    }

    internal static MapDocumentSource OpenTiled(NativeSceneFixture fixture)
    {
        string directory = TempDirectory();
        MapDocumentFile.SaveTiled(fixture.Document, directory);
        fixture.StorageDirectories.Add(directory);
        return MapDocumentSource.OpenTiled(directory);
    }

    internal static string TempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "khaozengine-native-scene-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    static JsonObject Reference(string id, byte[] bytes) => new()
    { ["id"] = id, ["path"] = "assets/" + id, ["sha256"] = Digest(bytes), ["payloadVersion"] = 1 };
    static JsonObject Bounds(int maxX) => new()
    {
        ["min"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["z"] = -1 },
        ["max"] = new JsonObject { ["x"] = maxX, ["y"] = 2, ["z"] = 1 },
    };
    static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    sealed class SceneAssets(IReadOnlyDictionary<string, byte[]> bytes) : IMapAssetSource
    {
        public ReadOnlyMemory<byte> Read(MapAssetRef reference) => bytes[reference.Id];
    }
}
