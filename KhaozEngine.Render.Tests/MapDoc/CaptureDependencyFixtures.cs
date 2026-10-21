using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;

namespace KhaozEngine.Tests.MapDoc;

internal static class CaptureDependencyFixtures
{
    internal static MapSnapshotRequest Request(int previewWindowLimit = 256)
    {
        var (document, closure) = Create();
        using var input = MapDocumentSource.FromDocument(document);
        var source = MapSceneSource.Capture(input, closure).Source!;
        var extents = MapRenderExtentValidator.Validate(closure, source.RequiredAssetIds, new(new())).Proofs!;
        source = source.WithRenderExtents(extents).Source!;
        var scope = new MapSceneScope(new(WorldFrame.Origin, new(-4, -4), new(4, 4), -1, 8,
            Enum.GetValues<MapSurfaceRole>(), null, new()), new(-4, -1, -4, 4, 8, 4), MapSceneChannels.All, .35);
        var point = new MapFramePoint(WorldFrame.Origin, Vector3.Zero);
        return new(closure, source, scope, new(new(WorldFrame.Origin, new(0, 8, 0)), point, true, 8, 640, 480, .1f, 32),
            new(WorldFrame.Origin, new(2, 1, 1)), null, MapRoofMode.AlwaysHidden, 3.25, 23, "windy",
            new(Policy(), Lighting(), MapCaptureAa.Quality, 4), MapCaptureRequirement.CompleteScope, Focus: new(WorldFrame.Origin, new(5, 0, 0)))
        { PreviewWindowLimit = previewWindowLimit };
    }
    internal static MapCaptureLighting Lighting() => new(Vector3.Normalize(new(-1, 1, 0)), Color.White, 1,
        new() { Mode = ShadowMode.ShadowMap, ShadowMapResolution = 1024, ShadowCascadeCount = 2, ShadowNearDistance = 8, ShadowMaxDistance = 32 }, 16);
    internal static MapRenderPolicy Policy() => new(new([], 200), new HashSet<string>(),
        new(MapFrozenFoliageOptions.Capture(new GroundCoverRenderOptions
        {
            UseGpuBatches = true,
            FadeMode = GroundCoverFadeMode.HeightScale,
            WindStrength = .7f
        }), new Dictionary<string, KhaozEngine.MapDoc.Foliage.MapFoliageModelExtent>()),
        MapFrozenWaterLook.Capture(new WaterLook { SwellAmplitude = 0, WaveSource = WaterWaveSource.Procedural }), ["floor-material"],
        new Dictionary<ushort, string> { [10] = "floor-material" });
    internal static (MapDocument Document, MapAssetClosure Closure) Create(string? directory = null)
    {
        var closure = NativeRenderAssetFixtures.Capture(directory);
        var document = new MapDocument
        {
            Id = "invented-capture",
            TileSize = 64,
            Bounds = new() { MinX = -128, MinZ = -128, MaxX = 128, MaxZ = 128 },
            PlayableBounds = new() { MinX = -128, MinZ = -128, MaxX = 128, MaxZ = 128 },
            ResolverIdentity = new(1, 2),
            SupportRecipe = MapSupportRecipe.AuthoredBindingsV2,
            NativeAssets = closure.Roots.ToList(),
            NumericIdHighWaterMark = 3
        };
        var patch = new MapSurfacePatch
        {
            Key = new("capture-floor", 0, 0),
            CellMinX = 0,
            CellMinZ = 0,
            Width = 8,
            Depth = 8,
            Heights = new int[81],
            Cells = Enumerable.Repeat(new MapSurfaceCell(10, 0, MapOverlayCut.Full, 0, MapCellFlags.None, MapCellTopology.ForceSwNe), 64).ToArray(),
            Presence = [ulong.MaxValue]
        };
        var surface = new MapSurfaceRef("capture-floor", MapLatticeFrame.ImportedMetreCentimetre with { RowDirection = MapRowDirection.PositiveZ }, MapSurfaceRole.SupportFloor, MapPresencePolicy.Native, null, null, "");
        document.Surfaces.Patches.Add(patch.Key, patch);
        document.Surfaces.Refs.Add(surface with
        {
            SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
            [KeyValuePair.Create(patch.Key, MapSurfaceSemantics.PatchDigest(patch))])
        });
        document.NativeResourceTransforms = [new(MapPrefabChildKind.Surface, "capture-floor", new(-4, 0, -4, 0, 1), new string('a', 64))];
        document.Placements = [new() { Id = "sun-caster", Kind = "cosmetic", AssetId = "sun", NumericId = 1, X = -8, Y = 3, Z = 0 },
            new() { Id = "point-caster", Kind = "cosmetic", AssetId = "point", NumericId = 2, X = 5, Y = 2.5f, Z = 0 },
            new() { Id = "unrelated-far", Kind = "cosmetic", AssetId = "sun", NumericId = 3, X = 80, Y = 0, Z = 80 }];
        document.BuildingIntent = new(1, [], [], [new MapLocalLightDoc(1, "point-a", null, new(6, 4, 0), Vector3.One, 2, 10, true)], []);
        return (document, closure);
    }
}
internal static partial class NativeRenderAssetFixtures
{
    internal static MapAssetClosure Capture(string? directory = null)
    {
        var source = new Source(); var resources = new JsonArray(); var assets = new JsonArray();
        void Write(MapAssetRef reference, byte[] bytes)
        {
            if (directory is null) return;
            string path = Path.Combine(directory, reference.Path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes);
        }
        void Resource(string id, string kind, byte[] bytes)
        {
            var r = source.Add(id, bytes); Write(r, bytes);
            resources.Add(new JsonObject
            {
                ["reference"] = new JsonObject { ["id"] = r.Id, ["path"] = r.Path, ["sha256"] = r.Sha256, ["payloadVersion"] = r.PayloadVersion },
                ["kind"] = kind,
                ["dependencies"] = new JsonArray()
            });
        }
        Resource("floor-material", "Material", Encoding.UTF8.GetBytes("""
        {"schema":"kemap/render-material","payloadVersion":1,"baseColour":[0.7,0.7,0.7,1],"roughness":1,"metallic":0,"alphaCutoff":0,"albedo":null,"normal":null,"roughnessMap":null}
        """));
        void Asset(string id, Vector3 min, Vector3 max)
        {
            Resource(id + "-mesh", "Mesh", BoxMesh(min, max));
            JsonObject Bounds() => new()
            {
                ["min"] = new JsonObject { ["x"] = min.X, ["y"] = min.Y, ["z"] = min.Z },
                ["max"] = new JsonObject { ["x"] = max.X, ["y"] = max.Y, ["z"] = max.Z }
            };
            assets.Add(new JsonObject
            {
                ["id"] = id,
                ["meshResourceId"] = id + "-mesh",
                ["supportResourceIds"] = new JsonArray(),
                ["materialResourceIds"] = new JsonArray(),
                ["lodResourceIds"] = new JsonArray(),
                ["lightResourceIds"] = new JsonArray(),
                ["sourceUnitsToMetres"] = 1,
                ["renderBounds"] = Bounds(),
                ["lodBounds"] = null,
                ["source"] = "invented.blend",
                ["license"] = "CC0",
                ["textured"] = false
            });
        }
        Asset("sun", new(-1, 0, -1), new(1, 2, 1)); Asset("point", new(-.5f, 0, -.5f), new(.5f, 1.25f, .5f));
        var root = source.Add("root", Encoding.UTF8.GetBytes(new JsonObject { ["payloadVersion"] = 1, ["assets"] = assets, ["resources"] = resources }.ToJsonString(MapJsonTextOptions.CreateSerializer(false))));
        Write(root, source.Read(root).ToArray());
        return MapAssetClosure.Load([root], source);
    }
    static byte[] BoxMesh(Vector3 min, Vector3 max)
    {
        Vector3 P(int bits) => new((bits & 1) == 0 ? min.X : max.X, (bits & 2) == 0 ? min.Y : max.Y, (bits & 4) == 0 ? min.Z : max.Z);
        int[] indices = [0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5];
        using var buffer = new MemoryStream(); using var writer = new BinaryWriter(buffer);
        foreach (int i in indices) { var p = P(i); writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); }
        for (int i = 0; i < indices.Length; i += 3)
        {
            var n = Vector3.Normalize(Vector3.Cross(P(indices[i + 1]) - P(indices[i]), P(indices[i + 2]) - P(indices[i])));
            for (int k = 0; k < 3; k++) { writer.Write(n.X); writer.Write(n.Y); writer.Write(n.Z); }
        }
        for (uint i = 0; i < indices.Length; i++) writer.Write(i);
        var json = JsonNode.Parse("""
        {"asset":{"version":"2.0"},"buffers":[{"byteLength":1008}],"bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":432},{"buffer":0,"byteOffset":432,"byteLength":432},{"buffer":0,"byteOffset":864,"byteLength":144}],"accessors":[{"bufferView":0,"componentType":5126,"count":36,"type":"VEC3","min":[0,0,0],"max":[1,1,1]},{"bufferView":1,"componentType":5126,"count":36,"type":"VEC3"},{"bufferView":2,"componentType":5125,"count":36,"type":"SCALAR"}],"meshes":[{"primitives":[{"attributes":{"POSITION":0,"NORMAL":1},"indices":2}]}],"nodes":[{"mesh":0}],"scenes":[{"nodes":[0]}],"scene":0}
        """)!;
        json["accessors"]![0]!["min"] = new JsonArray(min.X, min.Y, min.Z); json["accessors"]![0]!["max"] = new JsonArray(max.X, max.Y, max.Z);
        byte[] text = Encoding.UTF8.GetBytes(json.ToJsonString(MapJsonTextOptions.CreateSerializer(false))); int padded = (text.Length + 3) & ~3;
        using var glb = new MemoryStream(); using var output = new BinaryWriter(glb);
        output.Write(0x46546c67); output.Write(2); output.Write(12 + 8 + padded + 8 + (int)buffer.Length);
        output.Write(padded); output.Write(0x4e4f534a); output.Write(text); for (int i = text.Length; i < padded; i++) output.Write((byte)32);
        output.Write((int)buffer.Length); output.Write(0x004e4942); output.Write(buffer.ToArray()); return glb.ToArray();
    }
}
