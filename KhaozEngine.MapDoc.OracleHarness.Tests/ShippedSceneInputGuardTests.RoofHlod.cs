using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

public sealed partial class ShippedSceneInputGuardTests
{
    [Theory]
    [InlineData("roof-proxy-omission", "scene-roof-channel")]
    [InlineData("roof-hidden-colour", "scene-roof-channel")]
    [InlineData("roof-missing-shadow", "scene-roof-channel")]
    [InlineData("roof-complement", "scene-rigid-frame")]
    [InlineData("roof-wrong-kind", "scene-roof-channel")]
    [InlineData("roof-incomplete-cell", "scene-roof-channel")]
    public void SceneFullPath_RejectsRoofHlodMutations(string mutation, string category) =>
        RejectFullPath("roof-hlod", mutation, category);
}
