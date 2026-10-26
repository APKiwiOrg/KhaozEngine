using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Interiors;

public sealed class MapMembershipNestedWindowTests
{
    const long Private = MapMembershipCompactInventory.Retained - MapMembershipCompactInventory.Shared;
    const long Footprint = MapMembershipCompactInventory.Footprint;
    const long Group = 512;
    const long Cache = MapMembershipCompactInventory.Cache;

    [Theory]
    [InlineData(false, 0, 1, 2)]
    [InlineData(false, 0, 2, 1)]
    [InlineData(false, 1, 0, 2)]
    [InlineData(false, 1, 2, 0)]
    [InlineData(false, 2, 0, 1)]
    [InlineData(false, 2, 1, 0)]
    [InlineData(true, 0, 1, 2)]
    [InlineData(true, 0, 2, 1)]
    [InlineData(true, 1, 0, 2)]
    [InlineData(true, 1, 2, 0)]
    [InlineData(true, 2, 0, 1)]
    [InlineData(true, 2, 1, 0)]
    public void BorrowingAncestors_KeepOneFootprintObligationInEveryRetirementOrder(bool siblings, int a, int b, int c)
    {
        using var native = MapMembershipIntervalFixtures.Create("plain");
        var scene = NativeSceneFixtures.Open(native, false).Acquire(MapMembershipIntervalTests.Scope()).Scene!;
        var admission = new MapSceneAdmission(new());
        var buffers = new List<MapRoofEvaluationBuffer> { Prepare(scene, admission) };
        using var parent = admission.LimitPreparation(1000000, "P");
        var p = Assert.IsType<MapPreparationLedger.Window>(admission.WindowIdentity);
        MapPreparationLedger.Window? c1 = null;
        try
        {
            if (siblings)
            {
                using var child = admission.LimitPreparation(1000000, "C1");
                c1 = Assert.IsType<MapPreparationLedger.Window>(admission.WindowIdentity);
                buffers.Add(Prepare(scene, admission));
            }
            else buffers.Add(Prepare(scene, admission));
            using var lastChild = admission.LimitPreparation(1000000, "C2");
            var c2 = Assert.IsType<MapPreparationLedger.Window>(admission.WindowIdentity);
            buffers.Add(Prepare(scene, admission));
            bool[] alive = [true, true, true];
            AssertObligations();
            foreach (int index in new[] { a, b, c })
            {
                buffers[index].PreparationOwnership!.Dispose();
                alive[index] = false;
                AssertObligations();
                foreach (int survivor in Enumerable.Range(0, 3).Where(i => alive[i]))
                {
                    buffers[survivor].Evaluate(new(WorldFrame.Origin, new(.25f, 1, .25f)), MapRoofMode.Auto);
                    Assert.Equal("world", buffers[survivor].SpaceId);
                }
            }
            void AssertObligations()
            {
                int rootOwners = alive.Count(v => v), parentOwners = (alive[1] ? 1 : 0) + (alive[2] ? 1 : 0);
                Assert.Equal(Obligation(parentOwners), p.Total);
                Assert.Equal(Obligation(alive[2] ? 1 : 0), c2.Total);
                if (c1 is not null) Assert.Equal(Obligation(alive[1] ? 1 : 0), c1.Total);
                Assert.Equal(Obligation(rootOwners) + (rootOwners == 0 ? 0 : Cache), admission.RetainedBytes);
                Assert.Equal(0, admission.WorkspaceBytes);
            }
        }
        finally { foreach (var buffer in buffers) buffer.PreparationOwnership!.Dispose(); }
        Assert.Equal(0, p.Total);
        Assert.Equal(0, admission.RetainedBytes);
    }

