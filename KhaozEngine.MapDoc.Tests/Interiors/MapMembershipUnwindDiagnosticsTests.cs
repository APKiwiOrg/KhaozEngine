using System;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Interiors;

public sealed class MapMembershipUnwindDiagnosticsTests
{
    [Fact]
    public void SharedReferenceRefusal_ReportsOnlyTheUncoveredWindowIncrement()
    {
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 1000000));
        using var earlier = admission.Workspace();
        earlier.Reserve(100);
        using var retained = earlier.Transfer(100);
        retained.Share(admission, new object());
        using var outer = admission.LimitPreparation(1000, "Outer", 25);
        using var window = admission.LimitPreparation(99, "SharedWindow", 50);
        using var interval = admission.ObservePeak();
        using var product = admission.Workspace();
        var error = Assert.Throws<MapSceneCapacityException>(() => product.Borrow(retained));
        Assert.Equal(new MapSceneCapacity("SharedWindow", 150, 149), error.Capacity);
        Assert.Equal(100, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.Equal(100, retained.Bytes);
        Assert.Equal(100, interval.Counts.Root.AdmittedPeakBytes);
        Assert.Equal(100m, interval.Counts.Root.RefusedBytes);
        Assert.Equal(150m, interval.Counts.BindingWindow!.RefusedBytes);
        Assert.Equal(125m, interval.Counts.Windows[1].RefusedBytes);
        Assert.Equal(50, interval.Counts.BindingWindow.AdmittedPeakBytes);
    }

    [Fact]
    public void AllocationCancellation_UnwindsTheIncrementAndPreservesBorrowedSceneAndPriorBuffer()
    {
        using var native = NativeSceneFixtures.Create("empty");
        var scene = NativeSceneFixtures.Open(native, false).Acquire(MapMembershipIntervalTests.Scope()).Scene!;
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 10000000));
        var prior = MapRoofEvaluationBuffer.Prepare(scene, admission).Buffer!;
        const long priorBytes = MapMembershipCompactInventory.EmptyRetained, borrowedBytes = 1000000;
        Assert.Equal(priorBytes, prior.RetainedBytes);
        scene.PreparationOwnership!.Reserve(borrowedBytes - scene.PreparationOwnership.Bytes);
        admission.AllocationAdmitted = increment =>
        {
            if (increment == 768) throw new OperationCanceledException();
        };
        Assert.Throws<OperationCanceledException>(() => MapRoofEvaluationBuffer.Prepare(scene, admission, true));
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.Equal(priorBytes, admission.RetainedBytes);
        Assert.Equal(borrowedBytes, scene.PreparationOwnership.Bytes);
        Assert.Equal(priorBytes, prior.PreparationOwnership!.Bytes);
        prior.PreparationOwnership.Dispose();
    }

    [Fact]
    public void FaceRefusal_ReportsTheRejectedByteRequestWithoutAdmittingIt()
    {
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 1000000, MaxPreparedFaces: 0));
        using var earlier = admission.Workspace();
        earlier.Reserve(20);
        using var retained = earlier.Transfer(20);
        using var window = admission.LimitPreparation(2000, "StageWindow", 50);
        using var interval = admission.ObservePeak();
        int allocations = 0;
        admission.AllocationAdmitted = _ => allocations++;
        var error = Assert.Throws<MapSceneCapacityException>(() => admission.ReserveCompilation(1));
        Assert.Equal(new MapSceneCapacity("MaxPreparedFaces", 1, 0), error.Capacity);
        Assert.Equal(20, interval.Counts.Root.AdmittedPeakBytes);
        Assert.Equal(1300m, interval.Counts.Root.RequestedBytes);
        Assert.Equal(1300m, interval.Counts.Root.RefusedBytes);
        Assert.Equal(1330m, interval.Counts.BindingWindow!.RefusedBytes);
        Assert.Equal(0, allocations);
        Assert.Equal(0, admission.PreparedFaces);
    }

    [Fact]
    public void ByteUnwind_DoesNotRefundTheEarlierCompilerFaceWork()
    {
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 1000000, MaxPreparedFaces: 4));
        var (surface, patch) = CompilerFixtures.OneCell(0, 0, 0, 0, CompilerFixtures.Full);
        using var active = admission.Enter();
        using (var window = admission.LimitPreparation(2575))
        using (var attempt = admission.BeginPreparation())
            Assert.Throws<MapSceneCapacityException>(() => MapSurfaceCompiler.Compile(surface, patch));
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.Equal(0, admission.RetainedBytes);
        Assert.Equal(2, admission.PreparedFaces);
        using (var attempt = admission.BeginPreparation()) _ = MapSurfaceCompiler.Compile(surface, patch);
        Assert.Equal(4, admission.PreparedFaces);
        var refusal = Assert.Throws<MapSceneCapacityException>(() => admission.ReserveCompilation(1));
        Assert.Equal(new MapSceneCapacity("MaxPreparedFaces", 5, 4), refusal.Capacity);
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.Equal(0, admission.RetainedBytes);
    }
}
