using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeRoofEvaluationAdmissionTests
{
    [Fact]
    public void CanonicalEvaluation_KeepsItsSceneAfterTheAcquisitionOwnerReleases()
    {
        using var fixture = NativeSceneFixtures.Create("empty");
        var source = NativeSceneFixtures.Open(fixture, false);
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 32768));
        using var active = admission.Enter();
        var scene = MapSceneAcquisition.Run(source, NativePresentationFixtures.Scope, admission).Scene!;
        long sceneBytes = scene.PreparationOwnership!.Bytes;
        var buffer = MapRoofEvaluationBuffer.Prepare(scene, admission, requireCompletePolicy: true).Buffer!;
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
