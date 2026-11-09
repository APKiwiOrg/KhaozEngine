using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

public sealed partial class ShippedSceneInputGuardTests
{
    [Fact]
    public void SceneFullPath_SourceWaterGeometryPasses() => CheckUnchanged("water-valid");

    [Theory]
    [InlineData("water-height", "scene-water-geometry")]
    [InlineData("water-offset", "scene-water-geometry")]
    [InlineData("water-clip", "scene-water-clip")]
    public void SceneFullPath_RejectsWaterPayloadChanges(string mutation, string category) =>
        RejectFullPath("water-valid", mutation, category);
}
