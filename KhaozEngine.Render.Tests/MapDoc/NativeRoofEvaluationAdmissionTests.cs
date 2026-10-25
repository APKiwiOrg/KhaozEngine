using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeRoofEvaluationAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CanonicalEvaluation_RefusalPreservesTheEarlierSceneReceipt(bool foreignAttempt)
    {
        using var fixture = NativeSceneFixtures.Create("empty");
        var source = NativeSceneFixtures.Open(fixture, false);
        var parent = new MapSceneAdmission(new(MaxPreparedBytes: 65536));
        var scene = MapSceneAcquisition.Run(source, NativePresentationFixtures.Scope, parent).Scene!;
        using var receipt = scene.PreparationOwnership!;
        Assert.Equal(1600, receipt.Bytes);
        Assert.Equal(MapSceneStatus.CompleteForScope, MapMembershipPreparation.Measure(scene, out var demand, out _));
        Assert.True(demand.Bytes > receipt.Bytes);
        var admission = foreignAttempt ? new MapSceneAdmission(new(MaxPreparedBytes: 65536)) : parent;
        using var active = admission.Enter();
        using (admission.LimitPreparation(demand.Bytes, "exact-child"))
        {
            long rootHeadroom = 65536 - admission.RetainedBytes;
            var result = MapRoofEvaluationBuffer.Prepare(scene, admission, requireCompletePolicy: true);
            Assert.Equal(MapSceneStatus.CapacityExceeded, result.Status);
            Assert.Null(result.Buffer);
            Assert.Equal("exact-child", result.Capacity!.LimitName);
            Assert.Equal(demand.Bytes + 1600, result.Capacity.Requested);
            Assert.Equal(demand.Bytes, result.Capacity.Allowance);
            Assert.Equal(1600, receipt.Bytes);
            Assert.Equal(1600, parent.RetainedBytes);
            Assert.Equal(foreignAttempt ? 0 : 1600, admission.RetainedBytes);
            Assert.Equal(0, admission.WorkspaceBytes);
            Assert.Equal(demand.Bytes, admission.RemainingPreparedBytes);
            using var probe = admission.Workspace();
            probe.Reserve(demand.Bytes);
            Assert.Equal(demand.Bytes, admission.WorkspaceBytes);
            probe.Dispose();
            Assert.Equal(demand.Bytes, admission.RemainingPreparedBytes);
            Assert.Equal(rootHeadroom, 65536 - admission.RetainedBytes);
        }
        Assert.Equal(65536 - admission.RetainedBytes, admission.RemainingPreparedBytes);
        receipt.Dispose();
        Assert.Equal(0, parent.RetainedBytes);
    }

    [Fact]
    public void CanonicalEvaluation_KeepsItsSceneAfterTheAcquisitionOwnerReleases()
    {
        using var fixture = NativeSceneFixtures.Create("empty");
        var source = NativeSceneFixtures.Open(fixture, false);
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 32768));
        using var active = admission.Enter();
        var scene = MapSceneAcquisition.Run(source, NativePresentationFixtures.Scope, admission).Scene!;
        long sceneBytes = scene.PreparationOwnership!.Bytes;
        var prepared = MapRoofEvaluationBuffer.Prepare(scene, admission, requireCompletePolicy: true);
        Assert.True(prepared.Buffer is not null, prepared.Detail);
        var buffer = prepared.Buffer!;
        long retained = admission.RetainedBytes;
        scene.PreparationOwnership.Dispose();
        Assert.Equal(retained, admission.RetainedBytes);
        Assert.True(buffer.RetainedBytes > sceneBytes);
        buffer.Evaluate(new(WorldFrame.Origin, default), MapRoofMode.Auto);
        Assert.Empty(buffer.Roofs);
        Assert.Equal(0, admission.WorkspaceBytes);
        buffer.PreparationOwnership!.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
    }
}
