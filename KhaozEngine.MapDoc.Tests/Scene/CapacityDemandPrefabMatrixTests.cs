using System;
using System.Linq;
using System.Reflection;
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
        var counter = typeof(MapPrefabPreflight).GetNestedType("Counter", BindingFlags.NonPublic)!;
        var value = Activator.CreateInstance(counter, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: new object?[] { limits, null }, culture: null)!;
        var slot = counter.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!;
        var check = counter.GetMethod("Check", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var probe = new CapacityDemandProbe();
        slot.SetValue(value, 3L);
        check.Invoke(value, null);
        slot.SetValue(value, 4L);
        var error = Assert.Throws<TargetInvocationException>(() => check.Invoke(value, null));
        Assert.IsType<MapDocumentException>(error.InnerException);
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
