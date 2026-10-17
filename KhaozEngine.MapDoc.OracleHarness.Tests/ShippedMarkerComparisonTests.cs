using System.Runtime.Versioning;
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
        report.Record("markers", comparison.DetailJson);
        report.Check("marker-parity", comparison.Failures.Count == 0, comparison.DetailJson);
    });
}
