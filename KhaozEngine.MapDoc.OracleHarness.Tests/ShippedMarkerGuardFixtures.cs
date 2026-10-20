using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Tests.MapDoc;
using KhaozEngine.Tests.MapDocCompatibility;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
internal sealed class ShippedMarkerGuardFixture : IDisposable
{
    readonly string _root;
    internal IReadOnlyDictionary<string, string?> Environment { get; }
    internal string PublicSecretMarker => ShippedMarkerGuardFixtures.Sentinel;
    internal string SourceRoot => Environment["KHAOZ_R2_SHIPPED_SOURCE"]!;
    internal string ReportPath => Environment["KHAOZ_R2_ORACLE_REPORT"]!;
    internal ShippedMarkerGuardFixture(string root, Dictionary<string, string?> environment)
    { _root = root; Environment = environment; }
    public void Dispose() => Directory.Delete(_root, true);
}

[UnsupportedOSPlatform("windows")]
internal static class ShippedMarkerGuardFixtures
{
    internal const string Sentinel = "PUBLIC-INVENTED-MARKER-SECRET";
    internal const string PrefabMarkerId = "kit/kit-marker";
    internal const float TwistedTileX = 6.5f, TwistedTileZ = 5.5f;
    internal static readonly MapTransform KitFrame = new(new(-0.75f, 0, -4.25f), 0.371f, 1.2f);

