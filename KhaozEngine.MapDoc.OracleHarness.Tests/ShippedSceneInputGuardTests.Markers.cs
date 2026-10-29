using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

public sealed partial class ShippedSceneInputGuardTests
{
    [Fact]
    public void SceneFullPath_SourceMarkersPass() => CheckUnchanged("marker-valid");

    [Fact]
    public void SceneMarkers_UseRoomExpectationIndependentOfLinkedOutside() => CheckUnchanged("marker-scene-room");

    [Theory]
    [InlineData("sceneSpaceId")]
    [InlineData("sceneParents")]
    [InlineData("spaceId")]
    [InlineData("parents")]
    public void SceneMarkers_RequireBothMembershipPairs(string field)
    {
        using var fixture = ShippedSceneGuardFixtures.Create("marker-missing-" + field);
        Assert.Throws<System.IO.InvalidDataException>(() => ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment)));
    }

    [Theory]
    [InlineData("marker-omitted")]
    [InlineData("marker-moved")]
    [InlineData("marker-wrong-scene-room")]
    [InlineData("marker-wrong-scene-parent")]
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
