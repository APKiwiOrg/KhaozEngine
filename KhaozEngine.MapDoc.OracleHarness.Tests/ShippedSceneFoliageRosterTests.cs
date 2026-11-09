using System.Linq;
using System.Runtime.Versioning;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
public sealed class ShippedSceneFoliageRosterTests
{
    [Fact]
    public void SourceRoster_ExcludesOffRasterOwnersWithoutLosingLegacyRows()
    {
        using var fixture = ShippedSceneGuardFixtures.Create("foliage-valid");
        var inputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        var scope = ShippedSceneInputs.Scope(inputs.Manifest.Windows[0]);
        var generation = Assert.Single(LegacySceneRecorder.RecordFoliage(inputs, scope));
        Assert.Equal(new MapFoliageChunkKey(0, 0), generation.Chunk);
        var binding = Assert.Single(inputs.FoliageBindings);
        var surface = new TileFoliageSurface(inputs.World, inputs.Catalogs, binding.Source);
        var settings = ShippedFoliageComparison.Settings(binding.Source);
        var legacy = GroundCoverDistribution.Generate(new(0, -4, 32, 28), settings, surface.Sample);
        Assert.NotEmpty(legacy);
        Assert.Equal(legacy.ToArray(), generation.Rows.Select(r => r.Instance).ToArray());
        foreach (var area in new RectArea[] { new(-32, -36, 0, -4), new(0, -36, 32, -4), new(-32, -4, 0, 28) })
            Assert.Empty(GroundCoverDistribution.Generate(area, settings, surface.Sample));
    }

    [Fact]
    public void SourceRoster_RetainsEmptyInRasterOwnerAndRejectsItsOmission()
    {
        using var fixture = ShippedSceneGuardFixtures.Create("foliage-valid");
        var inputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        // Remove eligible ground without changing the authored density raster or its owner.
        for (int z = 0; z < 4; z++) for (int x = 0; x < 4; x++) inputs.World.SetUnderlay(x, z, 0, 0);
        var scope = ShippedSceneInputs.Scope(inputs.Manifest.Windows[0]);
        var expected = LegacySceneRecorder.RecordFoliage(inputs, scope);
        var generation = Assert.Single(expected);
        Assert.Equal(new MapFoliageChunkKey(0, 0), generation.Chunk);
        Assert.Empty(generation.Rows);
        Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(fixture.Environment, (_, report) =>
            ShippedSceneFoliageRoster.Compare(inputs, expected, new(new MapDrawRecorder().Snapshot(), [], []), report)));
        Assert.Contains("scene-foliage-key", fixture.FailureCategories());
    }

    [Theory]
    [InlineData(32.25, 0)]
    [InlineData(34.25, 2)]
    public void SourceRoster_IncludesAdjacentOwnerWithinSourceModelReach(double minX, double motionPadding)
    {
        using var fixture = ShippedSceneGuardFixtures.Create("foliage-valid");
        var inputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        var scope = ShippedSceneInputs.Scope(inputs.Manifest.Windows[0]) with
        {
            DrawBounds = new(minX, -10, -3, minX + .25, 10, -2),
            MotionPaddingMetres = motionPadding
        };
        // The draw rectangle lies in owner 1. Source model bounds reach owner 0 across that edge.
        var generation = Assert.Single(LegacySceneRecorder.RecordFoliage(inputs, scope));
        Assert.Equal(new MapFoliageChunkKey(0, 0), generation.Chunk);
        Assert.NotEmpty(generation.Rows);
    }
}
