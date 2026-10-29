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
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc;
using KhaozEngine.Tests.MapDocCompatibility;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
public sealed class ShippedBuildingInputGuardTests
{
    const int namesPerBuilding = 4;
    [Theory]
    [InlineData("missing-prefab")]
    [InlineData("missing-catalog")]
    [InlineData("missing-mesh")]
    [InlineData("stale-mesh-digest")]
    [InlineData("unassigned-leaf")]
    [InlineData("duplicate-leaf")]
    [InlineData("partial-room")]
    [InlineData("dangling-roof")]
    [InlineData("unattested-region")]
    [InlineData("unattested-dependency")]
    [InlineData("escape-path")]
    [InlineData("symlink-path")]
    [InlineData("missing-definition-key")]
    [InlineData("missing-doorway")]
    [InlineData("missing-light-owner")]
    [InlineData("duplicate-json-member")]
    [InlineData("escaping-texture")]
    [InlineData("rooted-texture")]
    [InlineData("standalone-roof-linked")]
    [InlineData("standalone-wall")]
    public void PrivateBuildingClosure_RefusesMissingOrUnverifiedInput(string variant)
    {
        using var input = ShippedBuildingGuardFixtures.Create(variant);
        var failure = Assert.ThrowsAny<Exception>(() => PrivateOracleEntry.Run(input.Environment, (i, report) =>
        {
            var result = ShippedBuildingComparison.Compare(ShippedBuildingInputs.Read(i));
            report.CheckFailures("building-closure", result.ClassifiedFailures);
        }));
        Assert.True(failure is PrivateOracleInputException or PrivateOracleFailure);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(ShippedBuildingGuardFixtures.PublicSecretMarker, failure.ToString());
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("actual-override")]
    [InlineData("shared-concealment")]
    [InlineData("curved-selection")]
    [InlineData("quarter-turn")]
    [InlineData("nested-texture")]
    [InlineData("standalone-roof")]
    [InlineData("yaw-full-turn")]
    [InlineData("derived-selection")]
    public void CompleteInventedBuilding_ClosesInputsAndComparesBothHeads(string variant)
    {
        using var fixture = ShippedBuildingGuardFixtures.Create(variant);
        Exception? failure = Record.Exception(() => PrivateOracleEntry.Run(fixture.Environment, (i, report) =>
        {
            var inputs = ShippedBuildingInputs.Read(i);
            Assert.Single(inputs.Prefabs);
            Assert.Equal((variant == "shared-concealment" ? namesPerBuilding * 2 : namesPerBuilding) + (variant == "standalone-roof" ? 1 : 0),
                inputs.World.AllObjects().Count());
            Assert.NotEmpty(inputs.MeshBytes);
            var result = ShippedBuildingComparison.Compare(inputs);
            report.Record("buildings", result.DetailJson);
            report.CheckFailures("building-parity", result.ClassifiedFailures);
        }));
        string reportPath = fixture.Environment["KHAOZ_R2_ORACLE_REPORT"]!;
        // This diagnostic is exclusively the invented public fixture, never the process environment.
        Assert.True(failure is null, File.Exists(reportPath) ? File.ReadAllText(reportPath) : failure?.ToString());
        Assert.Equal(PrivateOracleFixtures.SecureDirectoryMode, File.GetUnixFileMode(Path.GetDirectoryName(reportPath)!));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(reportPath));
        Assert.Contains("independent-heads", File.ReadAllText(reportPath));
        Assert.Throws<PrivateOracleInputException>(() => PrivateOracleInputs.Require(fixture.Environment));
    }

    [Fact]
    public void RoomCellsNearerAnotherFrameAnchor_AreQueriedInTheAcquiredFrame()
    {
        // F-R14. A decoy patch anchors the complete view one frame east, so every room cell lies nearer the origin anchor.
        using var fixture = ShippedBuildingGuardFixtures.Create("foreign-frame");
        Exception? failure = Record.Exception(() => PrivateOracleEntry.Run(fixture.Environment, (i, report) =>
        {
            var inputs = ShippedBuildingInputs.Read(i);
            var view = MapNativeResolution.Resolve(inputs.NativeDocument, inputs.Assets, new("guard", 1, "guard/1", 2)).EffectiveResources!.CompleteView();
            Assert.NotEqual(WorldFrame.Nearest(new Vector3(0.5f, 1, -0.5f)), view.Scope.Frame);
            Assert.Equal(WorldFrame.Nearest(new Vector3(1.5f, 1, -1.5f)), WorldFrame.Nearest(new Vector3(0.5f, 1, -0.5f)));
            var result = ShippedBuildingComparison.Compare(inputs);
            report.Record("buildings", result.DetailJson);
            report.CheckFailures("building-parity", result.ClassifiedFailures);
        }));
        string reportPath = fixture.Environment["KHAOZ_R2_ORACLE_REPORT"]!;
        Assert.True(failure is null, File.Exists(reportPath) ? File.ReadAllText(reportPath) : failure?.ToString());
    }

    [Theory]
    [InlineData("native-transform")]
    [InlineData("native-tags")]
    [InlineData("native-mesh")]
    [InlineData("native-collider")]
    [InlineData("native-selection")]
    [InlineData("membership-centre")]
    [InlineData("native-roof-link")]
    [InlineData("native-light")]
    [InlineData("floor-paint")]
    [InlineData("doorway-clearance")]
    [InlineData("expectation-count")]
    [InlineData("native-yaw")]
    [InlineData("derived-selection-box")]
    [InlineData("derived-selection-missing")]
    [InlineData("collider-selection")]
    public void Comparison_RecordsInventedSemanticMismatchOnlyInSecureReport(string variant)
    {
        using var fixture = ShippedBuildingGuardFixtures.Create(variant);
        ShippedBuildingReport? compared = null;
        var failure = Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(fixture.Environment, (i, report) =>
        {
            var result = ShippedBuildingComparison.Compare(ShippedBuildingInputs.Read(i));
            compared = result;
            report.Record("buildings", result.DetailJson);
            report.CheckFailures("building-parity", result.ClassifiedFailures);
        }));
        Assert.NotNull(compared);
        string category = variant switch
        {
            "native-transform" => "leaf-position",
            "native-tags" => "ordered-tags",
            "native-mesh" => "original-mesh",
            "native-collider" => "physical-collider-count",
            "native-selection" => "selection-bytes",
            "membership-centre" => "membership-centre",
            "native-roof-link" => "roof-links",
            "native-light" => "light-properties",
            "floor-paint" => "floor-material",
            "doorway-clearance" => "doorway-clear",
            "expectation-count" => "expectation-dedicatedlights",
            "native-yaw" => "leaf-yaw-scale",
            "derived-selection-box" or "derived-selection-missing" => "derived-selection",
            "collider-selection" => "authored-selection",
            _ => throw new InvalidOperationException("unknown invented mismatch"),
        };
        Assert.Contains(category, compared.Failures);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(ShippedBuildingGuardFixtures.PublicSecretMarker, failure.ToString());
        Assert.Contains("building-parity", failure.Message);
        Assert.Contains("failures", File.ReadAllText(fixture.Environment["KHAOZ_R2_ORACLE_REPORT"]!));
    }
    [Theory]
    [InlineData("standalone-roof-linked", "standalone roof has concealment")]
    [InlineData("standalone-wall", "unassigned building leaf")]
    public void StandaloneLeaf_RefusesForItsSpecificReason(string variant, string reason)
    {
        // F-R6. Only an inventoried unlinked roof may stand outside every nominated building.
        using var fixture = ShippedBuildingGuardFixtures.Create(variant);
        var error = Assert.Throws<InvalidDataException>(() => ShippedBuildingInputs.Read(PrivateOracleInputs.Require(fixture.Environment)));
        Assert.Equal(reason, error.Message);
    }

    [Fact]
    public void AcceptedFormatSevenBuilding_LoadsCurrentWithoutRewritingScaffold() => AcceptedFormat_LoadsCurrentWithoutRewritingScaffold(7);

    // A marker extraction accepted at format 8 may retain its building correspondence at format 8.
    [Fact]
    public void AcceptedFormatEightBuilding_LoadsCurrentWithoutRewritingScaffold() => AcceptedFormat_LoadsCurrentWithoutRewritingScaffold(8);

    static void AcceptedFormat_LoadsCurrentWithoutRewritingScaffold(int format)
    {
        using var fixture = ShippedBuildingGuardFixtures.Create("valid");
        var environment = fixture.Environment.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        string native = Path.Combine(environment["KHAOZ_R2_SHIPPED_SOURCE"]!, "native.json");
        var scaffold = JsonNode.Parse(File.ReadAllText(native))!;
        scaffold["formatVersion"] = format;
        File.WriteAllText(native, scaffold.ToJsonString());
        string provenancePath = environment["KHAOZ_R2_ORACLE_PROVENANCE"]!;
        var provenance = JsonNode.Parse(File.ReadAllText(provenancePath))!;
        provenance["paths"]!.AsArray().Single(p => p!["path"]!.GetValue<string>() == "native.json")!["sha256"] = AssertFixtures.Sha256(native);
        var typed = JsonSerializer.Deserialize<ShippedSourceProvenance>(provenance.ToJsonString(), ShippedSourceProvenance.JsonOptions)!;
        provenance["aggregateSha256"] = typed.ComputeAggregate();
        File.WriteAllText(provenancePath, provenance.ToJsonString());
        environment["KHAOZ_R2_ORACLE_PROVENANCE_SHA256"] = AssertFixtures.Sha256(provenancePath);
        var inputs = ShippedBuildingInputs.Read(PrivateOracleInputs.Require(environment));
        Assert.Equal(MapDocumentFile.CurrentFormatVersion, inputs.NativeDocument.FormatVersion);
        // The migration happened in memory. The retained attested bytes still declare the accepted format.
        using var retained = JsonDocument.Parse(inputs.Bytes("native.json"));
        Assert.Equal(format, retained.RootElement.GetProperty("formatVersion").GetInt32());
    }

    [Fact]
    public void ColliderTopology_WithUnchangedVertexAndNormalBagsIsRefused()
    {
        using var fixture = ShippedBuildingGuardFixtures.Create("collider-topology");
        PrivateOracleInputs entry = PrivateOracleInputs.Require(fixture.Environment);
        var inputs = ShippedBuildingInputs.Read(entry);
        var native = Assert.IsType<TriangleMeshShape>(MapAssetShapes.Read(inputs.Assets, "lamp").Collider);
        var source = Assert.Single(TileWorldColliders.Build(inputs.World, inputs.Catalogs, inputs.CollisionOptions).Colliders,
            c => c.Kind == TileColliderKind.Object);
        Assert.IsType<BoxShape>(source.Shape);
        var corners = new[] { new Vector3(2, -0.1f, -1), new(2, -0.1f, 0), new(2, 0.4f, -1), new(2, 0.4f, 0),
            new(3, -0.1f, -1), new(3, -0.1f, 0), new(3, 0.4f, -1), new(3, 0.4f, 0) };
        Assert.Equal(corners, native.Vertices.Select(v => v + new Vector3(2.5f, 0, -0.5f)));
        var normals = Enumerable.Range(0, native.Indices.Length / 3).Select(i => Vector3.Normalize(Vector3.Cross(
            native.Vertices[native.Indices[i * 3 + 1]] - native.Vertices[native.Indices[i * 3]],
            native.Vertices[native.Indices[i * 3 + 2]] - native.Vertices[native.Indices[i * 3]]))).ToArray();
        Assert.Equal(new[] { -Vector3.UnitX, Vector3.UnitX, -Vector3.UnitY, Vector3.UnitY, -Vector3.UnitZ, Vector3.UnitZ }, normals);
        ShippedBuildingReport? compared = null;
        Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(fixture.Environment, (i, report) =>
        {
            compared = ShippedBuildingComparison.Compare(ShippedBuildingInputs.Read(i));
            report.Record("buildings", compared.DetailJson);
            report.CheckFailures("building-parity", compared.ClassifiedFailures);
        }));
        Assert.True(compared is not null, File.ReadAllText(fixture.Environment["KHAOZ_R2_ORACLE_REPORT"]!));
        Assert.Contains(compared.Failures, category => category.StartsWith("physical-collider", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("renumbered", true)]
    [InlineData("rewired", false)]
    [InlineData("reversed", false)]
    public void MeshGeometry_PreservesConnectivityAndOrientedWinding(string variant, bool equivalent)
    {
        var vertices = new[] { Vector3.Zero, Vector3.UnitX, new Vector3(1, 1, 0), Vector3.UnitY };
        var original = new TriangleMeshShape(vertices, new[] { 0, 1, 2, 0, 2, 3 });
        var candidate = variant switch
        {
            "renumbered" => new TriangleMeshShape(vertices.Reverse().ToArray(), new[] { 3, 2, 1, 3, 1, 0 }),
            "rewired" => new TriangleMeshShape(vertices, new[] { 0, 1, 3, 1, 2, 3 }),
            "reversed" => new TriangleMeshShape(vertices, new[] { 0, 2, 1, 0, 3, 2 }),
            _ => throw new InvalidOperationException("unknown public mesh control"),
        };
        var failures = new List<string>();
        ShippedBuildingGeometry.Compare("mesh-guard",
            ShippedBuildingGeometry.Capture(original, p => p, n => n).ToArray(),
            ShippedBuildingGeometry.Capture(candidate, p => p, n => n).ToArray(), "invented",
            (category, ok, _) => { if (!ok) failures.Add(category); }, (_, _, _, _, _) => { });
        Assert.Equal(equivalent, failures.Count == 0);
    }

    [Fact]
    public void MeshGeometry_DuplicatePositionRenumberingRefusesConservatively()
    {
        // Vertices 0 and 3 share a position and belong to different triangles. Swapping their indices yields the same
        // mesh, but the coordinate-first vertex matching keeps the identity correspondence and refuses it. This
        // documents a false refusal, not a false pass. Topology and winding enforcement stay unchanged.
        var vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.Zero, -Vector3.UnitX, -Vector3.UnitY };
        var original = new TriangleMeshShape(vertices, new[] { 0, 1, 2, 3, 4, 5 });
        var renumbered = new TriangleMeshShape(vertices, new[] { 3, 1, 2, 0, 4, 5 });
        List<string> Failures(TriangleMeshShape candidate)
        {
            var failures = new List<string>();
            ShippedBuildingGeometry.Compare("mesh-guard",
                ShippedBuildingGeometry.Capture(original, p => p, n => n).ToArray(),
                ShippedBuildingGeometry.Capture(candidate, p => p, n => n).ToArray(), "invented",
                (category, ok, _) => { if (!ok) failures.Add(category); }, (_, _, _, _, _) => { });
            return failures;
        }
        Assert.Empty(Failures(original));
        Assert.Equal(new[] { "mesh-guard-primitive", "mesh-guard-unexpected" }, Failures(renumbered));
    }

    [Fact]
    public void SourcePickingDelta_RecordsBothQueriesAndMeasuredNonzeroDifference()
    {
        using var fixture = ShippedBuildingGuardFixtures.Create("picking-delta");
        PrivateOracleEntry.Run(fixture.Environment, (i, report) =>
        {
            var result = ShippedBuildingComparison.Compare(ShippedBuildingInputs.Read(i));
            report.Record("buildings", result.DetailJson);
            report.CheckFailures("building-parity", result.ClassifiedFailures);
        });
        using var reportJson = JsonDocument.Parse(File.ReadAllText(fixture.Environment["KHAOZ_R2_ORACLE_REPORT"]!));
        var delta = reportJson.RootElement.GetProperty("detail").GetProperty("records").EnumerateArray()
            .Single(r => r.GetProperty("category").GetString() == "source-query-delta" && r.GetProperty("detail").GetProperty("sourceId").GetInt64() == 3)
            .GetProperty("detail");
        Assert.Equal(0.1f, delta.GetProperty("sourceBounds").GetProperty("MaxY").GetSingle());
        Assert.Equal("deferred R11 acceptance", delta.GetProperty("acceptance").GetString());
        Assert.InRange(delta.GetProperty("nativeBounds").GetProperty("MaxY").GetDouble(), 0.899, 0.901);
        Assert.InRange(delta.GetProperty("boundsDelta").GetProperty("MaxY").GetDouble(), 0.799, 0.801);
        Assert.Contains(delta.GetProperty("queries").EnumerateArray(), q =>
            q.GetProperty("source").GetProperty("hit").GetBoolean() != q.GetProperty("native").GetProperty("hit").GetBoolean());
        Assert.Contains(delta.GetProperty("queries").EnumerateArray(), q => q.GetProperty("distanceDelta").ValueKind == JsonValueKind.Number &&
            Math.Abs(q.GetProperty("distanceDelta").GetDouble()) > 0.1);
    }

}

