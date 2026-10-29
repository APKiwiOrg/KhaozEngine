using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using KhaozEngine.Tests.MapDocCompatibility;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
public sealed class CompositeFailureClassificationTests
{
    [Fact]
    public void WaterComposite_ForwardsEachRealFailureWithItsField()
    {
        using var fixture = ShippedSceneGuardFixtures.Create("water-valid");
        var inputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        var (native, sources) = LegacyWaterConverter.ToNative(inputs.World, inputs.Catalogs);
        native.Water = null;
        var compared = LegacyWaterComparison.Compare(native, sources);
        Assert.Single(compared.Failures);
        Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(fixture.Environment, (_, report) =>
        {
            for (int i = 0; i < 24; i++) ShippedWaterFailures.Report(report, compared);
        }));
        var summary = File.ReadAllLines(fixture.ReportPath).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Single(row => row.GetProperty("category").GetString() == "scene-failure-summary").GetProperty("detail");
        Assert.Equal(24, summary.GetProperty("total").GetInt32());
        var classification = Assert.Single(summary.GetProperty("classifications").EnumerateArray());
        Assert.Equal("Water", classification.GetProperty("resourceKind").GetString());
        Assert.Equal("section", classification.GetProperty("keyClass").GetString());
        Assert.Equal("presence", classification.GetProperty("differingField").GetString());
    }

    [Fact]
    public void TerrainResourceCheck_HasTypedFieldClassification()
    {
        using var fixture = ShippedSceneGuardFixtures.Create("valid");
        var world = new TileWorldDocument { TileSize = 2, PlaneCount = 4 };
        world.GetOrCreateRegion(new(0, 0));
        Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(fixture.Environment,
            (_, report) => ShippedTerrainComparison.CompareRegion(new(new(0, 0), world), report)));
        var summary = File.ReadAllLines(fixture.ReportPath).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Single(row => row.GetProperty("category").GetString() == "scene-failure-summary" &&
                row.GetProperty("detail").GetProperty("category").GetString() == "tile-size").GetProperty("detail");
        var classification = Assert.Single(summary.GetProperty("classifications").EnumerateArray());
        Assert.Equal("Terrain", classification.GetProperty("resourceKind").GetString());
        Assert.Equal("surface", classification.GetProperty("keyClass").GetString());
        Assert.Equal("tile-size", classification.GetProperty("differingField").GetString());
        Assert.Equal(1, summary.GetProperty("total").GetInt32());
    }

    [Fact]
    public void FoliageTally_ForwardsTrueCountsForEachResourceField()
    {
        using var fixture = ShippedFoliageGuardFixtures.Create("changed-decision");
        Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(fixture.Environment,
            (inputs, report) => ShippedFoliageComparison.Compare(ShippedFoliageInputs.Read(inputs), report)));
        var records = File.ReadAllLines(fixture.Environment["KHAOZ_R2_ORACLE_REPORT"]!).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        var expected = records.Single(row => row.GetProperty("category").GetString() == "foliage-summary")
            .GetProperty("detail").GetProperty("failures").EnumerateObject().ToArray();
        Assert.Contains(expected, failure => failure.Value.GetInt64() > 16);
        foreach (var failure in expected)
        {
            var summary = Assert.Single(records, row => row.GetProperty("category").GetString() == "scene-failure-summary" &&
                row.GetProperty("detail").GetProperty("category").GetString() == failure.Name).GetProperty("detail");
            Assert.Equal(failure.Value.GetInt64(), summary.GetProperty("total").GetInt64());
            Assert.Equal(failure.Value.GetInt64(), summary.GetProperty("classifications").EnumerateArray().Sum(row => row.GetProperty("count").GetInt64()));
            if (!failure.Name.StartsWith("foliage-", System.StringComparison.Ordinal) || failure.Name == "foliage-head") continue;
            Assert.All(summary.GetProperty("classifications").EnumerateArray(), row => Assert.Equal("Foliage", row.GetProperty("resourceKind").GetString()));
        }
    }

    [Theory]
    [InlineData(false, 48)]
    [InlineData(true, 2112)]
    public void MarkerComposites_ReportEveryResourceFieldBeyondDetailLimit(bool fullScene, int expectedCount)
    {
        using var fixture = ShippedSceneGuardFixtures.Create("marker-many-moved");
        var inputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        Assert.Empty(ShippedMarkerComparison.Compare(inputs.Markers!).Failures);
        var document = inputs.Reload(true);
        Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(fixture.Environment, (_, report) =>
        {
            if (fullScene) ShippedSceneComparison.Compare(inputs, report);
            else ShippedSceneMarkers.Head(inputs, document, inputs.Assets(document), report);
        }));

        var records = File.ReadAllLines(fixture.ReportPath).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        var summary = Assert.Single(records, row => row.GetProperty("category").GetString() == "scene-failure-summary").GetProperty("detail");
        Assert.Equal("scene-marker", summary.GetProperty("category").GetString());
        Assert.Equal(expectedCount, summary.GetProperty("total").GetInt32());
        var classes = summary.GetProperty("classifications").EnumerateArray().ToArray();
        Assert.Equal(new[] { "position", "values" }, classes.Select(row => row.GetProperty("differingField").GetString()));
        Assert.All(classes, row =>
        {
            Assert.Equal("Marker", row.GetProperty("resourceKind").GetString());
            Assert.Equal("marker", row.GetProperty("keyClass").GetString());
            Assert.Equal(expectedCount / 2, row.GetProperty("count").GetInt32());
        });
        Assert.Equal(16, records.Count(row => row.GetProperty("category").GetString() == "scene-marker" && row.GetProperty("detail").EnumerateObject().Any()));
    }
}