    internal static ShippedMarkerGuardFixture Create(string variant)
    {
        string root = Path.Combine(Path.GetTempPath(), "ke-marker-guard-" + Guid.NewGuid().ToString("N"));
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
            if (variant.StartsWith("unattested-", StringComparison.Ordinal))
            {
                string omitted = variant switch
                {
                    "unattested-catalog" => "catalog.json",
                    "unattested-region" => "world/regions/r_0_0.json",
                    "unattested-generator" => "generator/build-scaffold.py",
                    "unattested-helper" => "generator/helper.py",
                    "unattested-resource" => "assets/public.mesh",
                    "unattested-prefab" => "definitions/platform.json",
                    _ => throw new ArgumentException("unknown public fixture"),
                };
                var paths = provenance["paths"]!.AsArray();
                paths.Remove(paths.Single(p => p!["path"]!.GetValue<string>() == omitted));
                File.Delete(Path.Combine(sourceRoot, omitted));
                var typed = JsonSerializer.Deserialize<ShippedSourceProvenance>(provenance.ToJsonString(), ShippedSourceProvenance.JsonOptions)!;
                provenance["aggregateSha256"] = typed.ComputeAggregate();
            }
            if (variant is "parent-link" or "final-link" or "dangling-link")
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
                    File.CreateSymbolicLink(path, variant == "final-link" ? target : Path.Combine(root, "absent"));
                }
            }
            string text = provenance.ToJsonString();
            if (variant == "duplicate-comparison") text = text.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal);
            if (variant == "case-comparison") text = text.Replace("\"schemaVersion\":1", "\"SchemaVersion\":1", StringComparison.Ordinal);
            File.WriteAllText(provenancePath, text);
            environment["KHAOZ_R2_ORACLE_PROVENANCE_SHA256"] = AssertFixtures.Sha256(provenancePath);
            if (variant == "wrong-digest") environment["KHAOZ_R2_ORACLE_PROVENANCE_SHA256"] = new string('0', 64);
            if (variant.StartsWith("missing-", StringComparison.Ordinal) && variant != "missing-support" && variant != "missing-role" && variant != "missing-marker-row")
            {
                string name = variant switch
                {
                    "missing-source" => "KHAOZ_R2_SHIPPED_SOURCE",
                    "missing-provenance" => "KHAOZ_R2_ORACLE_PROVENANCE",
                    "missing-digest" => "KHAOZ_R2_ORACLE_PROVENANCE_SHA256",
                    "missing-report" => "KHAOZ_R2_ORACLE_REPORT",
                    _ => throw new ArgumentException("unknown public missing input"),
                };
                environment.Remove(name);
            }
            string report = environment.GetValueOrDefault("KHAOZ_R2_ORACLE_REPORT") ?? Path.Combine(root, "out/report.json");
            if (variant == "unsafe-report") File.SetUnixFileMode(Path.GetDirectoryName(report)!, PrivateOracleFixtures.SecureDirectoryMode | UnixFileMode.GroupRead);
            if (variant == "existing-report") File.WriteAllText(report, Sentinel);
            if (variant == "linked-report") File.CreateSymbolicLink(report, Path.Combine(root, "absent-report"));
            return new(root, environment);
        }
        catch { Directory.Delete(root, true); throw; }
    }

    static JsonObject Write(string root, string variant)
    {
        JsonObject? building = variant == "building-manifest" ? CopyBuilding(root) : null;
        foreach (string directory in new[] { "world", "assets", "definitions", "generator" }) Directory.CreateDirectory(Path.Combine(root, directory));
        var world = building is null ? new TileWorldDocument { Id = "invented-marker-world" } : TileWorldFile.Load(Path.Combine(root, "world"));
        if (building is null) world.CatalogPaths.Add("../catalog.json");
        if (variant == "tile-size") world.TileSize = 2;
        world.GetOrCreateRegion(new(-1, 0)); world.GetOrCreateRegion(new(0, 0));
        for (int z = 0; z < 8; z++)
            for (int x = -4; x < 8; x++) world.SetUnderlay(x, z, 0, 1);
        // A twisted tile. Its centre lies on the drawn triangles' shared diagonal, a quarter metre off the bilinear centre.
        world.SetCornerHeightCm((int)TwistedTileX + 1, (int)TwistedTileZ + 1, 0, 100);
        var platform = world.AddObject("platform", 2, 2, 0, 0);
        var supportOwner = variant == "wrong-support-owner" ? world.AddObject("platform", 6, 6, 0, 0) : platform;
        string[] names = [Sentinel, "moth", "beacon", "caption", "sleeper", "caption-two"];
        int[] xs = [-2, 2, 4, 5, 3, 6], zs = [1, 2, 3, 4, 2, 5];
        for (int i = 0; i < names.Length; i++) world.SetMarker(names[i], xs[i], zs[i], variant == "upper-plane" && i == 4 ? 1 : 0, ["first", "second", "first"]);
        // The source point of the prefab-expanded marker.
        world.SetMarker("relay", 1, 6, 0, ["first", "second", "first"]);
        TileWorldFile.Save(world, Path.Combine(root, "world"), force: building is not null);
        const string Platform = """{"id":"platform","name":"Platform","meshRef":"assets/public.mesh","walkSurfaces":[{"height":2}]}""";
        if (building is null)
            File.WriteAllText(Path.Combine(root, "catalog.json"), """{"materials":[{"id":1,"name":"Invented ground","color":"#888888"}],"archetypes":[""" + Platform + "]}");
        else
        {
            var merged = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "catalog.json")))!;
            merged["archetypes"]!.AsArray().Add(JsonNode.Parse(Platform));
            File.WriteAllText(Path.Combine(root, "catalog.json"), merged.ToJsonString());
            File.WriteAllText(Path.Combine(root, "building.json"), building.ToJsonString());
        }
        if (variant == "support-only")
        {
            var catalog = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "catalog.json")))!;
            catalog["archetypes"]!.AsArray().Add(JsonNode.Parse("""{"id":"unused","name":"Unused","meshRef":"unused.mesh"}"""));
            File.WriteAllText(Path.Combine(root, "catalog.json"), catalog.ToJsonString());
        }
        TilePrefabFile.Save(new TilePrefab
        {
            Name = "Invented platform",
            Width = 1,
            Height = 1,
            PlaneCount = 1,
            Planes = new() { new TilePrefabPlane() },
            Objects = new() { new TilePrefabObject { ArchetypeId = "platform", X = 0, Z = 0, Plane = 0 } },
        }, Path.Combine(root, "definitions/platform.json"));
        File.WriteAllText(Path.Combine(root, "generator/build-scaffold.py"), "# Invented public fixture generator contract\nfrom helper import identity\n");
        File.WriteAllText(Path.Combine(root, "generator/helper.py"), "def identity(value):\n    return value\n");
        byte[] mesh = Encoding.UTF8.GetBytes("invented public mesh bytes");
        File.WriteAllBytes(Path.Combine(root, "assets/public.mesh"), mesh);
        var meshRef = new MapAssetRef("public-mesh", "assets/public.mesh", Hash(mesh), 1);
        // A prefab-local marker on the world terrain, expanded through a non-identity frame.
        var terrainBinding = new MapSupportBinding(MapSupportBindingKind.Surface, "plane-0", null, null, null, null);
        var kitMarker = MapMarkerHeights.Apply(new(1, "kit-marker", "relay", KitLocal(1.5, -6.5), 0.25f, "landmark-role", true,
            ["first", "second", "first"]), new(null, terrainBinding));
        var kit = new MapPrefabAsset(1, "marker-kit", [new MapPrefabMarkerChild(new(MapPrefabChildKind.Marker, "kit-marker"), kitMarker)
        { References = [new("/value/supportBinding/surfaceId", new(MapPrefabReferenceScope.World, MapPrefabChildKind.Surface, "plane-0"))] }]);
        byte[] kitBytes = MapPrefabFile.Write(kit);
        File.WriteAllBytes(Path.Combine(root, "assets/marker-kit"), kitBytes);
        var kitRef = new MapAssetRef("marker-kit", "assets/marker-kit", Hash(kitBytes), 1);
        byte[] manifest = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["payloadVersion"] = 1,
            ["assets"] = new JsonArray(),
            ["resources"] = new JsonArray(NativeAssetFixtures.Resource(meshRef, "Mesh"), NativeAssetFixtures.Resource(kitRef, "Prefab")),
        }.ToJsonString());
        File.WriteAllBytes(Path.Combine(root, "assets/markers.json"), manifest);
        var document = MapDocumentFile.LoadText("""
            {"formatVersion":8,"id":"invented-marker-native","tileSize":16,
             "bounds":{"minX":-64,"minZ":-64,"maxX":64,"maxZ":0},
             "playableBounds":{"minX":-64,"minZ":-64,"maxX":64,"maxZ":0},"terrain":{},
             "resolverIdentity":{"payloadVersion":1,"resolverVersion":2},"supportRecipe":"AuthoredBindingsV2"}
            """);
        document.NativeAssets.Add(new("marker-assets", "assets/markers.json", Hash(manifest), 1));
        document.Prefabs = new(1, 1, [new MapPrefabInstance("kit", kitRef.Id, kitRef.Sha256, KitFrame, null,
            new([new(new(MapPrefabChildKind.Marker, "kit-marker"), PrefabMarkerId, null, false)]), [])], []);
        foreach (var region in world.Regions.Keys)
        {
            var converted = LegacyOracleConverter.ToNative(world, region, 0);
            document.Surfaces.Patches.Add(converted.Patch.Key, converted.Patch);
            if (document.Surfaces.Refs.Count == 0) document.Surfaces.Refs.Add(converted.Surface);
            if (region.Rx != 0) continue;
            var key = converted.Patch.Key;
            converted.Patch.Records.Add(new MapSpaceDoc("exterior", MapSpaceKind.Exterior, null, null, [], [], [], []));
            converted.Patch.Records.Add(new MapSpaceFootprint("exterior-domain", new("exterior", key), key,
                Enumerable.Range(0, 8).SelectMany(z => Enumerable.Range(0, 8).Select(x => z * 64 + x)).ToArray(),
                new(MapBoundKind.SupportFloor, "plane-0", null), new(MapBoundKind.OpenTop, null, null)));
        }
        var deck = new MapSurfacePatch
        {
            Key = new("deck", 0, 0),
            CellMinX = 2,
            CellMinZ = 2,
            Width = 1,
            Depth = 1,
            Heights = [200, 200, 200, 200],
            Cells = [new(1, 0, MapOverlayCut.Full, 0, 0, MapCellTopology.Auto)],
            Presence = [1],
        };
        document.Surfaces.Patches.Add(deck.Key, deck);
        document.Surfaces.Refs.Add(new("deck", MapLatticeFrame.ImportedMetreCentimetre, MapSurfaceRole.SupportFloor, MapPresencePolicy.Native, null, null, ""));
        var parent = new MapRecordRef("exterior", new("plane-0", 0, 0));
        deck.Records.Add(new MapSpaceDoc("platform-room", MapSpaceKind.Room, parent, null, [], [], [], []));
        deck.Records.Add(new MapPrismDomain("platform-room-domain", 1, new("platform-room", deck.Key), parent, "", "platform-storey",
            new(MapLatticeFrame.ImportedMetreCentimetre, new[] { new MapWaterRect(2, 2, 3, 3) }, []), 2, 4));
        var roleRows = new JsonArray();
        var markerRows = new JsonArray();
        var nativeMarkers = new List<MapLocalMarkerDoc>();
        string[] projections = ["player-start", "spawn", "landmark", "label", "spawn", "label"];
        for (int i = 0; i < names.Length; i++)
        {
            bool explicitHeight = i == 3 && variant != "all-bound";
            string id = "destination-" + i, supportName = i == 1 ? "platform-top" : "terrain";
            var binding = new MapSupportBinding(MapSupportBindingKind.Surface, i == 1 ? "deck" : "plane-0", null, null, null, null);
            var marker = MapMarkerHeights.Apply(new(1, id, names[i], new Vector3(xs[i] + 0.5f, 0, -(zs[i] + 0.5f)), 0.371f,
                projections[i] + "-role", i != 4, ["first", "second", "first"]), explicitHeight ? new(0, null) : new(null, binding));
            nativeMarkers.Add(marker);
            roleRows.Add(new JsonObject
            {
                ["sourceName"] = names[i],
                ["id"] = id,
                ["role"] = marker.Role,
                ["projection"] = projections[i],
                ["offsetX"] = 0.5,
                ["offsetZ"] = 0.5,
                ["supportName"] = supportName,
                ["heightMode"] = explicitHeight ? "Explicit" : "Support",
                ["heightRule"] = i == 1 ? "standing" : "terrain",
                ["yaw"] = marker.YawRadians,
                ["enabled"] = marker.Enabled,
                ["archetype"] = null,
            });
            markerRows.Add(new JsonObject
            {
                ["sourceName"] = names[i],
                ["x"] = xs[i],
                ["z"] = zs[i],
                ["plane"] = 0,
                ["regionX"] = xs[i] < 0 ? -1 : 0,
                ["regionZ"] = 0,
                ["supportName"] = supportName,
                ["spaceId"] = i == 0 ? null : i == 1 ? "platform-room" : "exterior",
                ["parents"] = i == 1 ? new JsonArray("exterior") : new JsonArray(),
            });
        }
        roleRows.Add(new JsonObject
        {
            ["sourceName"] = "relay",
            ["id"] = PrefabMarkerId,
            ["role"] = kitMarker.Role,
            ["projection"] = "landmark",
            ["offsetX"] = 0.5,
            ["offsetZ"] = 0.5,
            ["supportName"] = "terrain",
            ["heightMode"] = "Support",
            ["heightRule"] = "terrain",
            ["yaw"] = (float)(kitMarker.YawRadians + MapEffectiveTransform.From(KitFrame).YawRadians),
            ["enabled"] = true,
            ["archetype"] = null,
        });
        markerRows.Add(new JsonObject
        {
            ["sourceName"] = "relay",
            ["x"] = 1,
            ["z"] = 6,
            ["plane"] = 0,
            ["regionX"] = 0,
            ["regionZ"] = 0,
            ["supportName"] = "terrain",
            ["spaceId"] = "exterior",
            ["parents"] = new JsonArray(),
        });
        var supports = new JsonArray();
        supports.Add(Support("terrain", "plane-0", "terrain", null));
        supports.Add(Support("platform-top", "deck", "object", variant switch
        {
            "absent-owner" => world.AllObjects().Max(o => o.Id) + 1,
            "nonpositive-owner" => 0,
            _ => supportOwner.Id,
        }));
        if (variant == "space-binding")
        {
            var binding = new MapSupportBinding(MapSupportBindingKind.Space, null, "platform-room", 2.1f, 0.2f, 0.2f);
            nativeMarkers[1] = nativeMarkers[1] with { SupportBinding = binding };
            supports[1]!["binding"] = JsonNode.Parse("""{"kind":"Space","spaceId":"platform-room","referenceY":2.1,"searchBelow":0.2,"searchAbove":0.2}""");
        }
        var roles = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["generator"] = new JsonObject { ["path"] = "generator/build-scaffold.py", ["helpers"] = new JsonArray("generator/helper.py") },
            ["sourcePrefabs"] = new JsonArray("definitions/platform.json"),
            ["buildingManifestPath"] = building is null ? null : "building.json",
            ["supports"] = supports,
            ["markers"] = roleRows,
        };
        var markers = new JsonObject { ["schemaVersion"] = 1, ["markers"] = markerRows };
        var comparison = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["nativeDocumentPath"] = "native.json",
            ["roleManifestPath"] = "roles.json",
            ["markerManifestPath"] = "markers.json",
            ["expectations"] = new JsonObject
            {
                ["total"] = 7,
                ["playerStart"] = 1,
                ["spawn"] = 2,
                ["landmark"] = 2,
                ["label"] = 2,
                ["negativeX"] = 1,
                ["supportBound"] = variant == "all-bound" ? 7 : 6,
                ["enabled"] = 6,
            },
        };
        switch (variant)
        {
            case "tags": nativeMarkers[0] = nativeMarkers[0] with { Tags = ["second", "first", "first"] }; break;
            case "height": nativeMarkers[3] = nativeMarkers[3] with { Position = nativeMarkers[3].Position with { Y = 0.25f } }; break;
            case "negative-x": nativeMarkers[0] = nativeMarkers[0] with { Position = nativeMarkers[0].Position with { X = 0.5f } }; break;
            case "yaw-full-turn": nativeMarkers[0] = nativeMarkers[0] with { YawRadians = nativeMarkers[0].YawRadians + MathF.Tau }; break;
            case "yaw-quarter-turn": nativeMarkers[0] = nativeMarkers[0] with { YawRadians = nativeMarkers[0].YawRadians + MathF.PI / 2 }; break;
            case "role": nativeMarkers[0] = nativeMarkers[0] with { Role = "other-role" }; break;
            case "name": nativeMarkers[0] = nativeMarkers[0] with { Name = "other-name" }; break;
            case "id": nativeMarkers[0] = nativeMarkers[0] with { Id = "other-id" }; break;
            case "enabled": nativeMarkers[0] = nativeMarkers[0] with { Enabled = false }; break;
            case "archetype": nativeMarkers[0] = nativeMarkers[0] with { ArchetypeId = "invented-actor" }; break;
            case "space": markerRows[1]!["spaceId"] = null; break;
            case "parents": markerRows[1]!["parents"] = new JsonArray("imaginary-parent"); break;
            case "binding": nativeMarkers[1] = nativeMarkers[1] with { SupportBinding = nativeMarkers[0].SupportBinding }; break;
            case "missing-support": supports.RemoveAt(1); break;
            case "extra-native": nativeMarkers.Add(nativeMarkers[3] with { Id = "extra", Name = "extra" }); break;
            case "expectation": comparison["expectations"]!["total"] = 5; break;
            case "standing-rule": roleRows[1]!["heightRule"] = "terrain"; break;
            case "wrong-source-count": nativeMarkers.RemoveAt(5); comparison["expectations"]!["total"] = 6; break;
            case "claimed-bound": roleRows[3]!["heightMode"] = "Support"; comparison["expectations"]!["supportBound"] = 7; break;
            case "twisted-bilinear":
                // The client presenter's bilinear height, which a server body never stands at on this tile.
                float bilinear = new TileDocumentGroundHeight(world).HeightAt(TwistedTileX, TwistedTileZ, 0);
                nativeMarkers[5] = MapMarkerHeights.Apply(nativeMarkers[5], new(bilinear, null));
                break;
            case "duplicate-destination": roleRows[1]!["id"] = roleRows[0]!["id"]!.DeepClone(); break;
            case "missing-role": roleRows.RemoveAt(5); break;
            case "extra-role": roleRows.Add(Renamed(roleRows[5]!, "extra-source")); break;
            case "missing-marker-row": markerRows.RemoveAt(5); break;
            case "extra-marker-row": markerRows.Add(Renamed(markerRows[5]!, "extra-source")); break;
            case "unknown-role-field": roleRows[0]!["unknown"] = true; break;
            case "unknown-height-rule": roleRows[0]!["heightRule"] = "guess"; break;
            case "unsupported-schema": comparison["schemaVersion"] = 2; break;
            case "escape-path": comparison["roleManifestPath"] = "../" + Sentinel; break;
        }
        document.BuildingIntent = new(1, [], [], [], nativeMarkers) { Storeys = [new(1, "platform-storey", "Platform floor", null)] };
        for (int i = 0; i < document.Surfaces.Refs.Count; i++)
        {
            var surface = document.Surfaces.Refs[i];
            document.Surfaces.Refs[i] = surface with
            {
                SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
                document.Surfaces.Patches.Values.Where(p => p.Key.SurfaceId == surface.Id)
                    .Select(p => new KeyValuePair<MapPatchKey, string>(p.Key, MapSurfaceSemantics.PatchDigest(p))))
            };
        }
        string nativeText = MapDocumentFile.SaveText(document);
        // The scaffold saves at the current format 9. The next format is the unsupported one.
        if (variant == "unsupported-native") nativeText = nativeText.Replace("\"formatVersion\": 9", "\"formatVersion\": 10", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(root, "native.json"), nativeText);
        string rolesText = roles.ToJsonString();
        if (variant == "duplicate-role-field") rolesText = rolesText.Replace("\"offsetX\":0.5", "\"offsetX\":0.5,\"offsetX\":0.5", StringComparison.Ordinal);
        if (variant == "case-role-field") rolesText = rolesText.Replace("\"offsetX\":0.5", "\"OffsetX\":0.5", StringComparison.Ordinal);
        if (variant == "case-duplicate-role-field") rolesText = rolesText.Replace("\"offsetX\":0.5", "\"offsetX\":0.5,\"OffsetX\":0.5", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(root, "roles.json"), rolesText);
        File.WriteAllText(Path.Combine(root, "markers.json"), markers.ToJsonString());
        if (variant is "duplicate-source" or "duplicate-index" or "wrong-index-tags" or "wrong-region-owner")
        {
            string worldPath = Path.Combine(root, "world/world.json"), regionPath = Path.Combine(root, "world/regions/r_0_0.json");
            var header = JsonNode.Parse(File.ReadAllText(worldPath))!;
            var region = JsonNode.Parse(File.ReadAllText(regionPath))!;
            if (variant == "duplicate-source") region["markers"]!.AsArray().Add(region["markers"]![0]!.DeepClone());
            if (variant == "duplicate-index") header["markers"]!.AsArray().Add(header["markers"]![0]!.DeepClone());
            if (variant == "wrong-index-tags") header["markers"]![0]!["tags"] = new JsonArray("second", "first");
            if (variant == "wrong-region-owner") region["markers"]![0]!["x"] = -2;
            File.WriteAllText(regionPath, region.ToJsonString());
            header["regions"]!.AsArray().Single(r => r!["rx"]!.GetValue<int>() == 0)!["hash"] = AssertFixtures.Sha256(regionPath);
            File.WriteAllText(worldPath, header.ToJsonString());
        }
        return comparison;
    }

    static JsonObject Support(string name, string surface, string kind, long? objectId) => new()
    {
        ["name"] = name,
        ["binding"] = new JsonObject { ["kind"] = "Surface", ["surfaceId"] = surface },
        ["source"] = new JsonObject { ["kind"] = kind, ["plane"] = 0, ["objectId"] = objectId },
    };

    static JsonNode Renamed(JsonNode row, string sourceName)
    {
        var copy = row.DeepClone();
        copy["sourceName"] = sourceName;
        return copy;
    }

    // A local point whose expansion through KitFrame lands one or two exact quanta west of the world target, inside the
    // source tolerance but never on the source float. Only the released frame kernel decides where it lands.
    internal static Vector3 KitLocal(double worldX, double worldZ)
    {
        var kernel = new MapResourceTransform(MapEffectiveTransform.From(KitFrame));
        var pose = kernel.Transform;
        var (s, c) = Math.SinCos(pose.YawRadians);
        double dx = (worldX - pose.X) / pose.Scale, dz = (worldZ - pose.Z) / pose.Scale;
        float x = (float)(c * dx - s * dz), z = (float)(s * dx + c * dz);
        for (int i = -16; i <= 16; i++)
            for (int j = -16; j <= 16; j++)
            {
                float lx = Step(x, i), lz = Step(z, j);
                var point = kernel.Point(new(MapExactValue.FromSingle(lx), MapExactValue.FromSingle(0), MapExactValue.FromSingle(lz)));
                double px = point.X.ToDouble(), pz = point.Z.ToDouble();
                if (px < worldX && worldX - px < 0.000002 && Math.Abs(pz - worldZ) < 0.000002) return new(lx, 0, lz);
            }
        throw new InvalidOperationException("no local point expands just west of the target");
    }

    static float Step(float value, int steps)
    {
        for (; steps > 0; steps--) value = MathF.BitIncrement(value);
        for (; steps < 0; steps++) value = MathF.BitDecrement(value);
        return value;
    }

    // Reuses the invented R5 building extraction as this world's retained building correspondence. Its world, catalog,
    // definitions and native resources become part of the marker extraction, and its scaffold moves aside.
    static JsonObject CopyBuilding(string root)
    {
        using var building = ShippedBuildingGuardFixtures.Create("valid");
        string source = building.Environment["KHAOZ_R2_SHIPPED_SOURCE"]!;
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(root, relative == "native.json" ? "building-native.json" : relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        var provenance = JsonNode.Parse(File.ReadAllText(building.Environment["KHAOZ_R2_ORACLE_PROVENANCE"]!))!;
        var comparison = provenance["comparison"]!.DeepClone().AsObject();
        comparison["nativeDocumentPath"] = "building-native.json";
        return comparison;
    }

    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
