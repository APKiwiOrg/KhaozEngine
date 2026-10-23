using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class DqCullingTests
{
    [Theory]
    [InlineData(false, "camera", 0f)]
    [InlineData(true, "camera", 0f)]
    [InlineData(false, "cascade", 128f)]
    [InlineData(true, "cascade", 128f)]
    [InlineData(false, "point", -128f)]
    [InlineData(true, "point", -128f)]
    public void DQ_bound_retains_a_surface_outside_the_rest_heuristic(bool gpu, string consumer, float origin)
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.UseGpuSkinning = gpu;
        scene.RenderOrigin = new Vector3(origin, 0, 0);
        SkinnedGltfMesh mesh = Patch();
        SkinnedMeshHandle handle = scene.LoadSkinnedMesh(mesh);
        scene.Begin();
        Matrix4x4 world = Matrix4x4.CreateTranslation(origin - 40 + .9f, 0, .5f);
        PreparedSkinningPose pose = DqGpuReadback.Queue(scene, mesh,
            new SkinnedInstanceDraw(handle, world) { ShadowOnly = consumer != "camera", CastsShadows = consumer != "camera" }, Joints());
        Assert.True(DualQuaternionBounds.TryBoundWorld(pose.ModelBounds, world, out MeshBounds bounds));
        MeshBounds.FromVertices(mesh.Vertices).WorldSphere(world, out Vector3 restCentre, out float restRadius);
        var frustum = FrustumPlanes.Extract(Matrix4x4.CreateTranslation(-origin, 0, 0));
        Assert.False(frustum.IntersectsSphere(restCentre, restRadius * Scene3D.SkinnedCullSafetyFactor));
        foreach (SkinnedVertex vertex in mesh.Vertices)
        {
            Vector3 expected = new(origin + .9f - vertex.Position.Y, vertex.Position.X, .5f);
            Vector3 cpu = Vector3.Transform(DualQuaternionSkinning.Position(vertex, pose.Owner.Dq(pose)), world);
            Assert.InRange(Vector3.Distance(expected, cpu), 0, 2e-5f * 40);
            Contains(bounds, cpu);
        }
        ((FrustumPlanes[])Field(scene, "_shadowFrustums"))[0] = frustum;
        if (consumer == "point")
        {
            scene.AddLight(new Vector3(origin, 0, .5f), Color.White, 2, 1, LightShadow.Dynamic);
            Assert.True(scene.PreparePointShadowRequests(Vector3.Zero));
        }
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList());
        typeof(Scene3D).GetMethod("PrepareSkinnedDraws", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(scene,
            [commands, consumer == "camera" ? frustum : FrustumPlanes.Extract(Matrix4x4.CreateTranslation(-origin - 400, 0, 0)),
                consumer == "cascade", consumer == "cascade" ? 1 : 0, consumer == "point"]);
        IList draws = (IList)Field(scene, gpu ? "_gpuSkinnedDraws" : "_cpuSkinnedDraws");
        Assert.Single(draws);
        PointShadowCasterSphere sphere = (PointShadowCasterSphere)Field(draws[0]!, "PointSphere")!;
        Assert.Equal(bounds.Center, sphere.Center);
        Assert.Equal(bounds.Radius, sphere.Radius);
        if (!gpu)
            foreach (ModelVertex vertex in (List<ModelVertex>)Field(scene, "_cpuSkinnedVerts"))
                Contains(bounds, Vector3.Transform(vertex.Position, world));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Public_DQ_admission_owns_the_selected_pose_and_culls_a_provably_distant_envelope(bool gpu)
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.UseGpuSkinning = gpu;
        scene.ForceTemporalForTests = true;
        SkinnedMeshHandle handle = scene.LoadSkinnedMesh(Patch());
        scene.Begin();
        scene.DrawSkinned(new SkinnedInstanceDraw(handle, Matrix4x4.CreateTranslation(-40, 0, 0))
        { Motion = MotionKey.From(33), CastsShadows = false }, Joints());
        Assert.Single(scene.QueuedSkinnedInstancesForTests);
        Assert.Equal(1, Store(scene).PoseCount);
        harness.Render();
        Assert.Equal(1, scene.DrawnSkinnedInstances);
        scene.Begin();
        scene.DrawSkinned(new SkinnedInstanceDraw(handle, Matrix4x4.CreateTranslation(-400, 0, 0))
        { CastsShadows = false }, Joints());
        harness.Render();
        Assert.Equal(0, scene.DrawnSkinnedInstances);
        Assert.Empty((IList)Field(scene, gpu ? "_gpuSkinnedDraws" : "_cpuSkinnedDraws"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Missing_envelope_or_invalid_placement_refuses_without_mutation(bool outline, bool missing)
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.ForceTemporalForTests = true;
        SkinnedMeshHandle handle = scene.LoadSkinnedMesh(Patch());
        if (missing)
        {
            object entry = ((IList)Field(scene, "_skinnedMeshes"))[handle.Index]!;
            entry.GetType().GetField("DqBounds")!.SetValue(entry, null);
        }
        scene.Begin();
        var before = Snapshot(scene);
        Matrix4x4 world = Matrix4x4.Identity;
        if (!missing) world.M14 = .5f;
        Exception error = Assert.ThrowsAny<Exception>(() =>
        {
            if (outline) scene.DrawSkinnedOutline(handle, Joints(), world, Color.White, 1);
            else scene.DrawSkinned(new SkinnedInstanceDraw(handle, world) { Motion = MotionKey.From(1) }, Joints());
        });
        if (missing) Assert.IsType<NotSupportedException>(error);
        else Assert.IsType<ArgumentException>(error);
        Assert.Contains("DualQuaternionRigid", error.Message);
        Assert.Contains("consumer", error.Message);
        Assert.Contains($"{handle.Index}:{handle.Generation}", error.Message);
        Assert.Equal(before, Snapshot(scene));
    }

    internal static SkinnedGltfMesh Patch()
    {
        SkinnedGltfMesh patch = DqCascadeScene.Patch(SkinningProfile.DualQuaternionRigid);
        for (int i = 0; i < patch.Vertices.Length; i++) patch.Vertices[i].Position *= .5f;
        return new SkinnedGltfMesh(patch.Vertices, patch.Indices32, patch.InverseBind, patch.RestPose, null, patch.Profile);
    }

    internal static Matrix4x4[] Joints()
    {
        Matrix4x4[] joints = DqCascadeScene.HalfTurn;
        for (int i = 0; i < joints.Length; i++) joints[i] *= Matrix4x4.CreateTranslation(40, 0, 0);
        return joints;
    }

    internal static void Contains(MeshBounds bounds, Vector3 position)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            Assert.True(float.IsFinite(bounds.Min[axis]) && float.IsFinite(bounds.Max[axis]));
            Assert.InRange(position[axis], bounds.Min[axis], bounds.Max[axis]);
        }
    }

    static object Snapshot(Scene3D scene) => (Store(scene).PoseCount, Store(scene).DqCount, Store(scene).LbsCount,
        scene.QueuedSkinnedInstancesForTests.Count, ((IList)Field(scene, "_boneMatrices")).Count,
        scene.MeshOutlineGroupCount, scene.MeshOutlinePartCount, scene.OutlinePoseMatrixCount, scene.MotionKeyCounts);
    static PreparedSkinningStore Store(Scene3D scene) => (PreparedSkinningStore)Field(scene, "_preparedSkinning");
    static object Field(object owner, string name) => DqGpuReadback.Field(owner, name);
}
