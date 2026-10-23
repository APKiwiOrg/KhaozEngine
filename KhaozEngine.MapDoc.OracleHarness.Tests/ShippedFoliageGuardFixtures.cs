using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.MapDoc;
using KhaozEngine.Tests.MapDocCompatibility;
using KhaozEngine.TileWorld;

namespace KhaozEngine.Tests.MapDocOracle;

/// <summary>One invented private foliage extraction in its own secure temporary directory. Only invented values are
/// exposed, and disposal removes every file the fixture wrote.</summary>
[UnsupportedOSPlatform("windows")]
internal sealed class ShippedFoliageGuardFixture : IDisposable
{
    readonly string _root;
    internal IReadOnlyDictionary<string, string?> Environment { get; }
    internal string PublicSecretMarker => ShippedFoliageGuardFixtures.Sentinel;
    internal ShippedFoliageGuardFixture(string root, Dictionary<string, string?> environment)
    { _root = root; Environment = environment; }
    public void Dispose() => Directory.Delete(_root, true);
}

/// <summary>Invented source worlds, native scaffolds and correspondence manifests for the private foliage contract. Two
/// regions carry a relief, dirt, rock, water, an indoor block, a road cut and owners on two planes: same-asset and
/// same-tag roofs and solids seated higher and level with the plane 0 layer, a higher roof that is also solid and has an
/// explicit-height native owner, and doors on both planes. Nothing here reads a committed file.</summary>
[UnsupportedOSPlatform("windows")]
internal static class ShippedFoliageGuardFixtures
{
    internal const string Sentinel = "PUBLIC-INVENTED-FOLIAGE-SECRET";
    internal const string Cover = "cover-a", Upper = "upper-a", Ground = "plane-0", Deck = "plane-1";
    internal const ushort Grass = 1, Dirt = 2, Rock = 3, Water = 4, Road = 6;
    // Source object ids in creation order: three trees, two canopies, two roof slabs, two doorways and one lamp.
    internal const long LevelTree = 1, FarTree = 2, HighTree = 3, HighCanopy = 4, LevelCanopy = 5, HighSlab = 6, LevelSlab = 7,
        LevelDoor = 8, HighDoor = 9, Lamp = 10;
    internal const int RimSeed = 1446451;

    internal static ShippedFoliageGuardFixture Create(string variant)
    {
        string root = Path.Combine(Path.GetTempPath(), "ke-foliage-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root, PrivateOracleFixtures.SecureDirectoryMode);
        try
        {
            JsonObject comparison = null!;
            var environment = PrivateOracleFixtures.SyntheticEnvironment(root, Sentinel,
                source => comparison = Write(source, variant), JsonSerializer.SerializeToElement("pending"));
            string provenancePath = environment["KHAOZ_R2_ORACLE_PROVENANCE"]!;
            var provenance = JsonNode.Parse(File.ReadAllText(provenancePath))!.AsObject();
            provenance["comparison"] = comparison;
            string sourceRoot = environment["KHAOZ_R2_SHIPPED_SOURCE"]!;
            if (variant.StartsWith("unattested-", StringComparison.Ordinal) && variant != "unattested-building")
            {
                string omitted = variant switch
                {
                    "unattested-region" => "world/regions/r_1_0.json",
                    "unattested-catalog" => "catalog.json",
                    "unattested-generator" => "generator/build-scaffold.py",
                    "unattested-helper" => "generator/helper.py",
                    "unattested-resource" => "assets/mesh",
                    "unattested-pack" => Directory.EnumerateFiles(Path.Combine(sourceRoot, "native-tiled"), "*", SearchOption.AllDirectories)
                        .Select(f => Path.GetRelativePath(sourceRoot, f).Replace('\\', '/')).Where(f => f.Contains("foliage", StringComparison.Ordinal))
                        .Order(StringComparer.Ordinal).First(),
                    _ => throw new ArgumentException("unknown public fixture"),
                };
                var paths = provenance["paths"]!.AsArray();
                paths.Remove(paths.Single(p => p!["path"]!.GetValue<string>() == omitted));
                File.Delete(Path.Combine(sourceRoot, omitted));
                var typed = JsonSerializer.Deserialize<ShippedSourceProvenance>(provenance.ToJsonString(), ShippedSourceProvenance.JsonOptions)!;
                provenance["aggregateSha256"] = typed.ComputeAggregate();
            }
            if (variant is "parent-link" or "final-link")
            {
                string path = Path.Combine(sourceRoot, "generator");
                if (variant == "parent-link")
                {
                    string target = Path.Combine(root, "moved-generator");
                    Directory.Move(path, target);
                    Directory.CreateSymbolicLink(path, target);
                }
                else
                {
                    path = Path.Combine(path, "helper.py");
                    string target = Path.Combine(root, "helper.py");
                    File.Move(path, target);
                    File.CreateSymbolicLink(path, target);
                }
            }
            string text = provenance.ToJsonString();
            if (variant == "duplicate-comparison") text = text.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal);
            if (variant == "case-comparison") text = text.Replace("\"schemaVersion\":1", "\"SchemaVersion\":1", StringComparison.Ordinal);
            File.WriteAllText(provenancePath, text);
            environment["KHAOZ_R2_ORACLE_PROVENANCE_SHA256"] = AssertFixtures.Sha256(provenancePath);
            if (variant == "wrong-digest") environment["KHAOZ_R2_ORACLE_PROVENANCE_SHA256"] = new string('0', 64);
            string? missing = variant switch
            {
                "missing-source" => "KHAOZ_R2_SHIPPED_SOURCE",
                "missing-provenance" => "KHAOZ_R2_ORACLE_PROVENANCE",
                "missing-digest" => "KHAOZ_R2_ORACLE_PROVENANCE_SHA256",
                "missing-report" => "KHAOZ_R2_ORACLE_REPORT",
                _ => null,
            };
            if (missing is not null) environment.Remove(missing);
            string report = Path.Combine(root, "out", "report.json");
            if (variant == "unsafe-report") File.SetUnixFileMode(Path.GetDirectoryName(report)!, PrivateOracleFixtures.SecureDirectoryMode | UnixFileMode.GroupRead);
            if (variant == "existing-report") File.WriteAllText(report, Sentinel);
            if (variant == "linked-report") File.CreateSymbolicLink(report, Path.Combine(root, "absent-report"));
            return new(root, environment);
        }
        catch { Directory.Delete(root, true); throw; }
    }

