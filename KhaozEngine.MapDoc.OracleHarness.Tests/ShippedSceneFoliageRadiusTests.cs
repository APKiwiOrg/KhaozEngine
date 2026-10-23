using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.Versioning;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
public sealed class ShippedSceneFoliageRadiusTests
{
    [Theory]
    [InlineData(4, 1)]
    [InlineData(0, 1)]
    [InlineData(4, 0)]
    [InlineData(0, 0)]
    public void ExplicitPlantExpectation_UsesLegacyPlacementRadiusWithCoverStyle(float coverRadius, float density)
    {
        using var fixture = ShippedSceneGuardFixtures.Create("animated-valid");
        var inputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        var options = inputs.SourcePolicy.Foliage;
        options.DrawRadius = coverRadius;
        options.QualityDensity = density;
        options.DistantDensity = density;
        options.DensityRadius = 2;
        options.FadeBandWidth = 1;
        options.InstanceFadeBandWidth = .5f;
        options.WindFadeBladePixels = 2;
        options.WindStrength = .7f;
        var source = LegacySceneRecorder.Record(inputs.World, inputs.Catalogs, inputs);
        var probe = inputs.Manifest.Policy.Probes[0];
        var frame = source.Frames.Single(f => f.ProbeId == probe.Id) with { Focus = ShippedSceneInputs.Point(20, 0, 0) };
        var scope = ShippedSceneInputs.Scope(inputs.Manifest.Windows.Single(w => w.Id == probe.WindowId));
        var expected = ShippedSceneExpectedFrames.Capture(inputs, source, frame, probe, scope, [],
            new Dictionary<MapRenderKey, MapSubmissionAnchor>(), new Dictionary<MapRenderKey, MapSubmissionAnchor>());
        var plant = Assert.Single(expected, e => e.Draw.Foliage is not null).Draw;
        Assert.Equal(new MapFoliageFrameOptions(100, 2, 1, 1, 1, .5f, GroundCoverFadeMode.HeightScale, 2), plant.Foliage!.Value.Options);
        Assert.Equal(.7f, plant.Foliage.Value.State.WindStrength);
        float distance = new Vector2(plant.LocalFocus.X, plant.LocalFocus.Z).Length();
        Assert.InRange(distance, coverRadius + 1, 99);
    }

    [Fact]
    public void GeneratedCoverExpectation_KeepsItsIndependentRadiusAndDensity()
    {
        using var fixture = ShippedSceneGuardFixtures.Create("foliage-valid");
        var inputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        inputs.SourcePolicy.Foliage.DrawRadius = 4;
        inputs.SourcePolicy.Foliage.QualityDensity = .25f;
        inputs.SourcePolicy.Foliage.DistantDensity = .125f;
        var source = LegacySceneRecorder.Record(inputs.World, inputs.Catalogs, inputs);
        var probe = inputs.Manifest.Policy.Probes[0];
        var scope = ShippedSceneInputs.Scope(inputs.Manifest.Windows.Single(w => w.Id == probe.WindowId));
        var foliage = LegacySceneRecorder.RecordFoliage(inputs, scope);
        var expected = ShippedSceneExpectedFrames.Capture(inputs, source, source.Frames.Single(f => f.ProbeId == probe.Id), probe, scope, foliage,
            new Dictionary<MapRenderKey, MapSubmissionAnchor>(), new Dictionary<MapRenderKey, MapSubmissionAnchor>());
        var cover = expected.Where(e => e.Draw.Foliage is not null).ToArray();
        Assert.NotEmpty(cover);
        Assert.All(cover, e =>
        {
            Assert.Equal(4, e.Draw.Foliage!.Value.Options.DrawRadius);
            Assert.Equal(.25f, e.Draw.Foliage.Value.Options.QualityDensity);
            Assert.Equal(.125f, e.Draw.Foliage.Value.Options.DistantDensity);
        });
    }
}
