using System.Runtime.Versioning;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
public sealed class ShippedBuildingComparisonTests
{
    [Fact]
    public void ShippedBuildings_PreserveEveryBoundLeafAndInterior() =>
        PrivateOracleEntry.Run((inputs, report) =>
        {
            var result = ShippedBuildingComparison.Compare(ShippedBuildingInputs.Read(inputs));
            report.Record("buildings", result.DetailJson);
            report.CheckFailures("building-parity", result.ClassifiedFailures);
        });
}
