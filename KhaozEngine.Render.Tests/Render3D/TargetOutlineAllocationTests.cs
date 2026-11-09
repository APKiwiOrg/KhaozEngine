using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class TargetOutlineAllocationTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 3)]
    public void Warm_outline_queue_reuses_group_and_part_storage_after_collection(int groups, int parts)
    {
        // #1749: warm group and part storage must survive frames and enum-cache collection.
        using var device = new FakeGpuDevice();
        using var scene = new Scene3D(device, new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm));
        MeshHandle mesh = scene.LoadMesh(MeshPrimitives.Box(1f));

        void Queue()
        {
            for (int index = 0; index < groups; index++)
            {
                MeshOutlineGroup group = scene.BeginMeshOutline(Color.White, 2f,
                    index % 2 == 0 ? MeshOutlineOcclusion.SceneDepth : MeshOutlineOcclusion.None);
                for (int part = 0; part < parts; part++)
                    scene.DrawMeshOutline(group, mesh, Matrix4x4.CreateTranslation(part, index, 0));
            }
        }

        scene.Begin();
        Queue();
        scene.Begin();
        Queue();
        scene.Begin();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        long allocated = AllocationMeasurement.Measure("warm target outline queue after collection", Queue);

        Assert.Equal(groups, scene.MeshOutlineGroupCount);
        Assert.Equal(groups * parts, scene.MeshOutlinePartCount);
        Assert.Equal(0L, allocated);
    }
}
