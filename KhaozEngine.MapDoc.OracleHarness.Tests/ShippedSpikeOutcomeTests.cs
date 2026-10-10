using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

// The spike point is a required input of the shipped comparison, so every run records exactly one spike outcome.
// A synthetic two-region world runs through the same entry, loader and per-region loop as the private test.
[UnsupportedOSPlatform("windows")]
public sealed class ShippedSpikeOutcomeTests
{
    const string Secret = "SECRET-5c1d";

    [Fact]
    public void Comparison_FailsOnceWhenTheManifestNamesNoSpikePoint() => PrivateOracleFixtures.InSecureTemp(root =>
    {
        Dictionary<string, string?> env = Environment(root, new { policy = "exact" });
        PrivateOracleFailure e = Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(env, CompareAll));
        Assert.Matches("^private oracle: 1 spike-point-named mismatches, report sha256 [0-9a-f]{64}$", e.Message);
        Assert.DoesNotContain(Secret, e.ToString());
        Assert.Single(Lines(env, "spike-point-named"));
        Assert.Empty(Lines(env, "spike-point"));
    });

    [Theory]
    [InlineData(65.25f, -2.75f, true, true)]
    [InlineData(70.5f, -2.5f, true, false)]
    [InlineData(-10.5f, -2.5f, false, false)]
    public void Comparison_RecordsExactlyOneSpikeOutcome(float worldX, float worldZ, bool owned, bool drawable)
        => PrivateOracleFixtures.InSecureTemp(root =>
        {
            Dictionary<string, string?> env = Environment(root, new { policy = "exact", spikePoint = new { worldX, worldZ } });
            PrivateOracleEntry.Run(env, CompareAll);
            JsonElement outcome = Assert.Single(Lines(env, "spike-point"));
            Assert.Equal(owned, outcome.GetProperty("owned").GetBoolean());
            Assert.Equal(drawable, outcome.GetProperty("drawable").GetBoolean());
            if (drawable) Assert.Equal(0d, outcome.GetProperty("error").GetDouble(), 0.00001);
        });

    [Fact]
    public void Comparison_FailsTheRunOnAMalformedSpikePoint() => PrivateOracleFixtures.InSecureTemp(root =>
    {
        Dictionary<string, string?> env = Environment(root, new { spikePoint = new { worldX = "east" } });
        PrivateOracleFailure e = Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(env, CompareAll));
        Assert.Matches("^private oracle: exception failure, report sha256 [0-9a-f]{64}$", e.Message);
        Assert.Empty(Lines(env, "spike-point"));
    });

    static void CompareAll(PrivateOracleInputs inputs, PrivateOracleReport report)
    {
        foreach (ShippedRegion region in ShippedSourceInventory.ReadRegions(inputs))
            ShippedTerrainComparison.CompareRegion(region, report);
    }

    static Dictionary<string, string?> Environment(string root, object comparison)
        => PrivateOracleFixtures.SyntheticEnvironment(root, Secret,
            source => TileWorldFile.Save(World(), Path.Combine(source, "world")),
            JsonSerializer.SerializeToElement(comparison));

    // Regions (0, 0) and (1, 0). Tiles (1, 2) and (65, 2) draw sloped ground and tile (70, 2) draws none.
    static TileWorldDocument World()
    {
        var document = new TileWorldDocument { Id = "synthetic-spike" };
        document.GetOrCreateRegion(new RegionCoord(0, 0));
        document.GetOrCreateRegion(new RegionCoord(1, 0));
        foreach (int x in new[] { 1, 65 })
        {
            document.SetUnderlay(x, 2, 0, 1);
            document.SetCornerHeightCm(x, 2, 0, 12);
            document.SetCornerHeightCm(x + 1, 2, 0, 137);
            document.SetCornerHeightCm(x, 3, 0, -41);
            document.SetCornerHeightCm(x + 1, 3, 0, 206);
        }
        return document;
    }

    static JsonElement[] Lines(Dictionary<string, string?> env, string category)
        => File.ReadAllLines(env["KHAOZ_R2_ORACLE_REPORT"]!)
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(line => line.GetProperty("category").GetString() == category)
            .Select(line => line.GetProperty("detail"))
            .ToArray();
}
