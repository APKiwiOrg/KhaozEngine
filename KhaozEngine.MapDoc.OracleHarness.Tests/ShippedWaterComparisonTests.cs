using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using KhaozEngine.Tests.MapDocCompatibility;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
public sealed class ShippedWaterComparisonTests
{
    [Fact]
    public void ShippedWater_IsExpressibleWithBoundedBodies() => PrivateOracleEntry.Run((inputs, report) =>
    {
        // inputs.SourceRoot was verified against the private provenance inside Require.
        var (document, catalogs) = ShippedSourceInventory.ReadWorld(inputs);
        var (native, sources) = LegacyWaterConverter.ToNative(document, catalogs);
        LegacyWaterReport water = LegacyWaterComparison.Compare(native, sources);
        report.Record("water", JsonSerializer.Serialize(new
        {
            bodies = water.Bodies,
            rects = water.Rects,
            cells = water.Cells,
            boundarySamples = water.BoundarySamples,
            boundaryOwner = water.Differences.Count(d => d.Kind == LegacyWaterDifferenceKind.BoundaryOwner),
            floorAboveSurface = water.Differences.Count(d => d.Kind == LegacyWaterDifferenceKind.FloorAboveSurface),
        }));
        ShippedWaterFailures.Report(report, water);
    });
}
