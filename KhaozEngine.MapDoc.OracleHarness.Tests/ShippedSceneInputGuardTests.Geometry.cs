using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

public sealed partial class ShippedSceneInputGuardTests
{
    [Fact]
    public void SceneFullPath_SourceHardAndFeatherPaintPasses() => CheckUnchanged("terrain-painted");

    [Theory]
    [InlineData("terrain-height", "scene-terrain-geometry")]
    [InlineData("terrain-normal", "scene-terrain-geometry")]
    [InlineData("terrain-weights", "scene-terrain-paint")]
    [InlineData("terrain-slots", "scene-terrain-paint")]
    public void SceneFullPath_RejectsTerrainPayloadChanges(string mutation, string category) =>
        RejectFullPath("valid", mutation, category);
}
