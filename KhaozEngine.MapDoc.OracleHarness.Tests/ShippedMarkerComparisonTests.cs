using System.Runtime.Versioning;
using System.Text.Json;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
public sealed class ShippedMarkerComparisonTests
{
    [Fact]
    public void ShippedMarkers_PreserveNamesTagsStandingPointsAndHeight() => PrivateOracleEntry.Run((inputs, report) =>
    {
        var markers = ShippedMarkerInputs.Read(inputs);
        markers.RequireBoundImports();
        var comparison = ShippedMarkerComparison.Compare(markers);
        // The full detail is written once. The parity check carries only its failure count.
        report.Record("markers", comparison.DetailJson);
        report.Check("marker-parity", comparison.Failures.Count == 0, JsonSerializer.Serialize(new { failures = comparison.Failures.Count }));
    });
}
