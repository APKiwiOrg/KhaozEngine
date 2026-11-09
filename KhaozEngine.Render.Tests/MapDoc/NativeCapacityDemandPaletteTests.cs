using System.Linq;
using Xunit;
using static KhaozEngine.Tests.CapacityDemandProbe;

namespace KhaozEngine.Tests.MapDoc;

[Collection("AllocSensitive")]
public sealed class NativeCapacityDemandPaletteTests
{
    [Fact]
    public void PaletteUnion_ReportsTheSplitBoundaryInTheExistingEnforcementFixture()
    {
        using var probe = new CapacityDemandProbe();
        new NativeMapTerrainTests().MaterialSets_PartitionDeterministicallyAndRefuseAnUnfittableFace();
        var rows = probe.For("MaxMaterials").Where(r => Read<string>(r, "Site") == "NativeMapTerrain.MaterialUnion").ToArray();
        Assert.Contains(rows, r => Read<decimal>(r, "CurrentDemand") == 63 && Read<decimal>(r, "ProposedIncrement") == 1 &&
            Read<decimal>(r, "Allowance") == 64 && !Read<bool>(r, "Refused"));
        Assert.Contains(rows, r => Read<decimal>(r, "CurrentDemand") == 1 && Read<decimal>(r, "ProposedIncrement") == 1 &&
            Read<decimal>(r, "Allowance") == 1 && Read<bool>(r, "Refused"));
    }
}