[UnsupportedOSPlatform("windows")]
internal sealed class ShippedBuildingGuardFixtures : IDisposable
{
    internal const string PublicSecretMarker = "PUBLIC-INVENTED-BUILDING-SECRET";
    readonly string _root;
    internal IReadOnlyDictionary<string, string?> Environment { get; }

    ShippedBuildingGuardFixtures(string root, IReadOnlyDictionary<string, string?> environment)
    { _root = root; Environment = environment; }

    internal static ShippedBuildingGuardFixtures Create(string variant)
    {
        string root = Path.Combine(Path.GetTempPath(), "ke-building-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root, PrivateOracleFixtures.SecureDirectoryMode);
        try
        {
            JsonObject comparison = null!;
            var env = PrivateOracleFixtures.SyntheticEnvironment(root, PublicSecretMarker,
                source => comparison = Write(source, variant), JsonSerializer.SerializeToElement(new { pending = true }));
            string provenancePath = env["KHAOZ_R2_ORACLE_PROVENANCE"]!;
            var provenance = JsonNode.Parse(File.ReadAllText(provenancePath))!.AsObject();
            provenance["comparison"] = comparison;
            if (variant is "missing-prefab" or "missing-catalog" or "missing-mesh" or "stale-mesh-digest" or "symlink-path")
            {
                string file = Path.Combine(env["KHAOZ_R2_SHIPPED_SOURCE"]!, variant switch
                { "missing-prefab" => "definitions/cabin.json", "missing-catalog" => "catalog.json", _ => "assets/wall.mesh" });
                if (variant == "stale-mesh-digest") File.AppendAllText(file, "stale");
                else
                {
                    File.Delete(file);
                    if (variant == "symlink-path") File.CreateSymbolicLink(file, Path.Combine(root, "absent"));
                }
            }
            if (variant is "unattested-region" or "unattested-dependency")
            {
                string omitted = variant == "unattested-region" ? "world/regions/r_0_0.json" : "assets/wall.mesh";
                var paths = provenance["paths"]!.AsArray();
                paths.Remove(paths.Single(p => p!["path"]!.GetValue<string>() == omitted));
                // Also omit the file. A manifest reference must be refused before its loader tries to read it.
                File.Delete(Path.Combine(env["KHAOZ_R2_SHIPPED_SOURCE"]!, omitted));
                var typed = JsonSerializer.Deserialize<ShippedSourceProvenance>(provenance.ToJsonString(), ShippedSourceProvenance.JsonOptions)!;
                provenance["aggregateSha256"] = typed.ComputeAggregate();
            }
            string text = provenance.ToJsonString();
            if (variant == "duplicate-json-member") text = text.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal);
            File.WriteAllText(provenancePath, text);
            env["KHAOZ_R2_ORACLE_PROVENANCE_SHA256"] = AssertFixtures.Sha256(provenancePath);
            return new(root, env);
        }
        catch { Directory.Delete(root, true); throw; }
    }

