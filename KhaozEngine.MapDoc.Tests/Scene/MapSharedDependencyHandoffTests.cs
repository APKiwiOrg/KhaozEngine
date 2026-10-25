using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapSharedDependencyHandoffTests
{
    // Empty fixture: scene 512, context (set 128 + row 128), three sources 384,
    // intent 128, product 256, detached set 128, view (256 + roles 12 + digest 8),
    // independent scene roles 12, known-empty witness 64. Empty indexes own no backing.
    const long SceneBytes = 2016, ContextBytes = 256, ViewBytes = 276;
    // 24 exact registers of 131 uints and a 64-byte row. One point slot owns two
    // interval values, an int cut and a bool. No cells, records, prisms or roofs exist.
    const long ExactBytes = 12640;
    static long PointBytes => 256 + 2L * Unsafe.SizeOf<MapMembershipPart>() + 4 + 1;
    static long MembershipDemand => 4096 + ExactBytes + PointBytes;
    // Empty construction overlaps its 1024-byte planner, 4096-byte publication
    // workspace, 512-byte geometry product and 1024-byte collection kernel.
    const long ConstructionWorkspace = 1024 + 4096 + 512 + 1024;
    static long BufferBytes => SceneBytes + 128 + 128 + 512 + 512 + ExactBytes + PointBytes;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignSceneTransfer_LaterViewAddsOnlyItsIndependentBacking(bool direct)
    {
        using var fixture = NativeSceneFixtures.Create("empty");
        var source = NativeSceneFixtures.Open(fixture, false);
        var original = new MapSceneAdmission(new(MaxPreparedBytes: 2_000_000));
        var scene = MapSceneAcquisition.Run(source, MapSceneScopeTests.Window(), original).Scene!;
        using var sceneOwner = scene.PreparationOwnership!;
        Assert.Equal(SceneBytes, original.RetainedBytes);
        var refused = new MapSceneAdmission(new(MaxPreparedBytes: SceneBytes - 1));
        using (var attempt = refused.BeginPreparation())
        {
            var error = Assert.Throws<MapSceneCapacityException>(() => Transfer(attempt, sceneOwner, direct));
            Assert.Equal(SceneBytes, error.Capacity.Requested);
            Assert.Equal(SceneBytes - 1, error.Capacity.Allowance);
            Assert.Equal(0, refused.WorkspaceBytes);
            Assert.Equal(0, refused.RetainedBytes);
            Assert.Equal(SceneBytes, original.RetainedBytes);
            Assert.Equal(SceneBytes, sceneOwner.Bytes);
        }
        var destination = new MapSceneAdmission(new(MaxPreparedBytes: 2_000_000));
        MapPreparationReservation transferred;
        using (var attempt = destination.BeginPreparation())
        {
            Transfer(attempt, sceneOwner, direct);
            transferred = attempt.Complete(SceneBytes);
        }
        using (transferred)
        using (destination.Enter())
        {
            Assert.Equal(0, original.RetainedBytes);
            Assert.Equal(SceneBytes, destination.RetainedBytes);
            var view = scene.Product.Acquire(scene.Scope.Surfaces);
            using var viewOwner = view.PreparationOwnership!;
            Assert.Equal(SceneBytes + ViewBytes, destination.RetainedBytes);
            Assert.Equal(ContextBytes + ViewBytes, viewOwner.Bytes);
            transferred.Dispose();
            Assert.Equal(ContextBytes + ViewBytes, destination.RetainedBytes);
        }
        Assert.Equal(0, destination.RetainedBytes);
        Assert.Equal(0, destination.WorkspaceBytes);
    }

    [Fact]
    public void ForeignRoofTransfer_LaterViewAddsOnlyItsIndependentBacking()
    {
        using var fixture = NativeSceneFixtures.Create("empty");
        var original = new MapSceneAdmission(new(MaxPreparedBytes: 2_000_000));
        var scene = MapSceneAcquisition.Run(NativeSceneFixtures.Open(fixture, false), MapSceneScopeTests.Window(), original).Scene!;
        using var sceneOwner = scene.PreparationOwnership!;
        var destination = new MapSceneAdmission(new(MaxPreparedBytes: 2_000_000));
        var result = MapRoofEvaluationBuffer.Prepare(scene, destination, requireCompletePolicy: true);
        Assert.Equal(MapSceneStatus.CompleteForScope, result.Status);
        using var bufferOwner = result.Buffer!.PreparationOwnership!;
        Assert.Equal(0, original.RetainedBytes);
        Assert.Equal(BufferBytes, destination.RetainedBytes);
        using (destination.Enter())
        {
            var view = scene.Product.Acquire(scene.Scope.Surfaces);
            using var viewOwner = view.PreparationOwnership!;
            Assert.Equal(BufferBytes + ViewBytes, destination.RetainedBytes);
            bufferOwner.Dispose();
            Assert.Equal(ContextBytes + ViewBytes, destination.RetainedBytes);
        }
        Assert.Equal(0, destination.RetainedBytes);
        Assert.Equal(0, destination.WorkspaceBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignTransfer_AdmitsEveryWindowBeforeConsumingSource(bool direct)
    {
        using var fixture = NativeSceneFixtures.Create("empty");
        var original = new MapSceneAdmission(new(MaxPreparedBytes: 2_000_000));
        var scene = MapSceneAcquisition.Run(NativeSceneFixtures.Open(fixture, false), MapSceneScopeTests.Window(), original).Scene!;
        using var sceneOwner = scene.PreparationOwnership!;
        var destination = new MapSceneAdmission(new(MaxPreparedBytes: 2_000_000));
        using var active = destination.Enter();
        var view = scene.Product.Acquire(scene.Scope.Surfaces);
        using var viewOwner = view.PreparationOwnership!;
        using (destination.LimitPreparation(SceneBytes - 1, "foreign-child"))
        using (var attempted = destination.BeginPreparation())
        {
            // The direct portion fits. Only the already root-owned context's child attribution refuses.
            var error = Assert.Throws<MapSceneCapacityException>(() => Transfer(attempted, sceneOwner, direct));
            Assert.Equal("foreign-child", error.LimitName);
            Assert.Equal(SceneBytes, error.Capacity.Requested);
            Assert.Equal(SceneBytes - 1, error.Capacity.Allowance);
            Assert.Equal(SceneBytes, original.RetainedBytes);
            Assert.Equal(SceneBytes, sceneOwner.Bytes);
            Assert.Equal(ContextBytes + ViewBytes, destination.RetainedBytes);
            Assert.Equal(0, destination.WorkspaceBytes);
            Assert.Equal(SceneBytes - 1, destination.RemainingPreparedBytes);
        }
        // Leave exactly the direct scene portion free at the root. The existing context needs no new bytes.
        using (var blocker = destination.Workspace())
        {
            blocker.Reserve(2_000_000 - SceneBytes - ViewBytes);
            using var attempted = destination.BeginPreparation();
            Transfer(attempted, sceneOwner, direct);
            using var transferred = attempted.Complete(SceneBytes);
            Assert.Equal(0, original.RetainedBytes);
            Assert.Equal(SceneBytes + ViewBytes, destination.RetainedBytes);
            Assert.Equal(0, destination.RemainingPreparedBytes);
            var error = Assert.Throws<MapSceneCapacityException>(() => blocker.Reserve(1));
            Assert.Equal(2_000_001, error.Capacity.Requested);
            Assert.Equal(2_000_000, error.Capacity.Allowance);
        }
        Assert.Equal(ContextBytes + ViewBytes, destination.RetainedBytes);
        viewOwner.Dispose();
        Assert.Equal(0, destination.RetainedBytes);
        Assert.Equal(0, destination.WorkspaceBytes);
    }

    [Fact]
    public void ChildWithExistingView_BorrowsSceneWithoutASecondContextAttribution()
    {
        using var fixture = NativeSceneFixtures.Create("empty");
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 2_000_000));
        var scene = MapSceneAcquisition.Run(NativeSceneFixtures.Open(fixture, false), MapSceneScopeTests.Window(), admission).Scene!;
        using var sceneOwner = scene.PreparationOwnership!;
        using var active = admission.Enter();
        long allowance = SceneBytes + ViewBytes + MembershipDemand + ConstructionWorkspace;
        using (admission.LimitPreparation(allowance, "scene-child"))
        {
            var view = scene.Product.Acquire(scene.Scope.Surfaces);
            using var viewOwner = view.PreparationOwnership!;
            Assert.Equal(ContextBytes + ViewBytes, allowance - admission.RemainingPreparedBytes);
            using (var attempted = admission.BeginPreparation())
            {
                using (var borrow = attempted.WorkspaceOwner.Borrow(sceneOwner))
                    Assert.Equal(SceneBytes + ViewBytes, allowance - admission.RemainingPreparedBytes);
                Assert.Equal(ContextBytes + ViewBytes, allowance - admission.RemainingPreparedBytes);
            }
            using (var attempted = admission.BeginPreparation())
            {
                attempted.Reserve(allowance - SceneBytes - ViewBytes + 1);
                var error = Assert.Throws<MapSceneCapacityException>(() => attempted.WorkspaceOwner.Borrow(sceneOwner));
                Assert.Equal("scene-child", error.LimitName);
                Assert.Equal(allowance + 1, error.Capacity.Requested);
                Assert.Equal(allowance, error.Capacity.Allowance);
                Assert.Equal(SceneBytes + ViewBytes, admission.RetainedBytes);
                Assert.Equal(SceneBytes, sceneOwner.Bytes);
            }
            Assert.Equal(ContextBytes + ViewBytes, allowance - admission.RemainingPreparedBytes);
            using (admission.LimitPreparation(allowance - ViewBytes - 1, "membership-peak"))
            {
                var refused = MapRoofEvaluationBuffer.Prepare(scene, admission, requireCompletePolicy: true);
                Assert.Equal(MapSceneStatus.CapacityExceeded, refused.Status);
                Assert.Null(refused.Buffer);
                Assert.Equal("membership-peak", refused.Capacity!.LimitName);
                Assert.Equal(allowance - ViewBytes, refused.Capacity.Requested);
                Assert.Equal(allowance - ViewBytes - 1, refused.Capacity.Allowance);
                Assert.Equal(SceneBytes, sceneOwner.Bytes);
                Assert.Equal(SceneBytes + ViewBytes, admission.RetainedBytes);
                Assert.Equal(0, admission.WorkspaceBytes);
            }
            Assert.Equal(ContextBytes + ViewBytes, allowance - admission.RemainingPreparedBytes);
            using var preparationPeak = admission.ObservePeak();
            var result = MapRoofEvaluationBuffer.Prepare(scene, admission, requireCompletePolicy: true);
            Assert.Equal(MapSceneStatus.CompleteForScope, result.Status);
            Assert.Equal(allowance, preparationPeak.Bytes);
            using var bufferOwner = result.Buffer!.PreparationOwnership!;
            Assert.Equal(BufferBytes + ViewBytes, admission.RetainedBytes);
            Assert.Equal(BufferBytes + ViewBytes, allowance - admission.RemainingPreparedBytes);
            bufferOwner.Dispose();
            Assert.Equal(ContextBytes + ViewBytes, admission.RetainedBytes);
            Assert.Equal(ContextBytes + ViewBytes, allowance - admission.RemainingPreparedBytes);
            viewOwner.Dispose();
            Assert.Equal(allowance, admission.RemainingPreparedBytes);
        }
        Assert.Equal(0, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
    }

    static void Transfer(MapPreparationScope attempt, MapPreparationReservation source, bool direct)
    {
        if (direct) attempt.WorkspaceOwner.Absorb(source);
        else
        {
            using var dependency = attempt.WorkspaceOwner.Borrow(source);
            dependency.Absorb();
        }
    }
}
