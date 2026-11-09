using System.Runtime.Versioning;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
public sealed class ShippedSceneComparisonTests
{
    [Fact]
    public void ShippedScene_DrawSetsAndStreamingPreserveEveryResource() => PrivateOracleEntry.Run((inputs, report) =>
        ShippedSceneComparison.Compare(ShippedSceneInputs.Read(inputs), report));
}