    static JsonObject Write(string root, string variant)
    {
        foreach (string directory in new[] { "world", "assets", "generator" }) Directory.CreateDirectory(Path.Combine(root, directory));
        (TileWorldDocument world, string catalog) = Source(variant);
        TileWorldFile.Save(world, Path.Combine(root, "world"));
        File.WriteAllText(Path.Combine(root, "catalog.json"), catalog);
        File.WriteAllText(Path.Combine(root, "generator/build-scaffold.py"), "# Invented public fixture generator contract\nfrom helper import identity\n");
        File.WriteAllText(Path.Combine(root, "generator/helper.py"), "def identity(value):\n    return value\n");
        File.WriteAllBytes(Path.Combine(root, "assets/public.mesh"), Encoding.UTF8.GetBytes("invented public mesh bytes"));
        MapAssetRef assets = ShippedFoliageGuardAssets.Write(root);
        TileWorldCatalogs catalogs = TileWorldCatalogs.LoadJson(catalog, "invented foliage catalog");

        MapDocument native = Native(world, catalogs, assets, variant);
        File.WriteAllText(Path.Combine(root, "native.json"), MapDocumentFile.SaveText(native));
        if (variant == "tiled-byte") native.Foliage = new(1, [With(native.Foliage!.Layers[0], chunks: Bumped(native.Foliage.Layers[0])), native.Foliage.Layers[1]]);
        MapDocumentFile.SaveTiled(native, Path.Combine(root, "native-tiled"));

        JsonObject manifest = Manifest(world, variant);
        string manifestText = manifest.ToJsonString();
        if (variant == "duplicate-manifest-field") manifestText = manifestText.Replace("\"nativeLayerId\":\"cover-a\"", "\"nativeLayerId\":\"cover-a\",\"nativeLayerId\":\"cover-a\"", StringComparison.Ordinal);
        if (variant == "case-manifest-field") manifestText = manifestText.Replace("\"nativeLayerId\":\"cover-a\"", "\"NativeLayerId\":\"cover-a\"", StringComparison.Ordinal);
        if (variant == "case-duplicate-manifest-field") manifestText = manifestText.Replace("\"nativeLayerId\":\"cover-a\"", "\"nativeLayerId\":\"cover-a\",\"NativeLayerId\":\"cover-a\"", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(root, "foliage.json"), manifestText);
        if (variant == "building-manifest") File.WriteAllText(Path.Combine(root, "building.json"), "{}");

        (long owned, long accepted) = LegacyCounts(world, catalogs);
        var comparison = new JsonObject
        {
            ["schemaVersion"] = variant == "unsupported-schema" ? 2 : 1,
            ["nativeDocumentPath"] = "native.json",
            ["nativeTiledManifestPath"] = "native-tiled/map.json",
            ["foliageManifestPath"] = variant == "escape-path" ? "../" + Sentinel : "foliage.json",
            ["buildingManifestPath"] = variant is "unattested-building" or "building-manifest" ? "building.json" : null,
            ["expectations"] = new JsonObject
            {
                ["layers"] = 2,
                ["rasterBytes"] = world.FoliageLayers.Sum(l => (long)l.Width * l.Height),
                ["storedChunks"] = world.FoliageLayers.Sum(l => ShippedFoliageComparison.SourceChunks(l).Count),
                ["sourceOwners"] = 9,
                ["models"] = 3,
                ["ownedCandidates"] = owned,
                ["accepted"] = variant == "expectation" ? accepted + 1 : accepted,
                ["rejected"] = variant == "negative-expectation" ? -1 : owned - accepted,
            },
        };
        return comparison;
    }

