using System;
using System.IO;
using System.Runtime.Versioning;
using System.Linq;
using System.Text.Json;
using KhaozEngine.MapDoc.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
public sealed class SceneStreamComparisonTests
{
    [Fact]
    public void StreamAliases_RigidFoliageAllowsWindowSpecificEnvelopes()
    {
        using var fixture = ShippedSceneGuardFixtures.Create("valid");
        var resource = ShippedSceneGuardDraws.Create().Resources[0];
        resource = resource with { Reference = resource.Reference with { Key = resource.Reference.Key with { Kind = MapRenderKind.Foliage } } };
        var other = resource with { Bounds = resource.Bounds with { MaxX = 2 } };
        PrivateOracleEntry.Run(fixture.Environment, (_, report) => SceneStreamComparison.Union([SceneStreamComparison.Capture(resource)], [resource, other], report));
        Assert.Empty(fixture.FailureCategories());
    }

    [Theory]
    [InlineData("rigid-payload")]
    [InlineData("placement-bounds")]
    [InlineData("instance-bounds")]
    public void StreamAliases_RejectsChangedIdentityDespiteMatchingClaimedDigest(string mutation)
    {
        using var fixture = ShippedSceneGuardFixtures.Create("valid");
        var set = ShippedSceneGuardDraws.Create();
        var resource = mutation == "instance-bounds" ? set.Resources.Last() : set.Resources[0];
        if (mutation == "rigid-payload")
            resource = resource with { Reference = resource.Reference with { Key = resource.Reference.Key with { Kind = MapRenderKind.Foliage } } };
        var other = resource with { Bounds = resource.Bounds with { MaxX = 2 } };
        if (mutation == "rigid-payload")
        {
            var rigid = (MapRigidResource)resource.Payload; var vertices = rigid.Mesh.Vertices.ToArray();
            vertices[0].Position.X += .0001f;
            other = resource with { Payload = new MapRigidResource(new(vertices, rigid.Mesh.Indices32.ToArray()), rigid.Maps) };
        }
        Assert.Equal(resource.PayloadDigest, other.PayloadDigest);
        Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(fixture.Environment,
            (_, report) => SceneStreamComparison.Union([SceneStreamComparison.Capture(resource)], [resource, other], report)));
        Assert.Contains("scene-stream-union", fixture.FailureCategories());
    }

    [Fact]
    public void StreamWindow_RejectsChangedRigidFoliageEnvelope()
    {
        using var fixture = ShippedSceneGuardFixtures.Create("valid");
        var resource = ShippedSceneGuardDraws.Create().Resources[0];
        resource = resource with { Reference = resource.Reference with { Key = resource.Reference.Key with { Kind = MapRenderKind.Foliage } } };
        var baseline = new[] { resource }.ToDictionary(r => r.Reference.Key, SceneStreamComparison.Capture);
        var changed = resource with { Bounds = resource.Bounds with { MaxX = 2 } };
        Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(fixture.Environment,
            (_, report) => SceneStreamComparison.Window(baseline, [changed], report)));
        Assert.Contains("window-bounds", File.ReadAllText(fixture.ReportPath));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("payload")]
    public void StreamWindow_RejectsLostDuplicateOrChangedRows(string mutation)
    {
        using var fixture = ShippedSceneGuardFixtures.Create("valid");
        var resources = ShippedSceneGuardDraws.Create().Resources;
        var baseline = resources.ToDictionary(r => r.Reference.Key, SceneStreamComparison.Capture);
        var changed = resources.ToList();
        if (mutation == "missing") changed.RemoveAt(0);
        if (mutation == "duplicate") changed.Add(changed[0]);
        if (mutation == "payload")
        {
            var water = (MapWaterResource)changed[4].Payload;
            changed[4] = changed[4] with { Payload = new MapWaterResource(water.Mesh, new byte[] { 9 }) };
        }
        Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(fixture.Environment,
            (_, report) => SceneStreamComparison.Window(baseline, changed, report)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StreamUnion_RejectsMissingOrExtraKeys(bool extra)
    {
        using var fixture = ShippedSceneGuardFixtures.Create("valid");
        var resources = ShippedSceneGuardDraws.Create().Resources;
        var baseline = resources.Select(SceneStreamComparison.Capture).ToArray();
        var changed = resources.ToList();
        if (extra) changed.Add(changed[0] with { Reference = changed[0].Reference with { Key = changed[0].Reference.Key with { OwnerId = "extra" } } });
        else changed.RemoveAt(0);
        Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(fixture.Environment,
            (_, report) => SceneStreamComparison.Union(baseline, changed, report)));
    }

    [Fact]
    public void FailureAggregation_ClassifiesEveryFailureBeyondBoundedExamples()
    {
        using var fixture = ShippedSceneGuardFixtures.Create("valid");
        var key = ShippedSceneGuardDraws.Create().Resources[0].Reference.Key;
        Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(fixture.Environment, (_, report) =>
        {
            for (int i = 0; i < 40; i++) ShippedSceneComparison.Check(report, "scene-stream-union", false, new { key, differingField = "payload" });
            for (int i = 0; i < 5; i++) ShippedSceneComparison.Check(report, "scene-stream-union", false, new { key, differingField = "bounds" });
            ShippedSceneComparison.Check(report, "scene-stream-union", false, new { differingField = "window-coverage" });
        }));
        var rows = File.ReadAllLines(fixture.ReportPath).Select(l => JsonSerializer.Deserialize<JsonElement>(l)).ToArray();
        Assert.Equal(16, rows.Count(r => r.GetProperty("category").GetString() == "scene-stream-union" && r.GetProperty("detail").EnumerateObject().Any()));
        var summary = Assert.Single(rows, r => r.GetProperty("category").GetString() == "scene-failure-summary").GetProperty("detail");
        Assert.Equal("scene-stream-union", summary.GetProperty("category").GetString());
        Assert.Equal(46, summary.GetProperty("total").GetInt32());
        var classes = summary.GetProperty("classifications").EnumerateArray().ToArray();
        Assert.Equal(46, classes.Sum(c => c.GetProperty("count").GetInt32()));
        Assert.Contains(classes, c => c.GetProperty("differingField").GetString() == "payload" && c.GetProperty("count").GetInt32() == 40 &&
            c.GetProperty("resourceKind").GetString() == "Placement" && c.GetProperty("keyClass").GetString() == "part");
        Assert.Contains(classes, c => c.GetProperty("differingField").GetString() == "bounds" && c.GetProperty("count").GetInt32() == 5);
        Assert.All(classes, c => Assert.All(new[] { "resourceKind", "keyClass", "differingField" }, f => Assert.False(string.IsNullOrWhiteSpace(c.GetProperty(f).GetString()))));
    }
}
