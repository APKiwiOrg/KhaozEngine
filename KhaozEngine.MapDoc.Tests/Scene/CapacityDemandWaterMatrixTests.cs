using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using KhaozEngine.MapDoc.Water;
using Xunit;
using static KhaozEngine.Tests.CapacityDemandProbe;

namespace KhaozEngine.Tests.MapDoc.Scene;

[Collection("AllocSensitive")]
public sealed class CapacityDemandWaterMatrixTests
{
    // All rows use a limit of three. Dimensions, per-cell faces and expected counters are authored independently.
    [Theory]
    [InlineData("RowCells", 2, 1, 1, 0, 2, false)]
    [InlineData("RowCells", 4, 1, 1, 0, 4, true)]
    [InlineData("RowFaces", 1, 1, 2, 0, 2, false)]
    [InlineData("RowFaces", 1, 1, 4, 0, 4, true)]
    [InlineData("ChunkCells", 1, 1, 1, 0, 1, false)]
    [InlineData("ChunkCells", 2, 2, 1, 2, 2, true)]
    [InlineData("ChunkFaces", 1, 1, 1, 0, 1, false)]
    [InlineData("ChunkFaces", 1, 2, 2, 2, 2, true)]
    [InlineData("CellFaces", 2, 1, 2, 0, 2, false)]
    [InlineData("CellFaces", 1, 1, 4, 0, 4, true)]
    [InlineData("RunCells", 2, 1, 2, 1, 1, false)]
    [InlineData("RunCells", 4, 1, 0, 3, 1, true)]
    [InlineData("RunFaces", 4, 1, 1, 1, 1, false)]
    [InlineData("RunFaces", 2, 1, 2, 2, 2, true)]
    public void ChunkSplitting_PublishesEveryEvaluatedThreshold(string site, long width, long height, long faces,
        int current, int increment, bool refused)
    {
        using var probe = new CapacityDemandProbe();
        var chunks = (IEnumerable)typeof(MapWaterBoundReads).GetMethod("Chunks", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { 0L, 0L, width, height, 3, (Func<long, long, long>)((_, _) => faces) })!;
        var result = chunks.Cast<object>().ToArray();
        Assert.NotEmpty(result);
        var rows = probe.For("MaxSourceFacesPerBound").Where(r => Read<string>(r, "Site") == "MapWaterBoundReads." + site).ToArray();
        Assert.Contains(rows, r => Read<decimal>(r, "CurrentDemand") == current && Read<decimal>(r, "ProposedIncrement") == increment &&
            Read<decimal>(r, "Allowance") == 3 && Read<bool>(r, "Refused") == refused);
        if (site == "CellFaces")
            Assert.Equal(refused, result.Any(chunk => (long)chunk.GetType().GetProperty("Faces")!.GetValue(chunk)! < 0));
    }
}
