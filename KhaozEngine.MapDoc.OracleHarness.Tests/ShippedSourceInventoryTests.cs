using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using KhaozEngine.TileWorld;
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
            ShippedSourcePath manifest = inputs.Provenance.Paths.Single(p =>
                Path.GetFileName(p.Path) == TileWorldFile.ManifestFileName);
            string root = Path.GetDirectoryName(Path.Combine(inputs.SourceRoot, manifest.Path))!;
            ShippedSourceInventory inventory = ShippedSourceInventory.Build(root);
            report.Record("provenance", JsonSerializer.Serialize(inputs.Provenance, ShippedSourceProvenance.JsonOptions));
            report.Record("inventory", JsonSerializer.Serialize(inventory));
            report.Check("inventory", inventory.Regions.Count > 0, "{}");
        });
}
