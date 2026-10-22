using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc.Foliage;

internal sealed record FarFoliageFixture(MapDocument Document, MapAssetClosure Assets);

/// <summary>Invented sparse inputs. Each location generates independently on its original R7 world lattice.</summary>
internal static class FarFoliageGenerationFixtures
{
    internal const string Layer = "far-cover";
    internal static FarFoliageFixture Create(int x, int z, MapAssetClosure? assets = null, int height = 0)
    {
        assets ??= Assets();
        var document = new MapDocument
        {
            Id = "invented-far-generation",
            TileSize = 64,
            Bounds = new() { MinX = -32192, MinZ = -32192, MaxX = 32192, MaxZ = 32192 },
            PlayableBounds = new() { MinX = -32192, MinZ = -32192, MaxX = 32192, MaxZ = 32192 },
            ResolverIdentity = new(1, 2),
            SupportRecipe = MapSupportRecipe.AuthoredBindingsV2,
            NativeAssets = assets.Roots.ToList()
        };
        var patch = new MapSurfacePatch
        {
            Key = new("far-floor", 0, 0),
            Width = 8,
            Depth = 8,
            Heights = Enumerable.Repeat(height * 100, 81).ToArray(),
            Cells = Enumerable.Repeat(new MapSurfaceCell(10, 0, MapOverlayCut.Full, 0, MapCellFlags.None,
                MapCellTopology.ForceSwNe), 64).ToArray(),
            Presence = [ulong.MaxValue]
        };
        var surface = new MapSurfaceRef("far-floor", MapLatticeFrame.ImportedMetreCentimetre with
        { RowDirection = MapRowDirection.PositiveZ }, MapSurfaceRole.SupportFloor, MapPresencePolicy.Native, null, null, "");
        document.Surfaces.Patches.Add(patch.Key, patch);
        document.Surfaces.Refs.Add(surface with
        {
            SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
            [KeyValuePair.Create(patch.Key, MapSurfaceSemantics.PatchDigest(patch))])
        });
        document.NativeResourceTransforms = [new(MapPrefabChildKind.Surface, "far-floor", new(x - 4, 0, z - 4, 0, 1), new string('a', 64))];
        var density = new byte[1024];
        for (int row = 0; row < 4; row++)
            for (int col = 0; col < 4; col++) density[row * 32 + col] = (byte)(col % 2 == 0 ? 255 : 180);
        document.Foliage = new(1, [new(1, Layer, new(MapSupportBindingKind.Surface, "far-floor", null, null, null, null),
            x - 2, z - 2, 1, 4, 4, MapRowDirection.PositiveZ, 17, 1, .8f, 1.4f, -.01f,
            [new("A", 1), new("B", 1)], [10], [new(10, "paint", MapFoliageMaterialKind.Ordinary)],
            new("legacy-tile", 1, 0), false, false, 0, 0, [new("far-floor", 0)], [], [], [], [new(new(0, 0), density)])]);
        return new(document, assets);
    }

    internal static MapFoliageSampleBatch Generate(FarFoliageFixture fixture)
    {
        var context = MapFoliageGenerationContext.Capture(fixture.Document, fixture.Assets);
        if (context.Status != MapFoliageStatus.Complete) throw new InvalidOperationException("Far generation refused: " + context.Status);
        return MapFoliageDistribution.Generate(context.Context!, Layer, new(0, 0));
    }

    internal static IEnumerable<int> MatrixBits(Matrix4x4 m) => new[] { m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 }.Select(BitConverter.SingleToInt32Bits);

    // Filled once from released 5d896b333, never derived from a window constructor or translated samples.
    internal static string Pins(int x) => x switch
    {
        0 => """
            far-cover|far-floor|-1|-1|1|legacy-tile|1|B|3f9ac879,00000000,bebb66d8,00000000,00000000,3fa1b7db,00000000,00000000,3ebb66d8,00000000,3f9ac879,00000000,bf1c9a6e,bc23d70a,bf98a912,3f800000|3edea1aa
            far-cover|far-floor|0|-1|1|legacy-tile|1|B|bf0af4e6,00000000,3f16906e,00000000,00000000,3f4ce2f8,00000000,00000000,bf16906e,00000000,bf0af4e6,00000000,3eec9984,bc23d70a,bf69e509,3f800000|3f302524
            far-cover|far-floor|1|-1|1|legacy-tile|1|A|3d8ae971,00000000,3fac5a08,00000000,00000000,3fac91fa,00000000,00000000,bfac5a08,00000000,3d8ae971,00000000,3f7c53a8,bc23d70a,bf61358a,3f800000|3e87dc72
            far-cover|far-floor|-1|0|1|legacy-tile|1|A|bf94aedb,00000000,3e63d39e,00000000,00000000,3f9762c3,00000000,00000000,be63d39e,00000000,bf94aedb,00000000,bf96143b,bc23d70a,3d50bcd0,3f800000|3e9aaf14
            far-cover|far-floor|0|0|1|legacy-tile|1|A|bdd8a351,00000000,bf591164,00000000,00000000,3f5ac024,00000000,00000000,3f591164,00000000,bdd8a351,00000000,3d3330f0,bc23d70a,bdb1d430,3f800000|3ee92e68
            far-cover|far-floor|1|0|1|legacy-tile|1|A|3f3a8d57,00000000,3f038f43,00000000,00000000,3f64467c,00000000,00000000,bf038f43,00000000,3f3a8d57,00000000,3f0fb21e,bc23d70a,3e68702c,3f800000|3ed9b536
            far-cover|far-floor|-2|1|1|legacy-tile|1|B|bfa92b90,00000000,3e1fd957,00000000,00000000,3faa589a,00000000,00000000,be1fd957,00000000,bfa92b90,00000000,bfd8a5d6,bc23d70a,3f300c52,3f800000|3f7a6bcf
            """,
        32000 => """
            far-cover|far-floor|31998|-32001|1|legacy-tile|1|B|bf00852a,00000000,bf6fca27,00000000,00000000,3f8807a6,00000000,00000000,3f6fca27,00000000,bf00852a,00000000,46f9fcbe,bc23d70a,c6fa0174,3f800000|3e11eeac
            far-cover|far-floor|31999|-32001|1|legacy-tile|1|A|bfa0a321,00000000,3e4d3303,00000000,00000000,3fa2ac14,00000000,00000000,be4d3303,00000000,bfa0a321,00000000,46f9fe02,bc23d70a,c6fa0170,3f800000|3eadadba
            far-cover|far-floor|32000|-32001|1|legacy-tile|1|A|bf47d732,00000000,3f5f314f,00000000,00000000,3f95cae3,00000000,00000000,bf5f314f,00000000,bf47d732,00000000,46fa0018,bc23d70a,c6fa029a,3f800000|3f29c47f
            far-cover|far-floor|31999|-32000|1|legacy-tile|1|A|3ef734bb,00000000,bf508aec,00000000,00000000,3f726bac,00000000,00000000,3f508aec,00000000,3ef734bb,00000000,46f9fe94,bc23d70a,c6f9ff4d,3f800000|3f008a99
            far-cover|far-floor|32000|-32000|1|legacy-tile|1|B|3cf4ea3c,00000000,3f4cd17d,00000000,00000000,3f4cf615,00000000,00000000,bf4cd17d,00000000,3cf4ea3c,00000000,46f9ff57,bc23d70a,c6f9ff4d,3f800000|3f56b9be
            far-cover|far-floor|32001|-32000|1|legacy-tile|1|A|bf72fd9a,00000000,3e6e481b,00000000,00000000,3f7a2fa4,00000000,00000000,be6e481b,00000000,bf72fd9a,00000000,46fa0151,bc23d70a,c6fa0010,3f800000|3dea39c0
            far-cover|far-floor|31999|-31999|1|legacy-tile|1|B|3f491074,00000000,bef2e653,00000000,00000000,3f6ae5cd,00000000,00000000,3ef2e653,00000000,3f491074,00000000,46f9fec7,bc23d70a,c6f9fed1,3f800000|3f096097
            far-cover|far-floor|32000|-31999|1|legacy-tile|1|A|3f7e8d94,00000000,3f00ea3f,00000000,00000000,3f8eaaf1,00000000,00000000,bf00ea3f,00000000,3f7e8d94,00000000,46fa007d,bc23d70a,c6f9fe2c,3f800000|3f74e04c
            """,
        -32000 => """
            far-cover|far-floor|-32002|31998|1|legacy-tile|1|A|3d3e5b9e,00000000,3f9a88f8,00000000,00000000,3f9aa645,00000000,00000000,bf9a88f8,00000000,3d3e5b9e,00000000,c6fa03f2,bc23d70a,46f9fcc3,3f800000|3e0ccfb8
            far-cover|far-floor|-32001|31998|1|legacy-tile|1|A|bf5d63a8,00000000,3cd64253,00000000,00000000,3f5d7d92,00000000,00000000,bcd64253,00000000,bf5d63a8,00000000,c6fa0141,bc23d70a,46f9fcd7,3f800000|3e2d1438
            far-cover|far-floor|-32002|31999|1|legacy-tile|1|A|3f86048c,00000000,3ed45b43,00000000,00000000,3f902665,00000000,00000000,bed45b43,00000000,3f86048c,00000000,c6fa0305,bc23d70a,46f9fe0f,3f800000|3f442c2a
            far-cover|far-floor|-32001|31999|1|legacy-tile|1|B|bf8ba6fc,00000000,bf242f99,00000000,00000000,3fa1fe6e,00000000,00000000,3f242f99,00000000,bf8ba6fc,00000000,c6fa0224,bc23d70a,46f9fe92,3f800000|3e8c5888
            far-cover|far-floor|-32000|31999|1|legacy-tile|1|B|bf0dc1e6,00000000,3f9af0d1,00000000,00000000,3faa6214,00000000,00000000,bf9af0d1,00000000,bf0dc1e6,00000000,c6f9ff19,bc23d70a,46f9fdca,3f800000|3f27ee1e
            far-cover|far-floor|-32000|32000|1|legacy-tile|1|A|3facc2be,00000000,bc87cb57,00000000,00000000,3facc614,00000000,00000000,3c87cb57,00000000,3facc2be,00000000,c6f9ff7f,bc23d70a,46fa00f1,3f800000|3a307000
            far-cover|far-floor|-32001|32001|1|legacy-tile|1|A|bf0a56ec,00000000,3f3ec51e,00000000,00000000,3f6ba674,00000000,00000000,bf3ec51e,00000000,bf0a56ec,00000000,c6fa0175,bc23d70a,46fa01b4,3f800000|3ec80a1a
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(x))
    };

    internal static string Capture(MapFoliageSampleBatch batch) => string.Join("\n", batch.Samples.Select(s =>
        $"{s.Key.Address.LayerId}|{s.Key.Address.SupportOwnerId}|{s.Key.Address.X}|{s.Key.Address.Z}|{s.Key.DistributionVersion}|{s.Key.Policy.Id}|{s.Key.Policy.Version}|{s.AssetId}|" +
        string.Join(",", MatrixBits(s.Transform).Select(b => b.ToString("x8", System.Globalization.CultureInfo.InvariantCulture))) +
        "|" + BitConverter.SingleToInt32Bits(s.ThinningRank).ToString("x8", System.Globalization.CultureInfo.InvariantCulture)));

    static MapAssetClosure Assets()
    {
        var source = new MemorySource(); var resources = new JsonArray(); var models = new JsonArray();
        foreach (string id in new[] { "mesh", "paint" })
        {
            var reference = source.Add(id, Encoding.UTF8.GetBytes("invented:" + id));
            resources.Add(new JsonObject { ["reference"] = Ref(reference), ["kind"] = id == "mesh" ? "Mesh" : "Material", ["dependencies"] = new JsonArray() });
        }
        foreach (string id in new[] { "A", "B" }) models.Add(new JsonObject
        {
            ["id"] = id,
            ["meshResourceId"] = "mesh",
            ["supportResourceIds"] = new JsonArray(),
            ["materialResourceIds"] = new JsonArray("paint"),
            ["lodResourceIds"] = new JsonArray(),
            ["lightResourceIds"] = new JsonArray(),
            ["sourceUnitsToMetres"] = 1,
            ["renderBounds"] = new JsonObject
            {
                ["min"] = new JsonObject { ["x"] = -1, ["y"] = -1, ["z"] = -1 },
                ["max"] = new JsonObject { ["x"] = 1, ["y"] = 1, ["z"] = 1 }
            },
            ["source"] = "invented.blend",
            ["license"] = "CC0",
            ["textured"] = false
        });
        var root = source.Add("root", Encoding.UTF8.GetBytes(new JsonObject
        {
            ["payloadVersion"] = 1,
            ["assets"] = models,
            ["resources"] = resources
        }.ToJsonString(MapJsonTextOptions.CreateSerializer(false))));
        return MapAssetClosure.Load([root], source);
    }
    static JsonObject Ref(MapAssetRef r) => new() { ["id"] = r.Id, ["path"] = r.Path, ["sha256"] = r.Sha256, ["payloadVersion"] = r.PayloadVersion };
    sealed class MemorySource : IMapAssetSource
    {
        readonly Dictionary<string, byte[]> _bytes = new();
        internal MapAssetRef Add(string id, byte[] bytes)
        { _bytes.Add(id, bytes); return new(id, "assets/" + id, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), 1); }
        public ReadOnlyMemory<byte> Read(MapAssetRef reference) => _bytes[reference.Id];
    }
}
