using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Interiors;

public sealed class MapMembershipSharedUnwindTests
{
    [Fact]
    public void BorrowingWindowRefusal_DoesNotEnterOrRetireAnotherWindowsProduct()
    {
        using var native = MapMembershipIntervalFixtures.Create("plain");
        var patch = native.Document.Surfaces.Patches.Values.Single();
        int index = patch.Records.FindIndex(r => r is MapSpaceFootprint);
        patch.Records[index] = ((MapSpaceFootprint)patch.Records[index]) with { SlotCells = Enumerable.Range(0, 256).ToArray() };
        var surface = native.Document.Surfaces.Refs[0];
        native.Document.Surfaces.Refs[0] = surface with
        {
            SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
            native.Document.Surfaces.Patches.Select(p => System.Collections.Generic.KeyValuePair.Create(p.Key, MapSurfaceSemantics.PatchDigest(p.Value))))
        };
        var scene = NativeSceneFixtures.Open(native, false).Acquire(MapMembershipIntervalTests.Scope()).Scene!;
        var admission = new MapSceneAdmission(new());
        var first = MapRoofEvaluationBuffer.Prepare(scene, admission).Buffer!;
        long root = MapMembershipCompactInventory.Retained - MapMembershipCompactInventory.Shared +
            MapMembershipArenaInventoryModel.FootprintRoot(256) + 512 + MapMembershipCompactInventory.Cache;
        Assert.Equal(root, admission.RetainedBytes);
        try
        {
            using var window = admission.LimitPreparation(170000, "BorrowWindow");
            var refused = MapRoofEvaluationBuffer.Prepare(scene, admission);
            Assert.Null(refused.Buffer);
            Assert.Equal(MapSceneStatus.CapacityExceeded, refused.Status);
            // Header 26197, plan 2080, publication 4224, private columns 30240, controls 1024,
            // bound owners 320, index 196, footprint 128, consumer 512 and window lease 512.
            const long beforeReference = 26197 + 2080 + 4224 + 30240 + 1024 + 320 + 196 + 128 + 512 + 512;
            long requested = beforeReference + MapMembershipArenaInventoryModel.FootprintRoot(256);
            Assert.Equal(new MapSceneCapacity("BorrowWindow", requested, 170000), refused.Capacity);
            Assert.Equal(requested, refused.Counts!.Membership.BindingWindow!.RefusedBytes);
            Assert.Equal(root, admission.RetainedBytes);
            Assert.Equal(0, admission.WorkspaceBytes);
            first.Evaluate(new(WorldFrame.Origin, new(.25f, 1, .25f)), MapRoofMode.Auto);
            Assert.Equal("world", first.SpaceId);
        }
        finally { first.PreparationOwnership!.Dispose(); }
        Assert.Equal(0, admission.RetainedBytes);
    }

    [Fact]
    public void CancellationBeforeCachePublication_RemovesOnlyTheIncompleteEntry()
    {
        using var a = MapMembershipIntervalFixtures.Create("plain");
        using var b = MapMembershipIntervalFixtures.Create("plain");
        var firstScene = NativeSceneFixtures.Open(a, false).Acquire(MapMembershipIntervalTests.Scope()).Scene!;
        var secondScene = NativeSceneFixtures.Open(b, false).Acquire(MapMembershipIntervalTests.Scope()).Scene!;
        var admission = new MapSceneAdmission(new());
        var first = MapRoofEvaluationBuffer.Prepare(firstScene, admission).Buffer!;
        admission.AllocationAdmitted = bytes => { if (bytes == 512 * 24) throw new OperationCanceledException(); };
        try
        {
            Assert.Throws<OperationCanceledException>(() => MapRoofEvaluationBuffer.Prepare(secondScene, admission));
            Assert.Equal(MapMembershipCompactInventory.Root, admission.RetainedBytes);
            Assert.Equal(0, admission.WorkspaceBytes);
            first.Evaluate(new(WorldFrame.Origin, new(.25f, 1, .25f)), MapRoofMode.Auto);
            Assert.Equal("world", first.SpaceId);
        }
        finally { first.PreparationOwnership!.Dispose(); }
        Assert.Equal(0, admission.RetainedBytes);
    }

    [Fact]
    public void LatePublicationFailure_ReleasesTheNewConsumerAndKeepsTheEarlierBuffer()
    {
        using var native = MapMembershipIntervalFixtures.Create("plain");
        var scene = NativeSceneFixtures.Open(native, false).Acquire(MapMembershipIntervalTests.Scope()).Scene!;
        var admission = new MapSceneAdmission(new());
        var first = MapRoofEvaluationBuffer.Prepare(scene, admission).Buffer!;
        bool changed = false;
        admission.AllocationAdmitted = bytes =>
        {
            if (changed || bytes != 512) return;
            changed = true;
            // Corrupt an internally owned test payload after preflight, before final roof-row publication.
            typeof(MapBuildingIntent).GetProperty(nameof(MapBuildingIntent.RoofGroups))!.SetValue(scene.CapturedBuildingIntent,
                new[] { new MapRoofGroupDoc("duplicate", []), new MapRoofGroupDoc("duplicate", []) });
        };
        try
        {
            Assert.Throws<ArgumentException>(() => MapRoofEvaluationBuffer.Prepare(scene, admission));
            Assert.True(changed);
            Assert.Equal(MapMembershipCompactInventory.Root, admission.RetainedBytes);
            Assert.Equal(0, admission.WorkspaceBytes);
            first.Evaluate(new(WorldFrame.Origin, new(.25f, 1, .25f)), MapRoofMode.Auto);
            Assert.Equal("world", first.SpaceId);
        }
        finally { first.PreparationOwnership!.Dispose(); }
        Assert.Equal(0, admission.RetainedBytes);
    }

    [Fact]
    public void WholeInterval_WithPooledCoefficients_UsesTheSameFixedKernelAllowance()
    {
        using var native = MapMembershipIntervalFixtures.Create("pooled");
        const float x = 6400000000;
        var scope = MapMembershipIntervalTests.Scope();
        scope = scope with
        {
            Surfaces = scope.Surfaces with { LocalMin = new(x - 1024, -2), LocalMax = new(x + 1024, 2) },
            DrawBounds = new(x - 1024, -2, -2, x + 1024, (double)int.MaxValue + 1, 2)
        };
        var scene = NativeSceneFixtures.Open(native, false).Acquire(scope).Scene!;
        Assert.NotNull(scene);
        var admission = new MapSceneAdmission(new());
        using var window = admission.LimitPreparation(1000000, "PooledWindow");
        var result = MapRoofEvaluationBuffer.Prepare(scene, admission);
        Assert.NotNull(result.Buffer);
        try
        {
            Assert.Equal(1, result.Counts!.CoefficientPoolPages);
            Assert.Equal(4096, result.Counts.CoefficientPoolCapacity);
            long inventory = MapMembershipArenaInventoryModel.Subtotal(scene, "short");
            long bound = checked(2 * inventory + 98304);
            Assert.True(result.Counts.Membership.Root.AdmittedPeakBytes <= bound);
            Assert.True(result.Counts.Membership.BindingWindow!.AdmittedPeakBytes <= bound);
        }
        finally { result.Buffer.PreparationOwnership!.Dispose(); }
        Assert.Equal(0, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
    }
}
