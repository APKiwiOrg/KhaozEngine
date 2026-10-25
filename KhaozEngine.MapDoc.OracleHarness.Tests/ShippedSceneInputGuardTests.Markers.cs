using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

public sealed partial class ShippedSceneInputGuardTests
{
    [Fact]
    public void SceneFullPath_SourceMarkersPass() => CheckUnchanged("marker-valid");

    [Theory]
    [InlineData("marker-omitted")]
    [InlineData("marker-moved")]
    public void SceneFullPath_RejectsSceneMarkersDespiteValidLinkedScaffold(string variant)
    {
        using var fixture = ShippedSceneGuardFixtures.Create(variant);
        var inputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        Assert.NotNull(inputs.Markers);
        Assert.Empty(ShippedMarkerComparison.Compare(inputs.Markers).Failures);
        Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(fixture.Environment,
            (_, report) => ShippedSceneComparison.Compare(inputs, report)));
        Assert.Contains("scene-marker", fixture.FailureCategories());
    }
}
