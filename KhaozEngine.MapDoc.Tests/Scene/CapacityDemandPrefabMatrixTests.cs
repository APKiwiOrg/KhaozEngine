using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using Xunit;
using static KhaozEngine.Tests.CapacityDemandProbe;

namespace KhaozEngine.Tests.MapDoc.Scene;

[Collection("AllocSensitive")]
public sealed class CapacityDemandPrefabMatrixTests
{
    [Theory]
    [InlineData("Instances")]
    [InlineData("Children")]
    [InlineData("ReferenceEdges")]
    [InlineData("Patches")]
    [InlineData("Records")]
    [InlineData("GeometryElements")]
    [InlineData("Bindings")]
    [InlineData("Overrides")]
    [InlineData("Transfers")]
    [InlineData("PayloadBytes")]
    public void ExpansionCounter_EachProfileFieldReportsItsIndependentBoundary(string field)
    {
        var limits = new MapPrefabExpansionLimits(MaxInstances: 3, MaxChildren: 3, MaxReferenceEdges: 3,
            MaxPatches: 3, MaxRecords: 3, MaxGeometryElements: 3, MaxBindings: 3, MaxOverrides: 3, MaxTransfers: 3, MaxPayloadBytes: 3);
        using var probe = new CapacityDemandProbe();
        MapPrefabPreflight.CheckCounterForTests(limits, field, 3);
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabPreflight.CheckCounterForTests(limits, field, 4));
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, error.Reason);
        Assert.Equal("Max" + field, error.Budget);
        Assert.Equal(4, error.ActualCount);
        Assert.Equal(3, error.Limit);
        var rows = probe.For("Max" + field).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, r => Assert.Equal("MapPrefabPreflight.Max" + field + ".1", Read<string>(r, "Site")));
        Assert.All(rows, r => Assert.Equal(0, Read<decimal>(r, "CurrentDemand")));
        Assert.All(rows, r => Assert.Equal(3, Read<decimal>(r, "Allowance")));
        Assert.Equal(3, Read<decimal>(rows[0], "ProposedIncrement"));
        Assert.Equal(4, Read<decimal>(rows[1], "ProposedIncrement"));
        Assert.False(Read<bool>(rows[0], "Refused"));
        Assert.True(Read<bool>(rows[1], "Refused"));
    }
}
