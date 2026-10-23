using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

public sealed partial class ShippedSceneInputGuardTests
{
    [Theory]
    [InlineData("tiled-only", 2)]
    [InlineData("valid", 4)]
    public void SceneStorage_BootstrapsVerifiedTiledWithOptionalMonolithicParity(string variant, int heads)
    {
        using var fixture = ShippedSceneGuardFixtures.Create(variant);
        var inputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        Assert.Equal(variant == "tiled-only", inputs.Manifest.NativeDocumentPath is null);
        Assert.NotNull(inputs.Reload(true).Tiles);
        PrivateOracleEntry.Run(fixture.Environment, (_, report) => ShippedSceneComparison.Compare(inputs, report));
        var summary = File.ReadLines(fixture.ReportPath).Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .Single(r => r.GetProperty("category").GetString() == "scene-summary");
        Assert.Equal(heads, summary.GetProperty("detail").GetProperty("heads").GetInt32());
        Assert.Empty(fixture.FailureCategories());
    }

    [Theory]
    [InlineData("monolithic-mismatch")]
    [InlineData("missing-tiled")]
    [InlineData("null-tiled")]
    [InlineData("corrupt-tiled")]
    public void SceneStorage_RejectsDisagreeingOrUnverifiedForms(string variant)
    {
        using var fixture = ShippedSceneGuardFixtures.Create(variant);
        Assert.Throws<InvalidDataException>(() => ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment)));
    }

    [Fact]
    public void SceneSelection_CatalogedUnplacedHlodLoadsAndDrawsNothingOnBothHeads()
    {
        using var fixture = ShippedSceneGuardFixtures.Create("unplaced-selection");
        var inputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        var legacy = LegacySceneRecorder.Record(inputs.World, inputs.Catalogs, inputs);
        Assert.Single(legacy.Placements);
        var document = inputs.Reload(true); var assets = inputs.Assets(document);
        using var docs = MapDocumentSource.FromDocument(document);
        var source = MapSceneSource.Capture(docs, assets).Source!;
        var scene = source.Acquire(ShippedSceneInputs.Scope(inputs.Manifest.Windows[0])).Scene!;
        var routing = MapPlacementRouting.Create(scene, inputs.NativePolicy.Placements, inputs.NativePolicy.AnimatedAssets);
        Assert.Equal(MapSceneStatus.CompleteForScope, routing.Status);
        Assert.Single(routing.Routing!.Entries);
        Assert.DoesNotContain(scene.Placements, p => p.AssetId == "unused-column");
        PrivateOracleEntry.Run(fixture.Environment, (_, report) => ShippedSceneComparison.Compare(inputs, report));
        Assert.Empty(fixture.FailureCategories());
    }
}
