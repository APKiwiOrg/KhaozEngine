using System;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Interiors;

public sealed class MapMembershipArenaAdmissionTests
{
    [Theory]
    [InlineData(false, 303)]
    [InlineData(false, 304)]
    [InlineData(false, 455)]
    [InlineData(false, 456)]
    [InlineData(true, 303)]
    [InlineData(true, 304)]
    [InlineData(true, 455)]
    [InlineData(true, 456)]
    public void PageAndDirectoryGrowth_AdmitOldAndNewCapacityBeforeEntry(bool windowBinds, long threshold)
    {
        // Owner 48, directory 32 and two 80-byte rows plus array header 24 = 264.
        // Directory replacement overlaps its old 32 and new 40 bytes = 304.
        // After releasing the old directory, another 184-byte page reaches 456.
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: windowBinds ? 1000000 : 100 + threshold));
        using var source = admission.Workspace(); source.Reserve(100);
        using var receipt = source.Transfer(100);
        using var window = admission.LimitPreparation(windowBinds ? threshold : 1000000, "PageWindow", 50);
        using var interval = admission.ObservePeak();
        using (var product = admission.BeginPreparation())
        using (var build = new MapMembershipBuild(admission, product, new MapRoofPreparationCounts()))
        {
            var column = MapMembershipArenaPages<MapMembershipGeometryArena.PolygonRow>.Create(2);
            column.Append(new(default, 0, 11, 0, 0)); column.Append(new(default, 0, 12, 0, 0));
            int directories = 0, pages = 0;
            admission.AllocationAdmitted = bytes =>
            { if (bytes == 40) directories++; if (bytes == 184) pages++; };
            var error = Record.Exception(() => column.Append(new(default, 0, 13, 0, 0)));
            if (threshold == 456)
            {
                Assert.Null(error); Assert.Equal(3, column.Count);
                Assert.Equal(456, column.InventoryBytes); Assert.Equal(1, pages);
            }
            else
            {
                long request = threshold == 303 ? 304 : 456;
                var refusal = Assert.IsType<MapSceneCapacityException>(error);
                Assert.Equal(new MapSceneCapacity(windowBinds ? "PageWindow" : "MaxPreparedBytes",
                    (windowBinds ? 50 : 100) + request, (windowBinds ? 50 : 100) + threshold), refusal.Capacity);
                Assert.Equal(2, column.Count); Assert.Equal(0, pages);
                Assert.Equal(threshold == 303 ? 264 : 272, column.InventoryBytes);
                Assert.Equal(100 + request, interval.Counts.Root.RefusedBytes);
                Assert.Equal(50 + request, interval.Counts.BindingWindow!.RefusedBytes);
            }
            Assert.Equal(threshold == 303 ? 0 : 1, directories);
            Assert.Equal(11, column[0].Count); Assert.Equal(12, column[1].Count);
            long peak = threshold == 456 ? 456 : threshold == 303 ? 264 : 304;
            Assert.Equal(100 + peak, interval.Counts.Root.AdmittedPeakBytes);
            Assert.Equal(50 + peak, interval.Counts.BindingWindow!.AdmittedPeakBytes);
        }
        Assert.Equal(0, admission.WorkspaceBytes); Assert.Equal(100, admission.RetainedBytes);
    }
}
