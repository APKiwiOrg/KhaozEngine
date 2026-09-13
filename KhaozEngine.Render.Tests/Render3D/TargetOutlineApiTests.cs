using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

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
