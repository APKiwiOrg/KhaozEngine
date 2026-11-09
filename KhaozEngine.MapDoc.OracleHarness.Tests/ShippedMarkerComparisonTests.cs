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
        // The composite retains its evidence, and every failed check keeps its typed classification.
        report.Record("markers", comparison.DetailJson);
        report.CheckFailures("marker-parity", comparison.ClassifiedFailures);
    });
}
