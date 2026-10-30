using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class ShadowStageProbeDirectionalTests
{
    [Fact]
    public void Selected_directional_instances_include_bridged_gaps_without_claiming_issued_instances()
    {
        using var f = new ShadowStageProbeFixture();
        var request = new ShadowStageProbeRequest(Guid.NewGuid(), 1, ShadowStageProbeMode.Counts);
        f.Frame.BeginShadowStageCallback(request);
        f.Scene.ConfigureShadowStageProbe(request);
        var spheres = Field<List<Vector4>>(f.Scene, "_shadowCasterSpheres");
        var spans = Field<List<Scene3D.ShadowCasterSpan>>(f.Scene, "_shadowCasterRunsScratch");
        var matrices = Field<Matrix4x4[]>(f.Scene, "_cascadeCpuVps");
        var direction = Vector3.Normalize(new Vector3(.4f, -.9f, .2f));
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, direction));
        for (int i = 0; i < 12; i++)
            spheres.Add(new Vector4(i is >= 2 and <= 4 or 9 ? Vector3.Zero : right * 500, .5f));
        spans.Add(new Scene3D.ShadowCasterSpan(3, 1, 0, 12, ShadowCastKind.Opaque));
        matrices[0] = ShadowMapMath.BuildLightViewProj(direction, Vector3.Zero, 20, 2048);
        f.Scene.ShadowCullMergeGap = 4;
        typeof(Scene3D).GetMethod("BuildCascadeCasterSpans", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(f.Scene, [1]);
        f.Scene.PublishShadowStageProbe(f.Frame);
        var snapshot = f.Frame.CompletedShadowStageCallback.Snapshot;
        Assert.NotNull(snapshot);
        var c = snapshot.Value.Counters!.Value;
        // Only 2,3,4 and 9 are kept. The four-slot gap makes one span covering 2 through 9.
        Assert.Equal(12, c.DirectionalMaskTests);
        Assert.Equal(1, c.DirectionalSelectedSpans);
        Assert.Equal(8, c.DirectionalSelectedInstances);
        Assert.Equal(0, c.DirectionalRigidDrawCalls);
    }

    static T Field<T>(Scene3D scene, string name) => (T)typeof(Scene3D)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;
}
