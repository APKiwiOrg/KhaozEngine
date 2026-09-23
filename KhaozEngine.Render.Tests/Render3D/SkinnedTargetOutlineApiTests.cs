using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class SkinnedTargetOutlineApiTests
{
    static readonly GpuOutputDescription Outputs = new(null, GpuPixelFormat.R8G8B8A8UNorm);

    [Fact]
    public void One_group_collects_rigid_and_skinned_parts_with_one_group_count()
    {
        using SceneHarness h = SceneHarness.Create();
        MeshHandle rigid = h.Scene.LoadMesh(MeshPrimitives.Box(1f));
        SkinnedGltfMesh tube = Tube();
        SkinnedMeshHandle skinned = h.Scene.LoadSkinnedMesh(tube);
        h.Scene.Begin();
        MeshOutlineGroup group = h.Scene.BeginMeshOutline(Color.White, 1.25f);

        h.Scene.DrawMeshOutline(group, rigid, Matrix4x4.Identity);
        h.Scene.DrawSkinnedOutline(group, skinned, tube.RestPose, Matrix4x4.Identity);

        Assert.Equal(1, h.Scene.MeshOutlineGroupCount);
        Assert.Equal(2, h.Scene.MeshOutlinePartCount);
        Assert.Equal(tube.BoneCount, h.Scene.OutlinePoseMatrixCount);
    }

    [Fact]
    public void Submission_copies_the_composed_pose()
    {
        using SceneHarness h = SceneHarness.Create();
        SkinnedGltfMesh tube = Tube();
        SkinnedMeshHandle mesh = h.Scene.LoadSkinnedMesh(tube);
        Matrix4x4[] pose = BentPose(tube);
        Matrix4x4 expected = tube.InverseBind[1] * pose[1];
        h.Scene.Begin();
        MeshOutlineGroup group = h.Scene.BeginMeshOutline(Color.White, 1.25f);

        h.Scene.DrawSkinnedOutline(group, mesh, pose, Matrix4x4.Identity);
        Matrix4x4 copied = h.Scene.OutlinePoseMatrixAt(1);
        pose[1] = Matrix4x4.CreateScale(99f);

        Assert.Equal(expected, copied);
        Assert.Equal(copied, h.Scene.OutlinePoseMatrixAt(1));
        Assert.NotEqual(pose[1], h.Scene.OutlinePoseMatrixAt(1));
    }

    [Fact]
    public void Invalid_submission_is_atomic()
    {
        using SceneHarness h = SceneHarness.Create();
        using SceneHarness other = SceneHarness.Create();
        SkinnedGltfMesh tube = Tube();
        SkinnedMeshHandle mesh = h.Scene.LoadSkinnedMesh(tube);
        Matrix4x4[] shortPose = tube.RestPose[..^1];

        h.Scene.Begin();
        other.Scene.Begin();
        MeshOutlineGroup foreign = other.Scene.BeginMeshOutline(Color.White, 1.25f);
        AssertAtomic(h.Scene, "group", () =>
            h.Scene.DrawSkinnedOutline(foreign, mesh, shortPose, Matrix4x4.Identity));

        h.Scene.Begin();
        MeshOutlineGroup stale = h.Scene.BeginMeshOutline(Color.White, 1.25f);
        h.Scene.Begin();
        AssertAtomic(h.Scene, "group", () =>
            h.Scene.DrawSkinnedOutline(stale, mesh, shortPose, Matrix4x4.Identity));

        MeshOutlineGroup valid = h.Scene.BeginMeshOutline(Color.White, 1.25f);
        AssertAtomic(h.Scene, "boneMatrices", () =>
            h.Scene.DrawSkinnedOutline(valid, mesh, shortPose, Matrix4x4.Identity));

        SkinnedGltfMesh oversized = SkinnedMeshBuilder.BuildTube(0.25f, 2f, 4, 6,
            SkinningMath.MaxBonesPerDraw + 1, Axis.Z);
        SkinnedMeshHandle oversizedMesh = h.Scene.LoadSkinnedMesh(oversized);
        AssertAtomic(h.Scene, "boneMatrices", () =>
            h.Scene.DrawSkinnedOutline(valid, oversizedMesh, oversized.RestPose, Matrix4x4.Identity));
    }

    [Fact]
    public void Invalid_and_unloaded_handles_do_not_retain()
    {
        using SceneHarness h = SceneHarness.Create();
        SkinnedGltfMesh tube = Tube();
        SkinnedMeshHandle unloaded = h.Scene.LoadSkinnedMesh(tube);
        h.Scene.UnloadSkinnedMesh(unloaded);
        h.Scene.Begin();
        MeshOutlineGroup group = h.Scene.BeginMeshOutline(Color.White, 1.25f);

        h.Scene.DrawSkinnedOutline(group, default, tube.RestPose, Matrix4x4.Identity);
        h.Scene.DrawSkinnedOutline(group, unloaded, tube.RestPose, Matrix4x4.Identity);

        Assert.Equal(0, h.Scene.MeshOutlinePartCount);
        Assert.Equal(0, h.Scene.OutlinePoseMatrixCount);
    }

    [Fact]
    public void Convenience_overloads_create_one_part_group_with_requested_occlusion()
    {
        using SceneHarness defaultOcclusion = SceneHarness.Create();
        SkinnedGltfMesh defaultTube = Tube();
        SkinnedMeshHandle defaultMesh = defaultOcclusion.Scene.LoadSkinnedMesh(defaultTube);
        defaultOcclusion.Scene.Begin();

        defaultOcclusion.Scene.DrawSkinnedOutline(defaultMesh, defaultTube.RestPose,
            Matrix4x4.Identity, Color.White, 1.25f);

        Assert.Equal(1, defaultOcclusion.Scene.MeshOutlineGroupCount);
        Assert.Equal(1, defaultOcclusion.Scene.MeshOutlinePartCount);
        Assert.Equal(MeshOutlineOcclusion.SceneDepth, defaultOcclusion.Scene.MeshOutlineOcclusionAt(0));

        using SceneHarness throughGeometry = SceneHarness.Create();
        SkinnedGltfMesh throughTube = Tube();
        SkinnedMeshHandle throughMesh = throughGeometry.Scene.LoadSkinnedMesh(throughTube);
        throughGeometry.Scene.Begin();

        throughGeometry.Scene.DrawSkinnedOutline(throughMesh, throughTube.RestPose,
            Matrix4x4.Identity, Color.White, 1.25f, MeshOutlineOcclusion.None);

        Assert.Equal(1, throughGeometry.Scene.MeshOutlineGroupCount);
        Assert.Equal(1, throughGeometry.Scene.MeshOutlinePartCount);
        Assert.Equal(MeshOutlineOcclusion.None, throughGeometry.Scene.MeshOutlineOcclusionAt(0));
    }

    [Fact]
    public void Dissolve_is_clamped_to_the_supported_range()
    {
        using SceneHarness h = SceneHarness.Create();
        SkinnedGltfMesh tube = Tube();
        SkinnedMeshHandle mesh = h.Scene.LoadSkinnedMesh(tube);
        h.Scene.Begin();
        MeshOutlineGroup group = h.Scene.BeginMeshOutline(Color.White, 1.25f);

        h.Scene.DrawSkinnedOutlineDissolved(group, mesh, tube.RestPose,
            Matrix4x4.Identity, -2f, false);
        h.Scene.DrawSkinnedOutlineDissolved(group, mesh, tube.RestPose,
            Matrix4x4.Identity, 2f, true);

        Assert.Equal(0f, h.Scene.MeshOutlineDissolveAt(0, 0));
        Assert.Equal(1f, h.Scene.MeshOutlineDissolveAt(0, 1));
    }

    [Fact]
    public void Begin_clears_groups_parts_and_outline_pose_together()
    {
        using SceneHarness h = SceneHarness.Create();
        SkinnedGltfMesh tube = Tube();
        SkinnedMeshHandle mesh = h.Scene.LoadSkinnedMesh(tube);
        h.Scene.Begin();
        MeshOutlineGroup group = h.Scene.BeginMeshOutline(Color.White, 1.25f);
        h.Scene.DrawSkinnedOutline(group, mesh, tube.RestPose, Matrix4x4.Identity);

        h.Scene.Begin();

        Assert.Equal(0, h.Scene.MeshOutlineGroupCount);
        Assert.Equal(0, h.Scene.MeshOutlinePartCount);
        Assert.Equal(0, h.Scene.OutlinePoseMatrixCount);
    }

    static void AssertAtomic(Scene3D scene, string parameterName, Action submit)
    {
        int partsBefore = scene.MeshOutlinePartCount;
        int poseBefore = scene.OutlinePoseMatrixCount;

        ArgumentException exception = Assert.Throws<ArgumentException>(submit);

        Assert.Equal(parameterName, exception.ParamName);
        Assert.Equal(partsBefore, scene.MeshOutlinePartCount);
        Assert.Equal(poseBefore, scene.OutlinePoseMatrixCount);
    }

    static SkinnedGltfMesh Tube() =>
        SkinnedMeshBuilder.BuildTube(0.25f, 2f, 4, 6, 3, Axis.Z);

    static Matrix4x4[] BentPose(SkinnedGltfMesh tube)
    {
        Matrix4x4[] pose = (Matrix4x4[])tube.RestPose.Clone();
        pose[1] = Matrix4x4.CreateRotationY(0.45f) * pose[1];
        return pose;
    }

    sealed class SceneHarness : IDisposable
    {
        readonly FakeGpuDevice _device;

        public Scene3D Scene { get; }

        SceneHarness()
        {
            _device = new FakeGpuDevice();
            Scene = new Scene3D(_device, Outputs);
        }

        public static SceneHarness Create() => new();

        public void Dispose()
        {
            Scene.Dispose();
            _device.Dispose();
        }
    }
}
