using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class RigidPointShadowGroupingTests
{
    [Fact]
    public void InterleavedMobilityFollowsCompactedMeshAndInteriorPartitions()
    {
        var queue = new SceneInstances();
        queue.Add(new RigidInstanceDraw(new MeshHandle(1, 0), Matrix4x4.CreateTranslation(1f, 0f, 0f)));
        queue.Add(new RigidInstanceDraw(new MeshHandle(2, 0), Matrix4x4.CreateTranslation(2f, 0f, 0f))
        { ShadowMobility = ShadowCasterMobility.Dynamic });
        queue.Add(new RigidInstanceDraw(new MeshHandle(1, 0), Matrix4x4.CreateTranslation(3f, 0f, 0f))
        { ShadowMobility = ShadowCasterMobility.Dynamic, CutInsideInteriors = true });
        queue.Add(new RigidInstanceDraw(new MeshHandle(1, 0), Matrix4x4.CreateTranslation(4f, 0f, 0f))
        { ShadowMobility = ShadowCasterMobility.Dynamic });
        var data = new List<ModelRenderer.InstanceData>();
        var runs = new List<Scene3D.MeshRun>();
        var mobility = new List<ShadowCasterMobility>();

        Scene3D.GroupInstances(queue.Items, data, runs, retained: new[] { true, true, true, false },
            interiorCuts: new InteriorCutInstancePartition(), shadowMobilities: mobility);

        Assert.Equal(new[] { 3f, 1f, 2f }, data.ConvertAll(slot => slot.Model.M41));
        Assert.Equal(new[] { ShadowCasterMobility.Dynamic, ShadowCasterMobility.Static,
            ShadowCasterMobility.Dynamic }, mobility);
    }
}
