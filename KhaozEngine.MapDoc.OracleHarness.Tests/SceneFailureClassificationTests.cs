using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
public sealed class SceneFailureClassificationTests
{
    [Fact]
    public void SourceMeshFailures_RemainPlacementPartsBeyondDetailLimit()
    {
        using var fixture = ShippedSceneGuardFixtures.Create("native-mesh", json =>
        {
            var windows = json["windows"]!.AsArray();
            foreach (string id in new[] { "D", "E", "F" })
            {
                var window = windows[0]!.DeepClone(); window["id"] = id; windows.Add(window);
            }
            json["expectations"]!["windows"] = 6;
        });
        var inputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(fixture.Environment,
            (_, report) => ShippedSceneComparison.Compare(inputs, report)));
        AssertClassified(fixture, "scene-placement-part", "Placement", "part", "mesh", 24);
    }

    [Fact]
    public void FoliageRowFailures_RemainInstanceRanksBeyondDetailLimit()
    {
        using var fixture = ShippedSceneGuardFixtures.Create("foliage-valid");
        var inputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(fixture.Environment,
            (_, report) => ShippedSceneComparison.Compare(inputs, report, observe: observation => observation with
            {
                DrawSet = new(observation.DrawSet.Resources.Select(resource => resource.Payload is MapFoliageResource payload
                    ? resource with
                    {
                        Payload = new MapFoliageResource(payload.Anchor, payload.WorldTransforms,
                        payload.Ranks.ToArray().Select(rank => rank == 0 ? .5f : 0f).ToArray(), payload.ModelIndices, payload.Models)
                    }
                    : resource).ToArray(), observation.DrawSet.Draws)
            })));
        AssertClassified(fixture, "scene-foliage", "Foliage", "instances", "rank");
    }

    [Fact]
    public void ModelMaterialFailures_RemainRigidFoliagePartsBeyondDetailLimit()
    {
        using var fixture = ShippedSceneGuardFixtures.Create("foliage-valid");
        var inputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        var parts = GltfLoader.LoadPartsWithMaterials(inputs.OriginalMeshes[inputs.SourceModelPaths["column"]], "synthetic source model");
        var source = Assert.Single(parts); var maps = source.Maps;
        var resource = ShippedSceneGuardDraws.Create().Resources[0];
        resource = resource with
        {
            Reference = resource.Reference with { Key = new(MapRenderKind.Foliage, "native-column", "model-part/0", 0) },
            Payload = new MapRigidResource(source.Mesh, new(maps.Albedo, maps.Normal, maps.Roughness, maps.AlphaCutoff + .25f))
        };
        var policy = GroundCoverModelPolicy.Resolve(inputs.SourcePolicy.Foliage, "column");
        var model = new MapFoliageModelBinding("native-column", [resource.Reference], policy.Motion, policy.CastShadows);
        Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(fixture.Environment, (_, report) =>
        {
            for (int i = 0; i < 24; i++) ShippedSceneFoliageRoster.CheckModel(inputs, "column", model, parts, [resource], report);
        }));
        AssertClassified(fixture, "scene-foliage-registration", "Foliage", "rigid-model-part", "material-maps", 24);
    }

    [Fact]
    public void StreamPayloadFailures_RecogniseModelPartKeysBeyondDetailLimit()
    {
        using var fixture = ShippedSceneGuardFixtures.Create("valid");
        var resource = ShippedSceneGuardDraws.Create().Resources[0];
        resource = resource with { Reference = resource.Reference with { Key = new(MapRenderKind.Foliage, "native-column", "model-part/0", 0) } };
        var mesh = (MapRigidResource)resource.Payload; var vertices = mesh.Mesh.Vertices.ToArray();
        vertices[0].Position.X += .0001f;
        var changed = resource with { Payload = new MapRigidResource(new(vertices, mesh.Mesh.Indices32.ToArray()), mesh.Maps) };
        var baseline = SceneStreamComparison.Capture(resource);
        Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(fixture.Environment, (_, report) =>
        {
            for (int i = 0; i < 24; i++) SceneStreamComparison.Union([baseline], [resource, changed], report);
        }));
        AssertClassified(fixture, "scene-stream-union", "Foliage", "rigid-model-part", "payload", 24);
    }

    static void AssertClassified(ShippedSceneGuardFixture fixture, string category, string kind, string keyClass, string field, int? expectedCount = null)
    {
        var records = File.ReadAllLines(fixture.ReportPath).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        var summary = Assert.Single(records, row => row.GetProperty("category").GetString() == "scene-failure-summary" &&
            row.GetProperty("detail").GetProperty("category").GetString() == category).GetProperty("detail");
        int total = summary.GetProperty("total").GetInt32();
        Assert.True(total > 16);
        if (expectedCount is { } count) Assert.Equal(count, total);
        Assert.Equal(16, records.Count(row => row.GetProperty("category").GetString() == category && row.GetProperty("detail").EnumerateObject().Any()));
        var classes = summary.GetProperty("classifications").EnumerateArray().ToArray();
        Assert.Equal(total, classes.Sum(row => row.GetProperty("count").GetInt32()));
        Assert.All(classes, row =>
        {
            Assert.Equal(kind, row.GetProperty("resourceKind").GetString());
            Assert.Equal(keyClass, row.GetProperty("keyClass").GetString());
            Assert.Equal(field, row.GetProperty("differingField").GetString());
        });
    }
}
