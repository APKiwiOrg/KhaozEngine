using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.MapDoc.Interiors;

public sealed class MapMembershipFullPolicyAccountingTests(ITestOutputHelper output)
{
    [Fact]
    public void FullPolicy_AccountsForEveryStageAgainstIndependentSimultaneousOwners()
    {
        using var native = MapMembershipIntervalFixtures.Create("prism");
        var scene = NativeSceneFixtures.Open(native, false).Acquire(MapMembershipIntervalTests.Scope()).Scene!;
        using var borrowed = scene.PreparationOwnership!;
        // Declare a fixed synthetic scene capacity. Padding its real lease keeps acquisition accounting
        // outside this test's subject. No expected membership total is learned from that lease.
        borrowed.Reserve(MapMembershipFullPolicyInventory.BorrowedScene - borrowed.Bytes);
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 1000000000));
        using var empty = NativeSceneFixtures.Create("empty");
        var priorScene = NativeSceneFixtures.Open(empty, false).Acquire(MapMembershipIntervalTests.Scope()).Scene!;
        using var priorSceneOwner = priorScene.PreparationOwnership;
        var prior = MapRoofEvaluationBuffer.Prepare(priorScene, admission).Buffer!;
        using var priorOwner = prior.PreparationOwnership!;
        Assert.Equal(MapMembershipFullPolicyInventory.Prior, prior.RetainedBytes);
        using (var historical = admission.Workspace())
        {
            historical.Reserve(MapMembershipFullPolicyInventory.Historical);
        }
        using var window = admission.LimitPreparation(100000000, "MembershipWindow", MapMembershipFullPolicyInventory.Reused);
        window.ChargeExternal(MapMembershipFullPolicyInventory.External);
        long rootBaseline = MapMembershipFullPolicyInventory.Prior + MapMembershipFullPolicyInventory.External;
        long windowBaseline = MapMembershipFullPolicyInventory.Reused + MapMembershipFullPolicyInventory.External;
        long rootOwners = rootBaseline + MapMembershipFullPolicyInventory.BorrowedScene;
        long windowOwners = windowBaseline + MapMembershipFullPolicyInventory.BorrowedScene;
        foreach (var boundary in MapMembershipFullPolicyInventory.Boundaries())
            output.WriteLine($"{boundary.Stage}: {boundary.Owners} = {boundary.Bytes}, root {rootOwners + boundary.Bytes}, window {windowOwners + boundary.Bytes}");

        var result = MapRoofEvaluationBuffer.Prepare(scene, admission, true);
        using var owner = result.Buffer?.PreparationOwnership;
        Assert.NotNull(result.Buffer);
        var counts = Assert.IsType<MapRoofPreparationCounts>(result.Counts);
        Assert.Equal(MapMembershipFullPolicyInventory.Header, counts.HeaderBytes);
        Assert.Equal(4, counts.ValidationRawPieces);
        Assert.Equal(12, counts.ValidationVertices);
        Assert.Equal(rootBaseline, counts.Membership.Root.BaselineBytes);
        Assert.Equal(windowBaseline, counts.Membership.BindingWindow!.BaselineBytes);
        AssertPeaks(counts.Membership, MapMembershipFullPolicyInventory.MembershipPeak);
        CheckStage("Validation", counts.Validation);
        CheckStage("Header", counts.Header);
        CheckStage("Planner", counts.Planner);
        CheckStage("Construction", counts.Construction);
        CheckStage("Publication", counts.Publication);
        Assert.Equal(MapMembershipFullPolicyInventory.Historical + MapMembershipFullPolicyInventory.Prior, admission.PeakBytes);
        owner!.Dispose();
        priorOwner.Dispose();
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.Equal(0, admission.RetainedBytes);

        void CheckStage(string name, MapPreparationStageCounts stage)
        {
            Assert.True(stage.Root.Observed, name);
            AssertPeaks(stage, MapMembershipFullPolicyInventory.Peak(name));
        }
        void AssertPeaks(MapPreparationStageCounts stage, long peak)
        {
            Assert.Equal(rootOwners + peak, stage.Root.AdmittedPeakBytes);
            Assert.Equal(windowOwners + peak, stage.BindingWindow!.AdmittedPeakBytes);
            Assert.Equal(0m, stage.Root.RefusedBytes);
            Assert.Equal(0m, stage.BindingWindow.RefusedBytes);
        }
    }
}