    /// <summary>The invented source world and its catalog JSON.</summary>
    static (TileWorldDocument World, string Catalog) Source(string variant)
    {
        var world = new TileWorldDocument { Id = "invented-foliage-world", DisplayName = "Invented foliage world", TileSize = variant == "tile-size" ? 2 : 1 };
        world.CatalogPaths.Add("../catalog.json");
        world.GetOrCreateRegion(new(0, 0));
        world.GetOrCreateRegion(new(1, 0));
        for (int z = 0; z < TileRegion.Size; z++)
            for (int x = 0; x < 2 * TileRegion.Size; x++) world.SetUnderlay(x, z, 0, Grass);
        Fill(24, 32, 28, 35, (x, z) => world.SetUnderlay(x, z, 0, Dirt));
        Fill(60, 36, 64, 40, (x, z) => world.SetUnderlay(x, z, 0, Rock));
        Fill(66, 44, 68, 46, (x, z) => world.SetUnderlay(x, z, 0, 0));
        Fill(32, 16, 35, 19, (x, z) => world.SetUnderlay(x, z, 0, Water));
        Fill(74, 40, 77, 42, (x, z) => world.SetSettings(x, z, 0, TileSettings.Indoors));
        world.SetSettings(80, 30, 0, TileSettings.NoDraw);
        world.SetOverlay(45, 44, 0, Road);
        world.SetOverlayShape(45, 44, 0, TileOverlayShape.DiagonalHalf);
        world.SetOverlayRotation(45, 44, 0, 1);
        // An invented relief inside region (0, 0), flat on every region rim.
        Fill(21, 21, 40, 40, (x, z) => world.SetCornerHeightCm(x, z, 0, (short)((x * 37 + z * 11) % 23 * 5 - 40)));
        Fill(44, 14, 60, 30, (x, z) => world.SetUnderlay(x, z, 1, Grass));
        Fill(57, 15, 59, 17, (x, z) => world.SetUnderlay(x, z, 1, Water));
        world.AddObject("tree", 30, 30, 0, 0);
        world.AddObject("tree", 70, 20, 0, 0);
        world.AddObject("tree", 48, 18, 1, 0);
        world.AddObject("canopy", 50, 20, 1, 0);
        world.AddObject("canopy", 36, 40, 0, 0);
        // A quarter turn: the shared slab rule's local 3 by 2 footprint covers 2 by 3 tiles through the owner's yaw.
        world.AddObject("roofslab", 52, 24, 1, 1, ["Cover"]);
        world.AddObject("roofslab", 26, 14, 0, 0, ["COVER"]);
        world.AddObject("doorway", 40, 26, 0, 0);
        world.AddObject("doorway", 56, 26, 1, 0);
        world.AddObject("lamp", 34, 22, 0, 0);
        // The plane 0 raster reaches north into the absent regions (0, 1) and (1, 1).
        world.SetFoliageLayer(new TileFoliageLayer(Sentinel, 0, 20, -70, 2, 36, 31, CoverBytes(), 23, 1f, 0.7f, 1.3f, -0.02f,
            [new TileFoliageArchetype("reed-a", 3f), new TileFoliageArchetype("reed-b", 1f)], [Grass, Dirt],
            excludeIndoors: true, excludeSolidObjects: true, doorClearance: 1.5f, edgeFade: 0.5f));
        // Seed 1446451 puts candidate (59, -15) exactly on world X 59, the west rim of tile (59, 15) beside plane 1 water,
        // so its released edge factor is exactly zero.
        world.SetFoliageLayer(new TileFoliageLayer("upper-source", 1, 44, -30, 1, 17, 17, UpperBytes(), RimSeed, 1f, 0.9f, 1.1f, 0f,
            [new TileFoliageArchetype("reed-b", 1f)], null, excludeIndoors: true, excludeSolidObjects: true, doorClearance: 1f, edgeFade: 0.5f));
        const string Mesh = "\"meshRef\":\"assets/public.mesh\"";
        string catalog = "{\"materials\":[" +
            "{\"id\":1,\"name\":\"Invented grass\",\"color\":\"#3a7a3a\"},{\"id\":2,\"name\":\"Invented dirt\",\"color\":\"#7a5a3a\"}," +
            "{\"id\":3,\"name\":\"Invented rock\",\"color\":\"#777777\"},{\"id\":4,\"name\":\"Invented water\",\"color\":\"#3a5a9a\",\"kind\":\"Water\"}," +
            "{\"id\":6,\"name\":\"Invented road\",\"color\":\"#999988\"}],\"archetypes\":[" +
            "{\"id\":\"reed-a\",\"name\":\"Reed A\"," + Mesh + "},{\"id\":\"reed-b\",\"name\":\"Reed B\"," + Mesh + "}," +
            "{\"id\":\"tree\",\"name\":\"Tree\"," + Mesh + ",\"collisionKind\":\"Solid\"}," +
            "{\"id\":\"canopy\",\"name\":\"Canopy\"," + Mesh + ",\"sizeX\":2,\"sizeZ\":3,\"collisionKind\":\"Solid\",\"isRoof\":true}," +
            "{\"id\":\"roofslab\",\"name\":\"Roof slab\"," + Mesh + ",\"sizeX\":3,\"sizeZ\":2,\"isRoof\":true}," +
            "{\"id\":\"doorway\",\"name\":\"Doorway\"," + Mesh + ",\"tags\":[\"Door\"]}," +
            "{\"id\":\"lamp\",\"name\":\"Lamp\"," + Mesh + "}]}";
        return (world, catalog);
    }

