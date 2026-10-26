using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Interiors;

public sealed class MapMembershipCoefficientPoolTests
{
    [Fact]
    public void CrossPageSlices_AreCanonicalAndCompareByValueRatherThanOffset()
    {
        var admission = new MapSceneAdmission(new());
        using var product = admission.BeginPreparation();
        var counts = new MapRoofPreparationCounts();
        using var build = new MapMembershipBuild(admission, product, counts);
        var store = MapMembershipCoefficientStore.Create(4, 256);
        BigInteger large = (BigInteger.One << 1924) + 7;
        int minimum = store.Append(long.MinValue), maximum = store.Append(long.MaxValue);
        int first = store.Append(large), second = store.Append(large);
        Assert.Equal(0, store[minimum].Kind);
        Assert.Equal(0, store[maximum].Kind);
        Assert.Equal(0, store[first].Offset);
        Assert.Equal(256, store[second].Offset);
        Assert.Equal(241, store[first].Length);
        Assert.Equal(large, new BigInteger(store.Magnitude(store[first]), isUnsigned: true));
        Assert.Equal(large, new BigInteger(store.Magnitude(store[second]), isUnsigned: true));
        Assert.NotEqual(0, store.Magnitude(store[first])[^1]);
        Assert.Equal(0, store.Compare(store[first], store[second]));
        Assert.True(store.Compare(store[minimum], store[maximum]) < 0);
        Assert.True(store.Compare(store[maximum], store[first]) < 0);
        Assert.Equal(2, counts.CoefficientPoolPages);
        Assert.Equal(512, counts.CoefficientPoolCapacity);
        Assert.Equal(848, store.InventoryBytes);
        Assert.Equal(848, store.Inventory().Sum(p => p.Bytes));
        Assert.Equal(5, store.Inventory().Count(p => p.Identity is Array));
        store.Freeze();
        Assert.Throws<InvalidOperationException>(() => store.Append(0));
        Assert.Equal(4, store.Count);
    }

    [Fact]
    public void RefusedDirectoryReplacement_LeavesEveryOldSliceReadable()
    {
        // Four 256-byte magnitude pages, one four-token page, directories and the three owner objects.
        // The fifth page first admits an 88-byte directory alongside the old 56-byte directory.
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 1511));
        using var interval = admission.ObservePeak();
        using var product = admission.BeginPreparation();
        using var build = new MapMembershipBuild(admission, product, new MapRoofPreparationCounts());
        var store = MapMembershipCoefficientStore.Create(4, 256);
        BigInteger large = (BigInteger.One << 1924) + 7;
        for (int i = 0; i < 4; i++) store.Append(large + i);
        Assert.Equal(1424, store.InventoryBytes);
        int entries = 0;
        admission.AllocationAdmitted = increment => { if (increment == 88) entries++; };
        var error = Assert.Throws<MapSceneCapacityException>(() => store.Append(large));
        Assert.Equal(new MapSceneCapacity("MaxPreparedBytes", 1512, 1511), error.Capacity);
        Assert.Equal(0, entries);
        Assert.Equal(1424, admission.WorkspaceBytes);
        Assert.Equal(1424m, interval.Counts.Root.AdmittedPeakBytes);
        Assert.Equal(1512m, interval.Counts.Root.RefusedBytes);
        Assert.Equal(4, store.Count);
        for (int i = 0; i < 4; i++)
            Assert.Equal(large + i, new BigInteger(store.Magnitude(store[i]), isUnsigned: true));
    }
}

[Collection("AllocSensitive")]
public sealed class MapMembershipCoefficientLayoutAllocationTests
{
    [Fact]
    public void InlineAffine_AllocatesExactlyItsOwnedPageLayouts()
    {
        // The allocation harness requires callers to warm static state and JIT paths before measurement.
        _ = new MapMembershipAffine(1, 2, 3, 1);
        MapMembershipCoefficientStore? retained = null;
        long bytes = AllocationMeasurement.Measure("coefficient physical page layout", () =>
            retained = new MapMembershipAffine(1, 2, 3, 1).Store);
        Assert.NotNull(retained);
        // Store 32, two page owners of 48, one directory of 32 and one four-token array of 88.
        Assert.Equal(248, bytes);
        Assert.Equal(248, retained.InventoryBytes);
    }
}