    static JsonObject Write(string root, string variant)
    {
        Directory.CreateDirectory(Path.Combine(root, "world"));
        Directory.CreateDirectory(Path.Combine(root, "definitions"));
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        var world = new TileWorldDocument { Id = "invented-building" };
        // F-R4. A nested catalog's texture climbs back to a sibling directory, as catalog-relative paths may.
        string catalogFile = variant == "nested-texture" ? "catalogs/catalog.json" : "catalog.json";
        world.CatalogPaths.Add("../" + catalogFile);
        world.GetOrCreateRegion(new(0, 0));
        for (int z = 0; z < 4; z++)
            for (int x = 0; x < 4; x++) world.SetUnderlay(x, z, 0, 1);
        if (variant == "collider-topology")
        {
            world.SetCornerHeightCm(2, 0, 0, -10); world.SetCornerHeightCm(2, 1, 0, -10);
            world.SetCornerHeightCm(3, 0, 0, 10); world.SetCornerHeightCm(3, 1, 0, 10);
        }
        var names = new[] { "wall", "roof", "lamp", "shelter" };
        var objects = names.Select((name, index) => world.AddObject(name, index, 0, 0, variant == "quarter-turn" && name == "wall" ? 1 : 0, new[] { "first", "second" })).ToArray();
        var otherObjects = variant == "shared-concealment" ? names.Select((name, index) => world.AddObject(name, index, 2, 0, 0, new[] { "first", "second" })).ToArray() : [];
        // F-R6. A source leaf outside every nominated building. Only an unlinked roof may stand alone.
        string? standaloneKind = variant switch { "standalone-roof" or "standalone-roof-linked" => "shelter", "standalone-wall" => "wall", _ => null };
        var standalone = standaloneKind is null ? null : world.AddObject(standaloneKind, 3, 3, 0, 1, new[] { "first", "second" });
        TileWorldFile.Save(world, Path.Combine(root, "world"));
        string? texture = variant switch
        {
            "nested-texture" or "escaping-texture" => "../textures/stone.png",
            "rooted-texture" => "/textures/stone.png",
            _ => null,
        };
        if (texture is not null)
        {
            // Invented bytes. The escaping copy sits outside the extraction, so only containment can refuse it.
            string textures = Path.Combine(variant == "escaping-texture" ? Path.GetDirectoryName(root)! : root, "textures");
            Directory.CreateDirectory(textures);
            File.WriteAllText(Path.Combine(textures, "stone.png"), "invented-public-texture");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, catalogFile))!);
        string material = texture is null ? "" : ",\"texture\":\"" + texture + "\"";
        File.WriteAllText(Path.Combine(root, catalogFile), "{\"materials\":[{\"id\":1,\"name\":\"Stone\",\"color\":\"#888888\"" + material + "}]," + """
            "archetypes":[
            {"id":"wall","name":"Wall","meshRef":"assets/wall.mesh","collisionKind":"Wall","collisionHeight":3},
            {"id":"roof","name":"Roof","meshRef":"assets/roof.mesh","isRoof":true},
            {"id":"lamp","name":"Lamp","meshRef":"assets/lamp.mesh"},
            {"id":"shelter","name":"Shelter","meshRef":"assets/shelter.mesh","isRoof":true}]}
            """);
        if (variant == "collider-topology")
        {
            string catalogPath = Path.Combine(root, "catalog.json");
            File.WriteAllText(catalogPath, File.ReadAllText(catalogPath).Replace("\"id\":\"lamp\",\"name\":\"Lamp\",\"meshRef\":\"assets/lamp.mesh\"",
                "\"id\":\"lamp\",\"name\":\"Lamp\",\"meshRef\":\"assets/lamp.mesh\",\"collisionKind\":\"Solid\",\"collisionHeight\":0.4", StringComparison.Ordinal));
        }
        var catalogs = TileWorldCatalogs.Load(new[] { Path.Combine(root, catalogFile) });
        var sourcePrefab = new TilePrefab
        {
            Name = "Invented cabin",
            Width = 4,
            Height = 1,
            PlaneCount = 1,
            Planes = new() { new TilePrefabPlane { Underlay = new ushort[] { 1, 1, 1, 1 } } },
            Objects = objects.Select(o => new TilePrefabObject
            {
                ArchetypeId = o.ArchetypeId,
                X = o.X,
                Z = o.Z,
                Plane = o.Plane,
                Rotation = o.Rotation,
                Tags = o.Tags
            }).ToList()
        };
        TilePrefabFile.Save(sourcePrefab, Path.Combine(root, "definitions/cabin.json"));
        if (variant == "actual-override")
        { objects[2].Tags!.Add("customized"); TileWorldFile.Save(world, Path.Combine(root, "world"), force: true); }

        var resources = new JsonArray();
        var assets = new JsonArray();
        MapAssetRef Add(string id, byte[] bytes, string kind, params string[] dependencies)
        {
            var reference = new MapAssetRef(id, "assets/" + id, Convert.ToHexStringLower(SHA256.HashData(bytes)), 1);
            File.WriteAllBytes(Path.Combine(root, reference.Path), bytes);
            resources.Add(NativeAssetFixtures.Resource(reference, kind, dependencies));
            return reference;
        }
        byte[] Shape(PhysicsShape shape)
        {
            using var stream = new MemoryStream();
            PropCollisionFormat.Write(shape, stream);
            return stream.ToArray();
        }
        var children = new List<MapPrefabChild>();
        var bindings = new List<MapPrefabBinding>();
        foreach (var o in objects)
        {
            string id = o.ArchetypeId;
            Add(id + ".mesh", Encoding.UTF8.GetBytes("invented mesh " + id), "Mesh");
            // F-R10. The colliderless lamp's derived selection spans its renderBounds (-2, 0.5, -3) to (2, 8, 3).
            bool derived = id == "lamp" && variant is "derived-selection" or "derived-selection-box";
            bool noSelection = id == "lamp" && variant == "derived-selection-missing";
            if (derived)
                Add("lamp.selection", Shape(new CompoundShape(new[] { new CompoundChild(new BoxShape(new(2, variant == "derived-selection" ? 3.75f : 3.5f, 3)),
                    new Pose(new(0, variant == "derived-selection" ? 4.25f : 4, 0), Quaternion.Identity)) })), "Selection");
            else if (!noSelection)
                Add(id + ".selection", Shape(variant == "curved-selection" && id == "lamp"
                    ? new CylinderShape(0.2f, 0.4f) : new BoxShape(new(id == "roof" ? 0.4f : 0.5f, 0.1f, 0.5f))), "Selection");
            if (id == "wall")
                Add("wall.collider", Shape(new CompoundShape(new[] { new CompoundChild(new BoxShape(new(0.05f, 1.5f, 0.5f)),
                    new Pose(new(-0.5f, 1.5f, 0), Quaternion.Identity)) })), "Collider");
            if (id == "lamp" && variant == "collider-topology")
                Add("lamp.collider", Shape(new TriangleMeshShape(new[]
                { new Vector3(-0.5f, -0.1f, -0.5f), new(-0.5f, -0.1f, 0.5f), new(-0.5f, 0.4f, -0.5f), new(-0.5f, 0.4f, 0.5f),
                    new(0.5f, -0.1f, -0.5f), new(0.5f, -0.1f, 0.5f), new(0.5f, 0.4f, -0.5f), new(0.5f, 0.4f, 0.5f) },
                    new[] { 0, 1, 3, 4, 6, 7, 0, 4, 5, 2, 3, 7, 0, 2, 6, 1, 5, 7 })), "Collider");
            // A native asset needs an interaction source, so the selectionless lamp carries a collider instead.
            if (noSelection) Add("lamp.collider", Shape(new BoxShape(new(0.5f, 0.1f, 0.5f))), "Collider");
            var a = NativeAssetFixtures.Asset();
            a["id"] = id; a["meshResourceId"] = id + ".mesh";
            a["collisionResourceId"] = id == "wall" ? "wall.collider" : id == "lamp" && variant is "collider-topology" or "derived-selection-missing" ? "lamp.collider" : null;
            a["selectionResourceId"] = noSelection ? null : id + ".selection";
            foreach (string field in new[] { "supportResourceIds", "materialResourceIds", "lodResourceIds", "lightResourceIds" }) a[field] = new JsonArray();
            a["sourceUnitsToMetres"] = 1; a["source"] = "invented/" + id; a["textured"] = false;
            if (id == "lamp" && variant == "picking-delta")
                a["renderBounds"] = JsonNode.Parse("""{"min":{"x":-0.5,"y":-0.1,"z":-0.5},"max":{"x":0.5,"y":0.1,"z":0.5}}""");
            assets.Add(a);
            Vector3 position = TileObjectPlacement.AnchorPosition(world, catalogs.Archetype(id)!, o);
            children.Add(new MapPrefabPlacementChild(new(MapPrefabChildKind.Placement, id), new MapPlacement
            {
                Id = id,
                Kind = id,
                AssetId = id,
                X = position.X,
                Y = position.Y,
                Z = position.Z,
                Yaw = TileObjectPlacement.YawRadians(catalogs.Archetype(id)!, o.Rotation),
                Scale = 1,
                Tags = variant == "native-tags" && id == "lamp" ? new() { "second", "first" } : new() { "first", "second" }
            }));
            bindings.Add(new(new(MapPrefabChildKind.Placement, id), "b/" + id, o.Id, false));
        }
        if (variant == "native-transform") ((MapPrefabPlacementChild)children[2]).Value.X += 0.25f;
        // F-R9. A full turn is the same pose. A quarter turn is not.
        if (variant is "yaw-full-turn" or "native-yaw") ((MapPrefabPlacementChild)children[2]).Value.Yaw += variant == "native-yaw" ? MathF.PI / 2 : MathF.Tau;
        var light = new MapLocalLightDoc(1, "indoor", new(MapPrefabReferenceScope.Local, MapPrefabChildKind.Placement, "lamp"),
            new(0, 1, 0), Vector3.One, variant == "native-light" ? 2 : 1, 2, false);
        children.Add(new MapPrefabLightChild(new(MapPrefabChildKind.Light, "indoor"), light));
        bindings.Add(new(new(MapPrefabChildKind.Light, "indoor"), "b/indoor", null, false));
        children.Add(new MapPrefabRoofChild(new(MapPrefabChildKind.RoofLink, "roof-link"), new("roof-link",
            new(MapPrefabReferenceScope.Local, MapPrefabChildKind.Placement, "roof"),
            new[] { new MapPrefabReference(MapPrefabReferenceScope.Local, MapPrefabChildKind.Record, "room") },
            variant == "shared-concealment" ? new[] { new MapPrefabReference(MapPrefabReferenceScope.World, MapPrefabChildKind.RoofGroup, "shared") } : [], variant == "native-roof-link" ? 4 : 3)));
        bindings.Add(new(new(MapPrefabChildKind.RoofLink, "roof-link"), "b/roof-link", null, false));
        var floorAnchor = new MapPatchKey("semantic", 0, 0);
        var semanticPatch = new MapSurfacePatch { Key = floorAnchor, Width = 1, Depth = 1, Heights = new int[4], Cells = new MapSurfaceCell[1], Presence = new ulong[1] };
        children.Add(new MapPrefabSurfaceChild(new(MapPrefabChildKind.Surface, "semantic"),
            new("semantic", MapLatticeFrame.ImportedMetreCentimetre, MapSurfaceRole.SupportFloor, MapPresencePolicy.Native, null, null, new string('a', 64)), new[] { semanticPatch })
        { References = new[] { new MapPrefabReferenceSlot("/patches/0/key/surfaceId", new(MapPrefabReferenceScope.Local, MapPrefabChildKind.Surface, "semantic")) } });
        MapPrefabReferenceSlot LocalFloor(string slot) => new(slot, new(MapPrefabReferenceScope.Local, MapPrefabChildKind.Surface, "semantic"));
        MapPrefabReferenceSlot Local(string slot, MapPrefabChildKind kind, string id) => new(slot, new(MapPrefabReferenceScope.Local, kind, id));
        children.Add(new MapPrefabRecordChild(new(MapPrefabChildKind.Record, "room"),
            new MapSpaceDoc("room", MapSpaceKind.Room, new("$parent", new("$parent", 0, 0)), null, [], [], [], []), floorAnchor)
        {
            References = new[] { LocalFloor("/anchor/surfaceId"), Local("/value/parent/id", MapPrefabChildKind.Record, "$parent"),
            Local("/value/parent/anchor/surfaceId", MapPrefabChildKind.Surface, "$parent") }
        });
        children.Add(new MapPrefabRecordChild(new(MapPrefabChildKind.Record, "room-domain"),
            new MapPrismDomain("room-domain", 1, new("room", floorAnchor), new("$parent", new("$parent", 0, 0)), "", "ground",
                new(MapLatticeFrame.ImportedMetreCentimetre, new[] { new MapWaterRect(0, 0, 2, 2) }, []), 0, 3), floorAnchor)
        {
            References = new[] { LocalFloor("/anchor/surfaceId"), Local("/value/space/id", MapPrefabChildKind.Record, "room"),
            LocalFloor("/value/space/anchor/surfaceId"), Local("/value/parent/id", MapPrefabChildKind.Record, "$parent"),
            Local("/value/parent/anchor/surfaceId", MapPrefabChildKind.Surface, "$parent"), Local("/value/storeyId", MapPrefabChildKind.Storey, "ground") }
        });
        children.Add(new MapPrefabStoreyChild(new(MapPrefabChildKind.Storey, "ground"), "Ground"));
        foreach (var (kind, key) in new[] { (MapPrefabChildKind.Surface, "semantic"), (MapPrefabChildKind.Record, "room"), (MapPrefabChildKind.Record, "room-domain"), (MapPrefabChildKind.Storey, "ground") })
            bindings.Add(new(new(kind, key), "b/" + key, null, false));
        children.Add(new MapPrefabLightChild(new(MapPrefabChildKind.Light, "glow"), new(1, "glow",
            new(MapPrefabReferenceScope.Local, MapPrefabChildKind.Placement, "wall"), new(0, 0.5f, 0), new(1, 0.5f, 0.25f), 0.25f, 1, false)));
        bindings.Add(new(new(MapPrefabChildKind.Light, "glow"), "b/glow", null, false));
        var prefab = new MapPrefabAsset(1, "cabin", children)
        {
            Roofs = new[] { new MapPrefabReference(MapPrefabReferenceScope.Local, MapPrefabChildKind.Placement, "roof"),
            new MapPrefabReference(MapPrefabReferenceScope.Local, MapPrefabChildKind.Placement, "shelter") }
        };
        var prefabRef = Add("cabin", MapPrefabFile.Write(prefab), "Prefab",
            resources.Select(r => r!["reference"]!["id"]!.GetValue<string>()).ToArray());
        if (variant is "native-mesh" or "native-collider" or "native-selection")
        {
            var altered = assets[0]!.AsObject();
            if (variant == "native-mesh")
            { var r = Add("different.mesh", Encoding.UTF8.GetBytes("different invented mesh"), "Mesh"); altered["meshResourceId"] = r.Id; resources.Single(n => n!["kind"]!.GetValue<string>() == "Prefab")!["dependencies"]!.AsArray().Add(r.Id); }
            if (variant == "native-collider") altered["collisionResourceId"] = null;
            if (variant == "native-selection") altered["selectionResourceId"] = "roof.selection";
        }
        var manifest = new JsonObject { ["payloadVersion"] = 1, ["assets"] = assets, ["resources"] = resources };
        byte[] manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        var manifestRef = new MapAssetRef("building-assets", "assets/manifest.json", Convert.ToHexStringLower(SHA256.HashData(manifestBytes)), 1);
        File.WriteAllBytes(Path.Combine(root, manifestRef.Path), manifestBytes);
        var document = MapDocumentFile.LoadText("""
            {"formatVersion":7,"id":"invented-native","bounds":{"minX":0,"minZ":-64,"maxX":64,"maxZ":0},
            "playableBounds":{"minX":0,"minZ":-64,"maxX":64,"maxZ":0},"terrain":{},
            "resolverIdentity":{"payloadVersion":1,"resolverVersion":2},"supportRecipe":"AuthoredBindingsV2"}
            """);
        var converted = LegacyOracleConverter.ToNative(world, new(0, 0), 0);
        var patch = converted.Patch;
        var anchor = patch.Key;
        patch.Records.Add(new MapSpaceDoc("exterior", MapSpaceKind.Exterior, null, null, [], [], [], []));
        patch.Records.Add(new MapSpaceFootprint("exterior-domain", new("exterior", anchor), anchor,
            Enumerable.Range(0, 4).SelectMany(z => Enumerable.Range(0, 4).Select(x => z * 64 + x)).ToArray(),
            new(MapBoundKind.SupportFloor, "plane-0", null), new(MapBoundKind.OpenTop, null, null)));
        document.Surfaces.Patches.Add(anchor, patch);
        document.Surfaces.Refs.Add(converted.Surface with
        {
            SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(converted.Surface,
            new[] { new KeyValuePair<MapPatchKey, string>(anchor, MapSurfaceSemantics.PatchDigest(patch)) })
        });
        if (variant == "foreign-frame")
        {
            // F-R14. An empty patch that sorts first and lies nearer the next anchor east picks the view's frame.
            var decoy = new MapSurfacePatch { Key = new("a-decoy", 1, 0), CellMinX = 1, Width = 1, Depth = 1, Heights = new int[4], Cells = new MapSurfaceCell[1], Presence = new ulong[1] };
            var decoySurface = new MapSurfaceRef("a-decoy", MapLatticeFrame.ImportedMetreCentimetre, MapSurfaceRole.SupportFloor, MapPresencePolicy.Native, null, null, new string('a', 64));
            document.Surfaces.Patches.Add(decoy.Key, decoy);
            document.Surfaces.Refs.Add(decoySurface with
            {
                SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(decoySurface,
                new[] { new KeyValuePair<MapPatchKey, string>(decoy.Key, MapSurfaceSemantics.PatchDigest(decoy)) })
            });
        }
        document.NativeAssets = new() { manifestRef };
        document.NumericIdHighWaterMark = world.AllObjects().Max(o => o.Id);
        document.BuildingIntent = new(1, [], [], [], []);
        if (standalone is not null)
        {
            var archetype = catalogs.Archetype(standalone.ArchetypeId)!;
            Vector3 at = TileObjectPlacement.AnchorPosition(world, archetype, standalone);
            document.Placements.Add(new MapPlacement
            {
                Id = "standalone-" + standalone.ArchetypeId,
                Kind = standalone.ArchetypeId,
                AssetId = standalone.ArchetypeId,
                X = at.X,
                Y = at.Y,
                Z = at.Z,
                Yaw = TileObjectPlacement.YawRadians(archetype, standalone.Rotation),
                Scale = 1,
                NumericId = standalone.Id,
                Tags = new() { "first", "second" },
            });
            var roofEntry = new MapPrefabReference(MapPrefabReferenceScope.World, MapPrefabChildKind.Placement, "standalone-" + standalone.ArchetypeId);
            document.BuildingIntent = new(1, variant == "standalone-roof-linked" ? new[] { new MapRoofLinkDoc("standalone-link", roofEntry,
                new[] { new MapPrefabReference(MapPrefabReferenceScope.World, MapPrefabChildKind.Record, "b/room") }, [], 3) } : [], [], [], [])
            { Roofs = standaloneKind == "shelter" ? new[] { roofEntry } : [] };
        }
        document.Prefabs = new(1, 1, new[] { new MapPrefabInstance("b", prefabRef.Id, prefabRef.Sha256,
            new(Vector3.Zero, 0, 1), new("exterior", anchor), new(bindings), variant == "actual-override"
                ? new MapPrefabOverride[] { new MapPrefabPlacementOverride(new(MapPrefabChildKind.Placement, "lamp"), null, null, objects[2].Tags!.ToArray()) } : []) }, []);
        if (variant == "shared-concealment")
        {
            var secondBindings = bindings.Select(b => b with
            {
                WorldId = "c/" + b.Child.Key,
                NumericId = b.NumericId is null ? null : otherObjects[System.Array.FindIndex(objects, o => o.Id == b.NumericId)].Id
            }).ToArray();
            var secondInstance = document.Prefabs.Instances[0] with { Id = "c", Transform = new(new(0, 0, -2), 0, 1), Bindings = new(secondBindings) };
            document.Prefabs = new(1, 1, document.Prefabs.Instances.Append(secondInstance).ToArray(), []);
            document.BuildingIntent = new(1, [], new[] { new MapRoofGroupDoc("shared", new[]
            { new MapPrefabReference(MapPrefabReferenceScope.World, MapPrefabChildKind.Record, "b/room"),
                new MapPrefabReference(MapPrefabReferenceScope.World, MapPrefabChildKind.Record, "c/room") }) }, [], []);
        }
        File.WriteAllText(Path.Combine(root, "native.json"), MapDocumentFile.SaveText(document));
        var comparison = JsonNode.Parse("""
            {"schemaVersion":1,"nativeDocumentPath":"native.json",
            "sourceCollisionOptions":{"wallThickness":0.1,"blockedHeight":3,"walkSurfaceThickness":0.1},
            "expectations":{"definitions":1,"buildings":1,"originalLeaves":4,"connectedInteriors":1,
            "roofs":2,"openings":1,"doorwayRecords":1,"dedicatedLights":1},
            "definitions":[{"id":"cabin","path":"definitions/cabin.json","resourceId":"cabin",
            "keys":[{"key":"wall","objectIndex":0},{"key":"roof","objectIndex":1},{"key":"lamp","objectIndex":2},{"key":"shelter","objectIndex":3}]}],
            "selectedLeafIds":[1,2,3,4],"buildings":[{"id":"b","definitionId":"cabin","leafIds":[1,2,3,4],
            "roomIds":["b/room"],"storeyIds":["b/ground"],"overrides":[]}],
            "leaves":[
            {"sourceId":1,"buildingId":"b","key":"wall","worldId":"b/wall","assetId":"wall","meshPath":"assets/wall.mesh","selectionPath":"assets/wall.selection","metadata":{"source":"invented/wall","license":"CC0","category":"trees","textured":false,"sourceUnitsToMetres":1,"renderBounds":{"min":[-2,0.5,-3],"max":[2,8,3]},"lodBounds":{"min":[-1,0,-1],"max":[1,7,1]},"lightBounds":{"min":[-4,0,-4],"max":[4,9,4]}}},
            {"sourceId":2,"buildingId":"b","key":"roof","worldId":"b/roof","assetId":"roof","meshPath":"assets/roof.mesh","selectionPath":"assets/roof.selection","metadata":{"source":"invented/roof","license":"CC0","category":"trees","textured":false,"sourceUnitsToMetres":1,"renderBounds":{"min":[-2,0.5,-3],"max":[2,8,3]},"lodBounds":{"min":[-1,0,-1],"max":[1,7,1]},"lightBounds":{"min":[-4,0,-4],"max":[4,9,4]}}},
            {"sourceId":3,"buildingId":"b","key":"lamp","worldId":"b/lamp","assetId":"lamp","meshPath":"assets/lamp.mesh","selectionPath":"assets/lamp.selection","metadata":{"source":"invented/lamp","license":"CC0","category":"trees","textured":false,"sourceUnitsToMetres":1,"renderBounds":{"min":[-2,0.5,-3],"max":[2,8,3]},"lodBounds":{"min":[-1,0,-1],"max":[1,7,1]},"lightBounds":{"min":[-4,0,-4],"max":[4,9,4]}}},
            {"sourceId":4,"buildingId":"b","key":"shelter","worldId":"b/shelter","assetId":"shelter","meshPath":"assets/shelter.mesh","selectionPath":"assets/shelter.selection","metadata":{"source":"invented/shelter","license":"CC0","category":"trees","textured":false,"sourceUnitsToMetres":1,"renderBounds":{"min":[-2,0.5,-3],"max":[2,8,3]},"lodBounds":{"min":[-1,0,-1],"max":[1,7,1]},"lightBounds":{"min":[-4,0,-4],"max":[4,9,4]}}}],
            "floors":[{"buildingId":"b","x":0,"z":0,"plane":0,"underlay":1,"overlay":0,"paintId":null}],
            "rooms":[{"id":"b/room","buildingId":"b","storeyId":"b/ground","cells":[{"x":0,"z":0,"plane":0,"y":1},{"x":1,"z":0,"plane":0,"y":1},{"x":0,"z":1,"plane":0,"y":1},{"x":1,"z":1,"plane":0,"y":1}]}],
            "groups":[],"roofs":[{"leafId":2,"linkId":"b/roof-link","rooms":["b/room"],"groups":[],"aboveY":3},
            {"leafId":4,"linkId":null,"rooms":[],"groups":[],"aboveY":3}],
            "lights":[{"id":"b/indoor","ownerLeafId":3,"dedicated":true,"position":[2.5,1,-0.5],"colour":[1,1,1],"intensity":1,"radius":2,"castShadows":false},
            {"id":"b/glow","ownerLeafId":1,"dedicated":false,"position":[0.5,0.5,-0.5],"colour":[1,0.5,0.25],"intensity":0.25,"radius":1,"castShadows":false}],
            "doorwayRecords":[{"id":"frame","leafId":1,"key":"wall"}],
            "doorways":[{"id":"opening","buildingId":"b","leafIds":[1],"recordIds":["frame"],"radius":0.1,"length":1,
            "clear":[[-1,0.1,-1.5],[1,0.1,-1.5]],"jamb":[[-1,0.1,-0.5],[1,0.1,-0.5]]}],
            "probes":[{"kind":"centre","position":[0.5,1,-0.5],"sourceSpaceId":"b/room","spaceId":"b/room","hiddenRoofIds":[2]},
            {"kind":"edge","position":[2,1,-0.5],"sourceSpaceId":"exterior","spaceId":"exterior","hiddenRoofIds":[]},
            {"kind":"storey","position":[0.5,3,-0.5],"sourceSpaceId":"exterior","spaceId":"exterior","hiddenRoofIds":[]}]}
            """)!.AsObject();
        if (variant == "picking-delta")
            comparison["leaves"]![2]!["metadata"]!["renderBounds"] = JsonNode.Parse("""{"min":[-0.5,-0.1,-0.5],"max":[0.5,0.1,0.5]}""");
        if (variant is "derived-selection" or "derived-selection-box" or "derived-selection-missing")
            comparison["leaves"]![2]!["selectionPath"] = null;
        if (variant == "collider-selection") comparison["leaves"]![0]!["selectionPath"] = null;
        if (variant == "quarter-turn")
        {
            comparison["doorways"]![0]!["clear"] = JsonNode.Parse("[[1.5,0.1,0],[1.5,0.1,-2]]");
            comparison["doorways"]![0]!["jamb"] = JsonNode.Parse("[[0.5,0.1,0],[0.5,0.1,-2]]");
        }
        if (variant == "shared-concealment")
        {
            var secondBuilding = comparison["buildings"]![0]!.DeepClone();
            secondBuilding["id"] = "c";
            secondBuilding["leafIds"] = new JsonArray(otherObjects.Select(o => (JsonNode?)JsonValue.Create(o.Id)).ToArray());
            secondBuilding["roomIds"] = new JsonArray("c/room"); secondBuilding["storeyIds"] = new JsonArray("c/ground");
            comparison["buildings"]!.AsArray().Add(secondBuilding);
            var originalLeaves = comparison["leaves"]!.AsArray().ToArray();
            for (int i = 0; i < otherObjects.Length; i++)
            {
                var leaf = originalLeaves[i]!.DeepClone(); leaf["sourceId"] = otherObjects[i].Id; leaf["buildingId"] = "c";
                leaf["worldId"] = "c/" + names[i]; comparison["leaves"]!.AsArray().Add(leaf);
                comparison["selectedLeafIds"]!.AsArray().Add(otherObjects[i].Id);
            }
            var room = comparison["rooms"]![0]!.DeepClone(); room["id"] = "c/room"; room["buildingId"] = "c"; room["storeyId"] = "c/ground";
            foreach (var cell in room["cells"]!.AsArray()) cell!["z"] = cell["z"]!.GetValue<int>() + 2;
            comparison["rooms"]!.AsArray().Add(room);
            comparison["groups"]!.AsArray().Add(JsonNode.Parse("""{"id":"shared","rooms":["b/room","c/room"]}"""));
            var originalRoofs = comparison["roofs"]!.AsArray().ToArray();
            for (int i = 0; i < originalRoofs.Length; i++)
            {
                if (i == 0) originalRoofs[i]!["groups"] = new JsonArray("shared");
                var roof = originalRoofs[i]!.DeepClone(); roof["leafId"] = otherObjects[i == 0 ? 1 : 3].Id;
                if (i == 0) { roof["linkId"] = "c/roof-link"; roof["rooms"] = new JsonArray("c/room"); }
                comparison["roofs"]!.AsArray().Add(roof);
            }
            foreach (var original in comparison["lights"]!.AsArray().ToArray())
            {
                var lightRow = original!.DeepClone(); lightRow["id"] = original["id"]!.GetValue<string>().Replace("b/", "c/", StringComparison.Ordinal);
                int owner = System.Array.FindIndex(objects, o => o.Id == original["ownerLeafId"]!.GetValue<long>());
                lightRow["ownerLeafId"] = otherObjects[owner].Id; lightRow["position"]![2] = original["position"]![2]!.GetValue<float>() - 2;
                comparison["lights"]!.AsArray().Add(lightRow);
            }
            comparison["doorwayRecords"]!.AsArray().Add(JsonNode.Parse("""{"id":"other-frame","leafId":5,"key":"wall"}"""));
            var door = comparison["doorways"]![0]!.DeepClone(); door["id"] = "other-opening"; door["buildingId"] = "c";
            door["leafIds"] = new JsonArray(otherObjects[0].Id); door["recordIds"] = new JsonArray("other-frame");
            foreach (string field in new[] { "clear", "jamb" }) foreach (var point in door[field]!.AsArray()) point![2] = point[2]!.GetValue<float>() - 2;
            comparison["doorways"]!.AsArray().Add(door);
            comparison["probes"]![0]!["hiddenRoofIds"]!.AsArray().Add(otherObjects[1].Id);
            var centreProbe = comparison["probes"]![0]!.DeepClone(); centreProbe["position"]![2] = -2.5;
            centreProbe["sourceSpaceId"] = "c/room"; centreProbe["spaceId"] = "c/room"; comparison["probes"]!.AsArray().Add(centreProbe);
            foreach (var field in new[] { "buildings", "originalLeaves", "roofs", "openings", "doorwayRecords", "dedicatedLights" })
                comparison["expectations"]![field] = comparison["expectations"]![field]!.GetValue<int>() * 2;
        }
        if (standalone is not null)
        {
            // No definition and no building. The leaf lists no building, and the roof inventory names it unlinked.
            var leaf = comparison["leaves"]![3]!.DeepClone();
            string id = "standalone-" + standalone.ArchetypeId;
            leaf["sourceId"] = standalone.Id; leaf["buildingId"] = null; leaf["key"] = id; leaf["worldId"] = id;
            leaf["assetId"] = standalone.ArchetypeId; leaf["meshPath"] = "assets/" + standalone.ArchetypeId + ".mesh";
            leaf["selectionPath"] = "assets/" + standalone.ArchetypeId + ".selection";
            leaf["metadata"]!["source"] = "invented/" + standalone.ArchetypeId;
            comparison["leaves"]!.AsArray().Add(leaf);
            comparison["selectedLeafIds"]!.AsArray().Add(standalone.Id);
            comparison["expectations"]!["originalLeaves"] = 5;
            if (standaloneKind == "shelter")
            {
                comparison["roofs"]!.AsArray().Add(variant == "standalone-roof-linked"
                    ? JsonNode.Parse("{\"leafId\":" + standalone.Id + ",\"linkId\":\"standalone-link\",\"rooms\":[\"b/room\"],\"groups\":[],\"aboveY\":3}")
                    : JsonNode.Parse("{\"leafId\":" + standalone.Id + ",\"linkId\":null,\"rooms\":[],\"groups\":[],\"aboveY\":3}"));
                comparison["expectations"]!["roofs"] = 3;
            }
        }
        if (variant == "actual-override") comparison["buildings"]![0]!["overrides"]!.AsArray().Add(JsonNode.Parse("""{"kind":"Placement","key":"lamp","operation":"placement"}"""));
        switch (variant)
        {
            case "unassigned-leaf": comparison["leaves"]!.AsArray().RemoveAt(0); break;
            case "duplicate-leaf": comparison["leaves"]!.AsArray().Add(comparison["leaves"]![0]!.DeepClone()); break;
            case "partial-room": comparison["rooms"]![0]!["cells"]!.AsArray().RemoveAt(0); break;
            case "dangling-roof": comparison["roofs"]![0]!["rooms"]![0] = PublicSecretMarker; break;
            case "escape-path": comparison["leaves"]![0]!["meshPath"] = "../" + PublicSecretMarker; break;
            case "missing-definition-key": comparison["definitions"]![0]!["keys"]!.AsArray().RemoveAt(0); break;
            case "missing-doorway": comparison["doorways"]!.AsArray().Clear(); break;
            case "missing-light-owner": comparison["lights"]![0]!["ownerLeafId"] = 999; break;
            case "membership-centre": comparison["probes"]![0]!["spaceId"] = "exterior"; break;
            case "floor-paint": comparison["floors"]![0]!["underlay"] = 2; break;
            case "doorway-clearance": comparison["doorways"]![0]!["clear"] = comparison["doorways"]![0]!["jamb"]!.DeepClone(); break;
            case "expectation-count": comparison["expectations"]!["dedicatedLights"] = 2; break;
        }
        return comparison;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
