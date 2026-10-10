using System.Runtime.Versioning;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
public sealed class ShippedTerrainComparisonTests
{
    [Fact]
    public void ShippedTerrain_RemainsExactWithCaveModel() => PrivateOracleEntry.Run((inputs, report) =>
    {
        // inputs.SourceRoot was verified against the private provenance inside Require.
        foreach (ShippedRegion region in ShippedSourceInventory.ReadRegions(inputs))
            ShippedTerrainComparison.CompareRegion(region, report);   // only report.Check and report.Record, never Assert on source values
    });
}
