using System.Runtime.Versioning;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
public sealed class ShippedFoliageComparisonTests
{
    // Private inputs only. Without them the entry refuses, it never skips.
    [Fact]
    public void ShippedFoliage_PreservesEveryByteCandidatePoseAndDecision() => PrivateOracleEntry.Run((inputs, report) =>
        ShippedFoliageComparison.Compare(ShippedFoliageInputs.Read(inputs), report));
}
