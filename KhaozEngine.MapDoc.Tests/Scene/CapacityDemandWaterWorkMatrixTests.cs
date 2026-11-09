using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using Xunit;
using static KhaozEngine.Tests.CapacityDemandProbe;

namespace KhaozEngine.Tests.MapDoc.Scene;

[Collection("AllocSensitive")]
public sealed class CapacityDemandWaterWorkMatrixTests
{
    [Theory]
    [InlineData("MaxPieces", 0, 1, 1)]
    [InlineData("MaxPieceVertices", 0, 4, 4)]
    [InlineData("MaxFootprintCells", 1, 1, 1)]
    [InlineData("MaxBoundFaces", 1, 1, 1)]
    [InlineData("MaxBoundCells", 1, 1, 1)]
    [InlineData("MaxPairChecks", 1, 1, 1)]
    [InlineData("MaxTriangles", 1, 1, 1)]
    public void WaterWork_EveryProfileFieldReportsItsOwnBoundary(string limit, int baseline, int success, int extra)
    {
        var meter = new MapWaterWorkMeter(new(MaxPieces: 1, MaxPieceVertices: 4, MaxFootprintCells: 2,
            MaxBoundFaces: 2, MaxBoundCells: 2, MaxPairChecks: 2, MaxTriangles: 2));
        Action<int> run = count =>
        {
            switch (limit)
            {
                case "MaxPieces":
                    meter.ChargeDomain(new(MapLatticeFrame.ImportedMetreCentimetre,
                    Enumerable.Repeat(new MapWaterRect(0, 0, 1, 1), count).ToArray(), [])); break;
                case "MaxPieceVertices":
                    meter.ChargeDomain(new(MapLatticeFrame.ImportedMetreCentimetre, [],
                    [new(Enumerable.Range(0, count).Select(i => MapLatticeAddress.Corner(i, i)).ToArray())])); break;
                case "MaxFootprintCells": meter.ChargeFootprintCells(count); break;
                case "MaxBoundFaces": meter.ChargeRead(count); break;
                case "MaxBoundCells": meter.ChargeBoundTraversal(BigInteger.Zero, count); break;
                case "MaxPairChecks": meter.Pairs.Charge(count); break;
                case "MaxTriangles": meter.ChargeTriangles(count); break;
            }
        };
        if (baseline != 0) run(baseline);
        using var probe = new CapacityDemandProbe();
        run(success);
        Assert.Throws<MapWaterCapacityException>(() => run(success + extra));
        var rows = probe.For(limit).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(baseline, Read<decimal>(rows[0], "CurrentDemand"));
        Assert.Equal(success, Read<decimal>(rows[0], "ProposedIncrement"));
        Assert.Equal(limit is "MaxPieces" or "MaxPieceVertices" ? 0 : baseline + success, Read<decimal>(rows[1], "CurrentDemand"));
        Assert.Equal(success + extra, Read<decimal>(rows[1], "ProposedIncrement"));
        Assert.All(rows, row => Assert.Equal(limit == "MaxPieces" ? 1 : limit == "MaxPieceVertices" ? 4 : 2, Read<decimal>(row, "Allowance")));
        Assert.Equal(baseline + success, Read<decimal>(rows[0], "Requested"));
        Assert.Equal((limit is "MaxPieces" or "MaxPieceVertices" ? 0 : baseline + success) + success + extra, Read<decimal>(rows[1], "Requested"));
        Assert.False(Read<bool>(rows[0], "Refused"));
        Assert.True(Read<bool>(rows[1], "Refused"));
    }
}
