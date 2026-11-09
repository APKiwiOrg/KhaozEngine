using System;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

[Collection("AllocSensitive")]
public sealed class MapPreparationCloneAllocationTests
{
    [Fact]
    public void RecordClone_AllocatesOnlyItsAdmittedFinalBackingArray()
    {
        var empty = Patch();
        var records = Patch();
        for (int i = 0; i < 5; i++)
            records.Records.Add(new MapWallStrip("wall-" + i, new("lower", records.Key),
                new("upper", records.Key), MapStripFacing.TwoSided, 1));
        _ = empty.Clone(); _ = records.Clone();
        object? held = null;
        long arrayBytes = AllocationMeasurement.Measure("one final record array", () => held = new object[8]);
        long emptyBytes = AllocationMeasurement.Measure("empty patch clone", () => held = empty.Clone());
        long recordBytes = AllocationMeasurement.Measure("five immutable record clone", () => held = records.Clone());
        GC.KeepAlive(held);
        Assert.Equal(arrayBytes, recordBytes - emptyBytes);
        long inventory = 32L + 8L * IntPtr.Size;
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: inventory));
        using var active = admission.Enter();
        var copy = records.Clone();
        Assert.Equal(8, copy.Records.Capacity);
        Assert.Equal(inventory, admission.PeakBytes);
        Assert.Equal(inventory, admission.RetainedBytes);
        copy.PreparationOwnership!.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
    }

    static MapSurfacePatch Patch() => new()
    {
        Key = new("invented", 0, 0),
        Width = 1,
        Depth = 1,
        Heights = new int[4],
        Cells = new MapSurfaceCell[1],
        Presence = [1UL],
    };
}
