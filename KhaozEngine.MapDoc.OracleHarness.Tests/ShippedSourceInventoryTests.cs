using System.Runtime.Versioning;
using System.Text.Json;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
public sealed class ShippedSourceInventoryTests
{
    [Fact]
    public void ShippedSourceInventory_VerifiesAndInventoriesTheFrozenSource()
        => PrivateOracleEntry.Run((inputs, report) =>
        {
            // The verified path list identifies exactly one world, even when git archive kept its directory prefix.
            ShippedSourceInventory inventory = ShippedSourceInventory.Build(ShippedSourceInventory.WorldRoot(inputs));
            report.Record("provenance", JsonSerializer.Serialize(inputs.Provenance, ShippedSourceProvenance.JsonOptions));
            report.Record("inventory", JsonSerializer.Serialize(inventory));
            report.Check("inventory", inventory.Regions.Count > 0, "{}");
        });
}
