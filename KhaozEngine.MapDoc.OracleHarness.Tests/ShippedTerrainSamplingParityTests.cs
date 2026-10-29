using System.Runtime.Versioning;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
public class ShippedTerrainSamplingParityTests
{
    [Fact]
    public void OracleParityIncludesTheDrawnVertexRangeAtASeam()
    {
        var document = new TileWorldDocument();
        document.GetOrCreateRegion(new(0, 0));
        document.SetUnderlay(0, 0, 0, 1);
        document.SetCornerHeightCm(0, 0, 0, 2);
        var catalogs = TileWorldCatalogs.LoadJson(
            """{"materials":[{"id":1,"name":"ground","kind":"Ground","color":"#112233"}],"archetypes":[]}""",
            "terrain-seam-parity");
        const float x = 0.8f, z = 0.19999998807907104f;
        float actual = TileWorldColliders.Build(document, catalogs).Ground.HeightAt(x, -z);
        Assert.True(ShippedTerrainComparison.TryMovementHeight(document, x, z, out float oracle));
        Assert.Equal(0, oracle);
        Assert.Equal(actual, oracle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OracleAndRuntimeUseExactlyTheSameDrawnTriangleArithmetic(bool sloped)
    {
        var document = new TileWorldDocument();
        document.GetOrCreateRegion(new(0, 0));
        for (int z = 25; z <= 28; z++)
            for (int x = 20; x <= 22; x++)
            {
                document.SetUnderlay(x, z, 0, 1);
                document.SetCornerHeightCm(x, z, 0, (short)(sloped && x == 21 ? -60 : -80));
            }
        var catalogs = TileWorldCatalogs.LoadJson(
            """{"materials":[{"id":1,"name":"ground","kind":"Ground","color":"#112233"}],"archetypes":[]}""",
            "terrain-arithmetic-parity");
        var runtime = TileWorldColliders.Build(document, catalogs).Ground;

        foreach (float z in new[] { 25.571007f, 25.684608f, 26.126749f, 25.5f, 26f })
        {
            float actual = runtime.HeightAt(20.5f, -z);
            if (!sloped) Assert.Equal(-0.7999999523162842f, actual);
            Assert.True(ShippedTerrainComparison.TryMovementHeight(document, 20.5f, z, out float oracle));
            Assert.Equal(actual, oracle);
        }
    }
}