    [Fact]
    public void NestedBorrower_FitsWhenTheParentAlreadyCarriesTheFootprint()
    {
        using var native = DominantFootprint();
        var scene = NativeSceneFixtures.Open(native, false).Acquire(MapMembershipIntervalTests.Scope()).Scene!;
        var admission = new MapSceneAdmission(new());
        long footprint = MapMembershipArenaInventoryModel.FootprintRoot(256);
        var first = Prepare(scene, admission);
        using var parent = admission.LimitPreparation(400000, "P");
        var p = Assert.IsType<MapPreparationLedger.Window>(admission.WindowIdentity);
        MapRoofEvaluationBuffer? second = null, third = null;
        try
        {
            second = Prepare(scene, admission);
            using var child = admission.LimitPreparation(1000000, "C");
            var c = Assert.IsType<MapPreparationLedger.Window>(admission.WindowIdentity);
            third = Prepare(scene, admission);
            Assert.Equal(Obligation(2, footprint), p.Total);
            Assert.Equal(Obligation(1, footprint), c.Total);
            Assert.Equal(Obligation(3, footprint) + Cache, admission.RetainedBytes);
            Assert.Equal(0m, third.Counts.Membership.Root.RefusedBytes);
            Assert.All(third.Counts.Membership.Windows, counts => Assert.Equal(0m, counts.RefusedBytes));
        }
        finally
        {
            third?.PreparationOwnership!.Dispose(); second?.PreparationOwnership!.Dispose(); first.PreparationOwnership!.Dispose();
        }
        Assert.Equal(0, p.Total);
        Assert.Equal(0, admission.RetainedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedNestedBorrower_PreservesEarlierAttributionAndReportsOnlyNewWindowDemand(bool cancel)
    {
        using var native = DominantFootprint();
        var scene = NativeSceneFixtures.Open(native, false).Acquire(MapMembershipIntervalTests.Scope()).Scene!;
        var admission = new MapSceneAdmission(new());
        long footprint = MapMembershipArenaInventoryModel.FootprintRoot(256);
        var first = Prepare(scene, admission);
        using var parent = admission.LimitPreparation(1000000, "P");
        var p = Assert.IsType<MapPreparationLedger.Window>(admission.WindowIdentity);
        MapRoofEvaluationBuffer? second = null;
        try
        {
            second = Prepare(scene, admission);
            using var child = admission.LimitPreparation(cancel ? 1000000 : 170000, "C");
            var c = Assert.IsType<MapPreparationLedger.Window>(admission.WindowIdentity);
            if (cancel)
            {
                long parentOnly = -1;
                admission.RequestingBytes = _ =>
                {
                    if (c.Total < footprint + Group) return;
                    parentOnly = p.Total - c.Total;
                    throw new OperationCanceledException();
                };
                Assert.Throws<OperationCanceledException>(() => MapRoofEvaluationBuffer.Prepare(scene, admission));
                Assert.Equal(Private + Group, parentOnly);
                admission.RequestingBytes = null;
            }
            else
            {
                var refused = MapRoofEvaluationBuffer.Prepare(scene, admission);
                Assert.Null(refused.Buffer);
                // Header, planning, publication, private columns, controls, bounds, index, cell ID and consumer leases.
                const long beforeReference = 26197 + 2080 + 4224 + 30240 + 1024 + 320 + 196 + 4 + 128 + 512 + 512;
                Assert.Equal(new MapSceneCapacity("C", beforeReference + footprint, 170000), refused.Capacity);
                var parentCounts = Assert.Single(refused.Counts!.Membership.Windows, w => w.WindowOrdinal == p.Ordinal);
                Assert.Equal(Obligation(1, footprint) + beforeReference, parentCounts.RefusedBytes);
            }
            Assert.Equal(Obligation(1, footprint), p.Total);
            Assert.Equal(0, c.Total);
            Assert.Equal(Obligation(2, footprint) + Cache, admission.RetainedBytes);
            Assert.Equal(0, admission.WorkspaceBytes);
            second.Evaluate(new(WorldFrame.Origin, new(.25f, 1, .25f)), MapRoofMode.Auto);
            Assert.Equal("world", second.SpaceId);
        }
        finally
        {
            admission.RequestingBytes = null;
            second?.PreparationOwnership!.Dispose(); first.PreparationOwnership!.Dispose();
        }
        Assert.Equal(0, p.Total);
        Assert.Equal(0, admission.RetainedBytes);
    }

    [Fact]
    public void WholeInterval_WithNestedDominantSharedGeometry_UsesIndependentInventoryInEveryWindow()
    {
        using var native = DominantFootprint();
        var scene = NativeSceneFixtures.Open(native, false).Acquire(MapMembershipIntervalTests.Scope()).Scene!;
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 1000000000));
        long footprint = MapMembershipArenaInventoryModel.FootprintRoot(256);
        var buffers = new List<MapRoofEvaluationBuffer> { Prepare(scene, admission) };
        var windows = new List<IDisposable>();
        try
        {
            for (int depth = 0; depth < 8; depth++)
            {
                windows.Add(admission.LimitPreparation(100000000, "Nested"));
                buffers.Add(Prepare(scene, admission));
            }
            var interval = buffers[^1].Counts.Membership;
            long rootInventory = 9 * (Private + Group) + footprint + Cache;
            Assert.True(interval.Root.AdmittedPeakBytes <= 2 * rootInventory + MapMembershipInventoryModel.FixedKernelAllowance);
            foreach (var window in interval.Windows)
            {
                // The ninth preparation is inside eight borrowing windows. Each ancestor contains only its descendants.
                int consumers = checked(9 - (int)window.WindowOrdinal);
                long inventory = consumers * (Private + Group) + footprint;
                Assert.True(window.AdmittedPeakBytes <= 2 * inventory + MapMembershipInventoryModel.FixedKernelAllowance,
                    $"Window {window.WindowOrdinal} peak {window.AdmittedPeakBytes} exceeds the independent bound for {inventory} bytes.");
            }
            foreach (var window in interval.Windows)
            {
                int consumers = checked(9 - (int)window.WindowOrdinal);
                Assert.Equal((consumers - 1) * (Private + Group) + (consumers == 1 ? 0 : footprint), window.BaselineBytes);
            }
            Assert.Equal(rootInventory, admission.RetainedBytes);
            Assert.Equal(0, admission.WorkspaceBytes);
        }
        finally
        {
            for (int i = windows.Count - 1; i >= 0; i--) windows[i].Dispose();
            foreach (var buffer in buffers) buffer.PreparationOwnership!.Dispose();
        }
        Assert.Equal(0, admission.RetainedBytes);
    }

    static NativeSceneFixture DominantFootprint()
    {
        var native = MapMembershipIntervalFixtures.Create("plain");
        var patch = native.Document.Surfaces.Patches.Values.Single();
        int index = patch.Records.FindIndex(r => r is MapSpaceFootprint);
        patch.Records[index] = ((MapSpaceFootprint)patch.Records[index]) with { SlotCells = Enumerable.Range(0, 256).ToArray() };
        var surface = native.Document.Surfaces.Refs[0];
        native.Document.Surfaces.Refs[0] = surface with
        {
            SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
                native.Document.Surfaces.Patches.Select(p => KeyValuePair.Create(p.Key, MapSurfaceSemantics.PatchDigest(p.Value))))
        };
        return native;
    }
    static long Obligation(int consumers, long footprint = Footprint) => consumers == 0 ? 0 : consumers * (Private + Group) + footprint;
    static MapRoofEvaluationBuffer Prepare(MapScopedScene scene, MapSceneAdmission admission)
    {
        var result = MapRoofEvaluationBuffer.Prepare(scene, admission);
        Assert.True(result.Buffer is not null, result.Detail);
        return result.Buffer;
    }
}
