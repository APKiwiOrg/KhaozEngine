using System.Linq;
using System;
using System.IO;
using KhaozEngine.MapDoc.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

public sealed partial class ShippedSceneInputGuardTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("coincident-valid")]
    [InlineData("foliage-valid")]
    [InlineData("water-valid")]
    [InlineData("light-valid")]
    [InlineData("animated-valid")]
    public void SceneFullPath_UnchangedProducerOutputPasses(string variant) => CheckUnchanged(variant);

    [Fact]
    public void SceneFullPath_StandaloneRoofFixturePasses() => CheckUnchanged("roof-valid");

    [Theory]
    [InlineData("roof-hlod")]
    [InlineData("roof-hlod-no-shadows")]
    public void SceneFullPath_RoofHlodPasses(string variant) => CheckUnchanged(variant);

    static void CheckUnchanged(string variant)
    {
        using var fixture = ShippedSceneGuardFixtures.Create(variant);
        var inputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        try { PrivateOracleEntry.Run(fixture.Environment, (_, report) => ShippedSceneComparison.Compare(inputs, report)); }
        catch (PrivateOracleFailure)
        {
            // Only this invented fixture is allowed through this public diagnostic path.
            Assert.Fail(string.Join("\n", File.ReadLines(fixture.ReportPath).Where(line => !line.StartsWith("{\"category\":\"scene-buildings\"", StringComparison.Ordinal)).Take(3))
                .Replace(fixture.SourceRoot, "<fixture>", StringComparison.Ordinal).Replace(fixture.PublicSecretMarker, "<sentinel>", StringComparison.Ordinal));
        }
        Assert.Empty(fixture.FailureCategories());
    }

    [Theory]
    [InlineData("valid", "extra-part", "scene-placement-frame")]
    [InlineData("coincident-valid", "missing-coincident", "scene-placement-frame")]
    [InlineData("foliage-valid", "missing-generation", "scene-foliage-key")]
    [InlineData("foliage-valid", "missing-row", "scene-foliage-key")]
    [InlineData("valid", "suppress-Terrain", "scene-terrain-frame")]
    [InlineData("light-valid", "suppress-Light", "scene-light-frame")]
    [InlineData("water-valid", "suppress-Water", "scene-water-frame")]
    [InlineData("foliage-valid", "suppress-Foliage", "scene-foliage-frame")]
    [InlineData("foliage-valid", "maps-albedo", "scene-foliage-registration")]
    [InlineData("foliage-valid", "maps-normal", "scene-foliage-registration")]
    [InlineData("foliage-valid", "maps-roughness", "scene-foliage-registration")]
    [InlineData("foliage-valid", "maps-alpha", "scene-foliage-registration")]
    public void SceneFullPath_RejectsProducerOmissionsAndChanges(string fixtureVariant, string mutation, string category) => RejectFullPath(fixtureVariant, mutation, category);

    [Theory]
    [InlineData("roof-valid", "roof-camera", "scene-roof-channel")]
    [InlineData("roof-valid", "roof-sky", "scene-roof-channel")]
    public void SceneFullPath_RoofChannelsAreRequired(string fixtureVariant, string mutation, string category) => RejectFullPath(fixtureVariant, mutation, category);

    [Theory]
    [InlineData("animated-valid", "maps-albedo", "scene-foliage-registration")]
    [InlineData("animated-valid", "maps-normal", "scene-foliage-registration")]
    [InlineData("animated-valid", "maps-roughness", "scene-foliage-registration")]
    [InlineData("animated-valid", "maps-alpha", "scene-foliage-registration")]
    public void SceneFullPath_ExplicitMaterialsAreRequired(string fixtureVariant, string mutation, string category) => RejectFullPath(fixtureVariant, mutation, category);

    static void RejectFullPath(string fixtureVariant, string mutation, string category)
    {
        using var fixture = ShippedSceneGuardFixtures.Create(fixtureVariant);
        int changes = 0;
        bool suppress = mutation.StartsWith("suppress-", StringComparison.Ordinal);
        var kind = suppress ? Enum.Parse<MapRenderKind>(mutation[9..]) : default;
        var failure = Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(fixture.Environment, (inputs, report) =>
            ShippedSceneComparison.Compare(ShippedSceneInputs.Read(inputs), report, observation =>
            {
                changes++;
                if (!suppress) return ShippedSceneGuardObservations.Mutate(observation, mutation);
                Assert.Contains(observation.DrawSet.Resources, r => r.Reference.Key.Kind == kind);
                Assert.DoesNotContain(observation.DrawSet.Draws, d => d.Resource.Key.Kind == kind);
                return observation;
            }, sink => suppress ? new ShippedSceneGuardObservations.SuppressWrites(sink, kind) : sink)));
        Assert.True(changes > 0);
        Assert.Contains(category, fixture.FailureCategories());
        Assert.DoesNotContain(fixture.PublicSecretMarker, failure.ToString());
    }
}
