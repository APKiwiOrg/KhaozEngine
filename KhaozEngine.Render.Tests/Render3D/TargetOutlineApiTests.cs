using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class TargetOutlineApiTests
{
    static readonly GpuOutputDescription Outputs = new(null, GpuPixelFormat.R8G8B8A8UNorm);

    [Fact]
    public void Group_collects_parts_and_Begin_invalidates_the_previous_frame()
    {
        using var device = new FakeGpuDevice();
        using var scene = new Scene3D(device, Outputs);
        MeshHandle mesh = scene.LoadMesh(MeshPrimitives.Box(1f));

        scene.Begin();
        MeshOutlineGroup group = scene.BeginMeshOutline(new Color(1f, 0f, 0f, 1f), 1.25f);
        scene.DrawMeshOutline(group, mesh, Matrix4x4.Identity);
        scene.DrawMeshOutline(group, mesh, Matrix4x4.CreateTranslation(1f, 0f, 0f));

        Assert.Equal(1, scene.MeshOutlineGroupCount);
        Assert.Equal(2, scene.MeshOutlinePartCount);

        scene.Begin();
        Assert.Throws<ArgumentException>(() =>
            scene.DrawMeshOutline(group, mesh, Matrix4x4.Identity));
    }

    [Fact]
    public void Reused_group_starts_empty_with_its_new_settings()
    {
        using var device = new FakeGpuDevice();
        using var scene = new Scene3D(device, Outputs);
        MeshHandle mesh = scene.LoadMesh(MeshPrimitives.Box(1f));
        scene.Begin();
        MeshOutlineGroup old = scene.BeginMeshOutline(Color.White, 1.25f);
        scene.DrawMeshOutlineDissolved(old, mesh, Matrix4x4.Identity, .75f, false);
        scene.DrawMeshOutline(old, mesh, Matrix4x4.Identity);

        scene.Begin();
        MeshOutlineGroup group = scene.BeginMeshOutline(Color.White, 2f, MeshOutlineOcclusion.None);

        Assert.Equal(old.Index, group.Index);
        Assert.Equal(1, scene.MeshOutlineGroupCount);
        Assert.Equal(0, scene.MeshOutlinePartCount);
        Assert.Equal(MeshOutlineOcclusion.None, scene.MeshOutlineOcclusionAt(group.Index));
        Assert.Throws<ArgumentOutOfRangeException>(() => scene.MeshOutlineDissolveAt(group.Index, 0));
        Assert.Throws<ArgumentException>(() => scene.DrawMeshOutline(old, mesh, Matrix4x4.Identity));
        scene.DrawMeshOutlineDissolved(group, mesh, Matrix4x4.Identity, .25f, false);
        Assert.Equal(.25f, scene.MeshOutlineDissolveAt(group.Index, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => scene.MeshOutlineDissolveAt(group.Index, 1));
    }

    [Fact]
    public void Group_from_another_scene_is_rejected()
    {
        using var deviceA = new FakeGpuDevice();
        using var deviceB = new FakeGpuDevice();
        using var sceneA = new Scene3D(deviceA, Outputs);
        using var sceneB = new Scene3D(deviceB, Outputs);
        MeshHandle mesh = sceneB.LoadMesh(MeshPrimitives.Box(1f));
        sceneA.Begin();
        sceneB.Begin();

        MeshOutlineGroup group = sceneA.BeginMeshOutline(Color.White, 1.25f);

        Assert.Throws<ArgumentException>(() =>
            sceneB.DrawMeshOutline(group, mesh, Matrix4x4.Identity));
    }

    [Fact]
    public void Group_keeps_its_occlusion_and_defaults_to_scene_depth()
    {
        using var device = new FakeGpuDevice();
        using var scene = new Scene3D(device, Outputs);
        scene.Begin();

        MeshOutlineGroup occluded = scene.BeginMeshOutline(Color.White, 1.25f);
        MeshOutlineGroup through = scene.BeginMeshOutline(Color.White, 1.25f, MeshOutlineOcclusion.None);

        Assert.Equal(MeshOutlineOcclusion.SceneDepth, scene.MeshOutlineOcclusionAt(occluded.Index));
        Assert.Equal(MeshOutlineOcclusion.None, scene.MeshOutlineOcclusionAt(through.Index));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            scene.BeginMeshOutline(Color.White, 1.25f, (MeshOutlineOcclusion)2));
    }

    [Fact]
    public void Every_defined_occlusion_is_accepted_and_an_undefined_value_is_rejected()
    {
        using var device = new FakeGpuDevice();
        using var scene = new Scene3D(device, Outputs);
        scene.Begin();
        MeshOutlineOcclusion[] defined = Enum.GetValues<MeshOutlineOcclusion>();

        foreach (MeshOutlineOcclusion occlusion in defined)
        {
            MeshOutlineGroup group = scene.BeginMeshOutline(Color.White, 1.25f, occlusion);
            Assert.Equal(occlusion, scene.MeshOutlineOcclusionAt(group.Index));
        }
        foreach (MeshOutlineOcclusion undefined in new[] { defined.Max() + 1, (MeshOutlineOcclusion)(-1) })
        {
            var failure = Assert.Throws<ArgumentOutOfRangeException>(() =>
                scene.BeginMeshOutline(Color.White, 1.25f, undefined));
            Assert.Equal("occlusion", failure.ParamName);
            Assert.Equal(undefined, failure.ActualValue);
            Assert.StartsWith("unknown outline occlusion.", failure.Message);
        }
    }

    // Engine issue 1515. A collection can reclaim the runtime's enum metadata cache, so the warm validation must not
    // rebuild it.
    [Fact]
    public void Warm_begin_allocates_nothing_after_a_full_collection()
    {
        using var device = new FakeGpuDevice();
        using var scene = new Scene3D(device, Outputs);
        scene.Begin();
        _ = scene.BeginMeshOutline(Color.White, 1.25f, MeshOutlineOcclusion.None);
        scene.Begin();
        _ = scene.BeginMeshOutline(Color.White, 1.25f, MeshOutlineOcclusion.None);
        scene.Begin();
        Action begin = () => _ = scene.BeginMeshOutline(Color.White, 1.25f, MeshOutlineOcclusion.None);

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long allocated = AllocationMeasurement.Measure("warm target outline begin after a full collection", begin);

        Assert.Equal(0, allocated);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(0f)]
    [InlineData(0.49f)]
    [InlineData(8.01f)]
    public void Pixel_width_outside_the_supported_range_is_rejected(float widthPixels)
    {
        using var device = new FakeGpuDevice();
        using var scene = new Scene3D(device, Outputs);
        scene.Begin();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            scene.BeginMeshOutline(Color.White, widthPixels));
    }
}