    // Varied nonzero bytes with one zero block, positive world Z rows.
    static byte[] CoverBytes()
    {
        var bytes = new byte[36 * 31];
        for (int z = 0; z < 31; z++)
            for (int x = 0; x < 36; x++) bytes[z * 36 + x] = x is >= 4 and <= 6 && z is >= 4 and <= 6 ? (byte)0 : (byte)((x * 29 + z * 17) % 200 + 40);
        return bytes;
    }

    static byte[] UpperBytes()
    {
        var bytes = Enumerable.Repeat((byte)180, 17 * 17).ToArray();
        for (int z = 5; z <= 7; z++)
            for (int x = 5; x <= 7; x++) bytes[z * 17 + x] = 0;
        return bytes;
    }

    /// <summary>The native scaffold: the released terrain conversion of both planes, one placement per source owner at its
    /// footprint centre, and both layers converted with their rules, ordinals and the explicit-height owner's seat.</summary>
    static MapDocument Native(TileWorldDocument world, TileWorldCatalogs catalog, MapAssetRef assets, string variant)
    {
        MapDocument document = MapDocumentFile.LoadText("""
            {"formatVersion":9,"id":"invented-foliage-native","tileSize":16,
             "bounds":{"minX":-64,"minZ":-128,"maxX":192,"maxZ":64},
             "playableBounds":{"minX":-64,"minZ":-128,"maxX":192,"maxZ":64},"terrain":{},
             "resolverIdentity":{"payloadVersion":1,"resolverVersion":2},"supportRecipe":"AuthoredBindingsV2"}
            """);
        document.NativeAssets.Add(assets);
        foreach (var (region, plane) in LegacyOracleConverter.RegionPlanes(world))
        {
            var converted = LegacyOracleConverter.ToNative(world, region, plane);
            document.Surfaces.Patches.Add(converted.Patch.Key, converted.Patch);
            if (!document.Surfaces.Refs.Any(s => s.Id == converted.Surface.Id)) document.Surfaces.Refs.Add(converted.Surface);
        }
        void Cell(int x, int z, Func<MapSurfaceCell, MapSurfaceCell> change)
        {
            var patch = document.Surfaces.Patches[new(Ground, x / TileRegion.Size, z / TileRegion.Size)];
            int index = z % TileRegion.Size * TileRegion.Size + x % TileRegion.Size;
            patch.Cells[index] = change(patch.Cells[index]);
        }
        if (variant == "patch-cells") Cell(50, 50, c => c with { Underlay = Dirt });
        for (int i = 0; i < document.Surfaces.Refs.Count; i++)
        {
            var surface = document.Surfaces.Refs[i];
            document.Surfaces.Refs[i] = surface with
            {
                SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface, document.Surfaces.Patches.Values.Where(p => p.Key.SurfaceId == surface.Id)
                    .Select(p => new KeyValuePair<MapPatchKey, string>(p.Key, MapSurfaceSemantics.PatchDigest(p))))
            };
        }
        foreach (TileObject obj in world.AllObjects().Where(o => o.ArchetypeId != "lamp").OrderBy(o => o.Id))
        {
            TileRect footprint = TileFootprint.Of(catalog.Archetype(obj.ArchetypeId)!, obj.X, obj.Z, obj.Rotation);
            bool explicitY = obj.Id == HighCanopy;
            string seat = obj.Plane == 0 || variant == "seat-lower" && obj.Id == HighTree ? Ground : Deck;
            document.Placements.Add(new MapPlacement
            {
                Id = OwnerId(obj.Id),
                Kind = "native-" + obj.ArchetypeId,
                AssetId = "native-" + obj.ArchetypeId,
                X = footprint.X + footprint.Width * 0.5f,
                Z = -(footprint.Z + footprint.Height * 0.5f),
                Y = explicitY ? world.CornerHeightCm(obj.X, obj.Z, obj.Plane) / 100f : null,
                Yaw = obj.Rotation * MathF.PI / 2,
                Scale = 1,
                Tags = (obj.Tags ?? []).ToList(),
                SupportBinding = explicitY ? null : new(MapSupportBindingKind.Surface, seat, null, null, null, null),
            });
        }
        MapFoliageCompatibilityOrdinal[] ordinals = variant switch
        {
            "table-wrong-ordinal" => [new(Ground, 0), new(Deck, 0)],
            "table-missing-row" => [new(Ground, 0)],
            "table-extra-row" => [new(Ground, 0), new(Deck, 1), new("plane-2", 2)],
            _ => [new(Ground, 0), new(Deck, 1)],
        };
        var explicitOwner = new MapPrefabReference(MapPrefabReferenceScope.World, MapPrefabChildKind.Placement, OwnerId(HighCanopy));
        MapFoliageOwnerSupport[] owners = [new(explicitOwner, Surface(variant == "explicit-lower" ? Ground : Deck))];
        MapFoliageRect unit = new(-0.5f, -0.5f, 0.5f, 0.5f), canopy = new(-1, -1.5f, 1, 1.5f), slab = new(-1.5f, -1, 1.5f, 1);
        MapFoliageClearanceRule[] coverRules =
        [
            new("cover-solid-tree", "native-tree", null, MapFoliageClearanceKind.Solid, variant == "rule-footprint" ? new(-1.5f, -0.5f, 0.5f, 0.5f) : unit),
            new("cover-solid-canopy", "native-canopy", null, MapFoliageClearanceKind.Solid, canopy),
            new("cover-roof-canopy", "native-canopy", null, MapFoliageClearanceKind.RoofAbove, canopy),
            new("cover-roof-slab", "native-roofslab", "cover", MapFoliageClearanceKind.RoofAbove, slab),
            new("cover-door", "native-doorway", null, variant == "rule-kind" ? MapFoliageClearanceKind.Solid : MapFoliageClearanceKind.Door, unit),
        ];
        MapFoliageClearanceRule[] upperRules =
        [
            new("upper-solid-tree", "native-tree", null, MapFoliageClearanceKind.Solid, unit),
            new("upper-solid-canopy", "native-canopy", null, MapFoliageClearanceKind.Solid, canopy),
            new("upper-door", "native-doorway", null, MapFoliageClearanceKind.Door, unit),
        ];
        TileFoliageLayer coverSource = world.FoliageLayers[0], upperSource = world.FoliageLayers[1];
        MapFoliageLayer cover = Convert(coverSource, Cover, Ground, ["native-reed-a", "native-reed-b"], ordinals, coverRules, owners, variant);
        MapFoliageLayer upper = Convert(upperSource, Upper, Deck, ["native-reed-b"], ordinals, upperRules, owners, variant);
        cover = variant switch
        {
            "changed-byte" => With(cover, chunks: Bumped(cover)),
            "changed-decision" => With(cover, door: 2.5f),
            "extra-sample" => With(cover, allowed: [Grass, Dirt, Rock]),
            "wrong-seed" => With(cover, seed: cover.Seed + 1),
            "wrong-weight" => With(cover, models: [new("native-reed-a", 1), new("native-reed-b", 3)]),
            "missing-chunk" => With(cover, chunks: cover.Chunks.Where(c => c.Key != new MapFoliageChunkKey(1, 0)).ToArray()),
            _ => cover,
        };
        document.Foliage = new(1, [cover, upper]);
        return document;
    }

    static MapFoliageLayer Convert(TileFoliageLayer source, string id, string surface, string[] models,
        MapFoliageCompatibilityOrdinal[] ordinals, MapFoliageClearanceRule[] rules, MapFoliageOwnerSupport[] owners, string variant)
    {
        var chunks = new List<MapFoliageChunk>();
        foreach (MapFoliageChunkKey key in ShippedFoliageComparison.SourceChunks(source).OrderBy(k => k.Z).ThenBy(k => k.X))
        {
            var data = new byte[MapFoliageChunk.ByteLength];
            for (int z = 0; z < MapFoliageChunk.Size; z++)
                for (int x = 0; x < MapFoliageChunk.Size; x++)
                {
                    int sx = key.X * MapFoliageChunk.Size + x, sz = key.Z * MapFoliageChunk.Size + z;
                    if (sx < source.Width && sz < source.Height) data[z * MapFoliageChunk.Size + x] = source.DensityAt(sx, sz);
                }
            chunks.Add(new(key, data));
        }
        return new MapFoliageLayer(1, id, Surface(surface), source.OriginX, source.OriginZ, source.CellSize, source.Width, source.Height,
            MapRowDirection.PositiveZ, source.Seed, source.Spacing, source.ScaleMin, source.ScaleMax, source.RootOffset,
            source.Archetypes.Select((a, i) => new MapFoliageModel(models[i], a.Weight)).ToArray(), source.AllowedUnderlays.ToArray(),
            new MapFoliageMaterial[]
            {
                new(Grass, "grass-material", MapFoliageMaterialKind.Ordinary), new(Dirt, "dirt-material", MapFoliageMaterialKind.Ordinary),
                new(Rock, "rock-material", MapFoliageMaterialKind.Ordinary),
                new(Water, "water-material", variant == "material-kind" ? MapFoliageMaterialKind.Ordinary : MapFoliageMaterialKind.Water),
                new(Road, "road-material", MapFoliageMaterialKind.Ordinary),
            }.Where(m => !(variant == "missing-material" && m.Id == Rock)).ToArray(),
            new("legacy-tile", 1, 0), source.ExcludeIndoors, source.ExcludeSolidObjects, source.DoorClearance, source.EdgeFade,
            ordinals, rules, [], owners, chunks);
    }

    static MapFoliageLayer With(MapFoliageLayer l, int? seed = null, IReadOnlyList<MapFoliageModel>? models = null,
        IReadOnlyList<MapFoliageChunk>? chunks = null, float? door = null, ushort[]? allowed = null) =>
        new(1, l.Id, l.Support, l.OriginX, l.OriginZ, l.CellSize, l.Width, l.Depth, l.RowDirection, seed ?? l.Seed, l.Spacing, l.ScaleMin,
            l.ScaleMax, l.RootOffset, models ?? l.Models, allowed ?? l.AllowedVisibleMaterials, l.Materials, l.Policy, l.ExcludeIndoors,
            l.ExcludeSolidObjects, door ?? l.DoorClearance, l.EdgeFade, l.CompatibilityOrdinals, l.ClearanceRules, l.ExceptionMasks, l.OwnerSupports,
            chunks ?? l.Chunks);

    // One changed density byte, at raster sample (14, 9), in the middle of open grass.
    static MapFoliageChunk[] Bumped(MapFoliageLayer layer) => layer.Chunks.Select(c =>
    {
        if (c.Key != new MapFoliageChunkKey(0, 0)) return c;
        byte[] bytes = c.CopyDensity();
        bytes[9 * MapFoliageChunk.Size + 14] = (byte)(bytes[9 * MapFoliageChunk.Size + 14] ^ 0x40);
        return new MapFoliageChunk(c.Key, bytes);
    }).ToArray();

    /// <summary>The correspondence manifest the retained generator would write for the invented extraction.</summary>
    static JsonObject Manifest(TileWorldDocument world, string variant)
    {
        JsonObject Binding(string surface) => new() { ["kind"] = "Surface", ["surfaceId"] = surface };
        JsonArray Rows(params (int Plane, string Surface)[] rows) => new(rows.Select(r => (JsonNode)new JsonObject
        { ["sourcePlane"] = r.Plane, ["nativeSurfaceId"] = r.Surface }).ToArray());
        JsonArray Materials() => new(new (ushort Id, string Resource, string Kind)[]
        {
            (Grass, "grass-material", "Ordinary"), (Dirt, "dirt-material", "Ordinary"), (Rock, "rock-material", "Ordinary"),
            (Water, "water-material", variant == "material-kind" ? "Ordinary" : "Water"), (Road, "road-material", "Ordinary"),
        }.Where(m => !(variant == "missing-material" && m.Id == Rock))
            .Select(m => (JsonNode)new JsonObject { ["sourceMaterialId"] = m.Id, ["nativeResourceId"] = m.Resource, ["kind"] = m.Kind }).ToArray());
        JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
        JsonArray Models(params (string Source, string Native)[] models) => new(models.Select(m => (JsonNode)new JsonObject
        { ["sourceArchetypeId"] = m.Source, ["nativeAssetId"] = m.Native }).ToArray());

        // A missing owner row is also missing from the layers, so only the owner correspondence is incomplete.
        long dropped = variant == "missing-owner" ? LevelTree : 0;
        long[] owners = world.AllObjects().Where(o => o.ArchetypeId != "lamp" && o.Id != dropped).Select(o => o.Id).Order().ToArray();
        long[] upperOwners = world.AllObjects().Where(o => o.ArchetypeId is "tree" or "canopy" or "doorway" && o.Id != dropped)
            .Select(o => o.Id).Order().ToArray();
        string[] coverRules = ["cover-solid-tree", "cover-solid-canopy", "cover-roof-canopy", "cover-roof-slab", "cover-door"];
        var cover = new JsonObject
        {
            ["sourceLayerId"] = Sentinel,
            ["nativeLayerId"] = Cover,
            ["support"] = variant == "space-binding"
                ? new JsonObject { ["kind"] = "Space", ["spaceId"] = "exterior", ["referenceY"] = 0, ["searchBelow"] = 1, ["searchAbove"] = 1 }
                : Binding(Ground),
            ["compatibilitySupports"] = variant == "manifest-missing-row" ? Rows((0, Ground)) : Rows((0, Ground), (1, Deck)),
            ["models"] = variant == "model-row" ? Models(("reed-a", "native-reed-b"), ("reed-b", "native-reed-a"))
                : Models(("reed-a", "native-reed-a"), ("reed-b", "native-reed-b")),
            ["materials"] = Materials(),
            ["ruleIds"] = Strings(variant == "unknown-rule" ? coverRules.Skip(1) : coverRules),
            ["maskIds"] = new JsonArray(),
            ["ownerIds"] = Strings(owners.Append(variant == "dangling-layer-owner" ? 999 : owners[0]).Distinct()
                .Select(o => o.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            ["candidateConvention"] = variant == "wrong-convention" ? "world-lattice-v2" : ShippedFoliageManifest.CandidateConvention,
        };
        if (variant == "obsolete-rule-order") cover["ruleOrder"] = Strings(coverRules);
        var upper = new JsonObject
        {
            ["sourceLayerId"] = "upper-source",
            ["nativeLayerId"] = Upper,
            ["support"] = Binding(Deck),
            ["compatibilitySupports"] = Rows((0, Ground), (1, Deck)),
            ["models"] = Models(("reed-b", "native-reed-b")),
            ["materials"] = Materials(),
            ["ruleIds"] = Strings(["upper-solid-tree", "upper-solid-canopy", "upper-door"]),
            ["maskIds"] = new JsonArray(),
            ["ownerIds"] = Strings(upperOwners.Select(o => o.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            ["candidateConvention"] = ShippedFoliageManifest.CandidateConvention,
        };
        var ownerRows = new JsonArray();
        foreach (TileObject obj in world.AllObjects().Where(o => o.ArchetypeId != "lamp").OrderBy(o => o.Id))
        {
            string[] rules = obj.ArchetypeId switch
            {
                "tree" => ["cover-solid-tree", "upper-solid-tree"],
                "canopy" => ["cover-solid-canopy", "cover-roof-canopy", "upper-solid-canopy"],
                "roofslab" => ["cover-roof-slab"],
                _ => ["cover-door", "upper-door"],
            };
            if (variant == "owner-rules" && obj.Id == LevelTree) rules = rules[..1];
            string[] targets = obj.Id switch
            {
                LevelTree or FarTree or LevelCanopy or HighSlab or LevelDoor => [Cover],
                HighTree or HighDoor => [Upper],
                HighCanopy => [Cover, Upper],
                _ => [],
            };
            if (variant == "target-peer-roof" && obj.Id == LevelSlab) targets = [Cover];
            if (variant == "target-missing" && obj.Id == HighSlab) targets = [];
            string seated = obj.Plane == 0 || variant == "manifest-seat-lower" && obj.Id == HighTree ? Ground : Deck;
            ownerRows.Add(new JsonObject
            {
                ["sourceObjectId"] = obj.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["sourcePlane"] = obj.Plane,
                ["sourceArchetypeId"] = obj.ArchetypeId,
                ["nativeOwner"] = new JsonObject { ["scope"] = "World", ["kind"] = "Placement", ["id"] = OwnerId(obj.Id) },
                ["seatedSupport"] = Binding(seated),
                ["targets"] = new JsonArray(targets.Select(t => (JsonNode)new JsonObject
                { ["nativeLayerId"] = t, ["support"] = Binding(t == Cover ? Ground : Deck) }).ToArray()),
                ["ruleIds"] = Strings(rules),
                ["maskIds"] = new JsonArray(),
            });
        }
        if (variant == "missing-owner") ownerRows.RemoveAt(0);
        if (variant == "duplicate-owner") ownerRows.Add(ownerRows[0]!.DeepClone());
        if (variant == "extra-owner")
        {
            JsonNode extra = ownerRows[0]!.DeepClone();
            extra["sourceObjectId"] = Lamp.ToString(System.Globalization.CultureInfo.InvariantCulture);
            extra["nativeOwner"]!["id"] = OwnerId(Lamp);
            ownerRows.Add(extra);
        }
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["generator"] = new JsonObject
            {
                ["path"] = variant == "wrong-generator" ? "generator/helper.py" : ShippedFoliageManifest.GeneratorPath,
                ["helpers"] = variant == "duplicate-helper" ? Strings(["generator/helper.py", "generator/helper.py"]) : Strings(["generator/helper.py"]),
            },
            ["layers"] = new JsonArray(cover, upper),
            ["owners"] = ownerRows,
        };
    }

    // Owned candidates and accepted instances of the released cover build, from the public legacy sampler alone.
    static (long Owned, long Accepted) LegacyCounts(TileWorldDocument world, TileWorldCatalogs catalogs)
    {
        long owned = 0, accepted = 0;
        foreach (TileFoliageLayer layer in world.FoliageLayers)
        {
            var surface = new TileFoliageSurface(world, catalogs, layer);
            foreach (RegionCoord region in ComparedRegions(world))
                GroundCoverDistribution.GenerateTraced(ShippedFoliageComparison.Area(region), ShippedFoliageComparison.Settings(layer),
                    c => new GroundCoverSurfaceInput(surface.Sample(c.WorldX, c.WorldZ)), d =>
                    {
                        if (d.Kind == GroundCoverDecisionKind.OutsideArea) return;
                        owned++;
                        if (d.Kind == GroundCoverDecisionKind.Accepted) accepted++;
                    });
        }
        return (owned, accepted);
    }

    // Every source region, and every absent region a raster's closed sample rectangle reaches.
    static IEnumerable<RegionCoord> ComparedRegions(TileWorldDocument world)
    {
        var regions = world.Regions.Keys.ToHashSet();
        foreach (TileFoliageLayer layer in world.FoliageLayers)
        {
            float maxX = layer.OriginX + (layer.Width - 1) * layer.CellSize, maxZ = layer.OriginZ + (layer.Height - 1) * layer.CellSize;
            RegionCoord low = RegionCoord.Of((int)MathF.Floor(layer.OriginX), (int)MathF.Floor(-maxZ));
            RegionCoord high = RegionCoord.Of((int)MathF.Floor(maxX), (int)MathF.Floor(-layer.OriginZ));
            for (int rz = low.Rz; rz <= high.Rz; rz++)
                for (int rx = low.Rx; rx <= high.Rx; rx++) regions.Add(new(rx, rz));
        }
        return regions;
    }

    internal static string OwnerId(long sourceObjectId) => "owner-" + sourceObjectId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    static MapSupportBinding Surface(string id) => new(MapSupportBindingKind.Surface, id, null, null, null, null);

    static void Fill(int x0, int z0, int x1, int z1, Action<int, int> set)
    {
        for (int z = z0; z < z1; z++)
            for (int x = x0; x < x1; x++) set(x, z);
    }
}
