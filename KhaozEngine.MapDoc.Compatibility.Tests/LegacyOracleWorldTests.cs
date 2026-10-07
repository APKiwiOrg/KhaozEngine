using System;
using System.Linq;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.MapDocCompatibility;

public sealed class LegacyOracleWorldTests
{
    [Fact]
    public void OracleWorld_ExercisesEveryOperativeTopologyClass()
    {
        LegacyOracleWorld w = LegacyOracleWorld.Create();
        var triangles = new TileLatticeTriangle[TileTriangulation.MaxTriangles];
        bool Describe(string name, out TileGroundCell cell)
        {
            LegacyOracleCase c = w.Cases.Single(x => x.Name == name);
            return TileGroundTriangles.TryDescribe(w.Document, c.WorldX, c.WorldZ, c.Plane, out cell, triangles);
        }
        var cuts = w.Cases.Where(c => c.Name.StartsWith("cut-", StringComparison.Ordinal)).ToList();
        Assert.Equal(16, cuts.Count);
        Assert.Equal(8, cuts.Count(c => Describe(c.Name, out TileGroundCell cell) && cell.TriangleCount == 4));
        for (int r = 0; r < 4; r++)
        {
            Assert.True(Describe($"diagonal-no-overlay-{r}", out TileGroundCell d));
            Assert.Equal((TileOverlayShape.Full, r % 2 == 0), (d.Cut, d.SplitSwNe));
        }
        Assert.False(Describe("void", out _));
        Assert.False(Describe("flag-nodraw", out _));
        Assert.Contains(w.Cases, c => c.WorldX == -1);
        Assert.Contains(w.Cases, c => c.Plane == 1);
        Assert.Contains(w.Cases, c => c.Plane == 2);
    }
}
