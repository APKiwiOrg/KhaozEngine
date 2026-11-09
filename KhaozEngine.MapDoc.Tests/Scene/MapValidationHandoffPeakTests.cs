using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapValidationHandoffPeakTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ForeignHandoff_ValidationObservesOnlyNewInventory(bool direct, bool reuseContext)
    {
        // Empty scene: 512 scene + 256 shared context + 384 sources + 128 intent +
        // 256 product + 128 detached set + 276 view + 12 scene roles + 64 witness.
        const long sceneBytes = 2016, contextBytes = 256, viewBytes = 276;
        using var fixture = NativeSceneFixtures.Create("empty");
        var original = new MapSceneAdmission(new(MaxPreparedBytes: 200000));
        var scene = MapSceneAcquisition.Run(NativeSceneFixtures.Open(fixture, false), MapSceneScopeTests.Window(), original).Scene!;
        using var sceneOwner = scene.PreparationOwnership!;
        Assert.Equal(sceneBytes, original.RetainedBytes);
        var destination = new MapSceneAdmission(new(MaxPreparedBytes: 200000));
        using var active = destination.Enter();
        using var earlier = destination.Workspace();
        earlier.Reserve(100000);
        earlier.Release(100000);
        earlier.Reserve(100);
        using var earlierOwner = earlier.Transfer(100);
        using var existingView = reuseContext ? scene.Product.Acquire(scene.Scope.Surfaces).PreparationOwnership : null;
        Assert.Equal(100 + (reuseContext ? contextBytes + viewBytes : 0), destination.RetainedBytes);
        var diagnostics = destination.ObserveAcquisition();
        var counts = new MapRoofPreparationCounts();
        // Validation header, earlier owner, exactly one scene context, optional independent view and external bytes.
        long expected = 32768 + 100 + sceneBytes + (reuseContext ? viewBytes : 0) + 200;
        using (var outer = destination.ObservePeak())
        {
            using (var validation = new MapPrismValidationWorkspace(destination, counts, 0))
            using (var observed = destination.ObservePeak())
            using (diagnostics.Enter(MapAcquisitionStage.Dependency))
            using (var window = destination.LimitPreparation(100000, "handoff-child"))
            using (var attempt = destination.BeginPreparation())
            {
                if (direct) attempt.WorkspaceOwner.Absorb(sceneOwner);
                else
                {
                    using var dependency = attempt.WorkspaceOwner.Borrow(sceneOwner);
                    dependency.Absorb();
                }
                Assert.Equal(expected - 200, observed.Bytes);
                using var adopted = attempt.Complete(sceneBytes, publishToParent: false);
                // Adoption changes classification without reserving or observing an additional allocation.
                Assert.Equal(expected - 200, observed.Bytes);
                window.ChargeExternal(200);
                Assert.Equal(expected, observed.Bytes);
                Assert.Equal(0, original.RetainedBytes);
                Assert.Equal(100 + sceneBytes + (reuseContext ? viewBytes : 0), destination.RetainedBytes);
            }
            Assert.Equal(expected, outer.Bytes);
        }
        Assert.Equal(expected, counts.ValidationPeakBytes);
        Assert.Equal(100000, destination.PeakBytes);
        Assert.Equal(100 + sceneBytes + (reuseContext ? viewBytes : 0),
            diagnostics.Stages[MapAcquisitionStage.Dependency].RetainedHighWater);
        existingView?.Dispose();
        earlierOwner.Dispose();
        Assert.Equal(0, destination.WorkspaceBytes);
        Assert.Equal(0, destination.RetainedBytes);
    }
}
