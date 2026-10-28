using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeWaterOpticsMergeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ViewportPreparationReservesNativeSlotsBeforeCreatingOpticalBindings(bool bound, bool column)
    {
        using var fixture = NativeSceneFixtures.Create("water-notch");
        var source = NativeSceneFixtures.Open(fixture, false).Acquire(
            NativeSceneFixtures.WaterWindow(0, 10, 7) with { MotionPaddingMetres = 1 }).Scene!;
        var batch = NativeMapWater.Prepare(MapWaterScopedCompiler.Compile(source),
            new WaterLook { SwellAmplitude = 0f }, new WaterSettings());
        var recorder = new MapDrawRecorder();
        batch.Register(new("closure", "source", "scope", "water", 6), recorder);
        var resource = Assert.IsType<MapWaterResource>(Assert.Single(recorder.Snapshot().Resources).Payload);
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        using var scene = new Scene3D(device, new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm));
        scene.Post.Starfield = false;
        scene.Post.RenderScale = RenderScale.MatchViewport;
        scene.Post.Water.SwellAmplitude = 0f;
        var mesh = scene.LoadWaterMesh(resource.Mesh, resource.ClipData);
        WaterOpticsBinding optics = default;
        if (bound)
        {
            var field = scene.LoadWaterOpticsField(new WaterOpticsField(2, 2, [2f, 2f, 2f, 2f]));
            optics = new(field, new Vector3(-5f, 0f, -5f), new Vector2(10f));
        }
        scene.Begin();
        scene.DrawWater(new WaterPlane(0f, 0f, 0f, 5f, optics: optics));
        for (int i = 0; i < 5; i++)
            scene.DrawWater(new WaterSurfaceMesh(mesh, new(i * 10, 0, 0), Matrix4x4.Identity,
                new WaterLook { SwellAmplitude = 0f, WaterColumnLight = column }));
        scene.PrepareFrame(128, 96);
        var waterSets = factory.ResourceSets.Where(s => s.Resources.Length ==
            VulkanDescriptorLimitTests.ShippedLayouts["Water"].Elements.Length).ToArray();
        Assert.NotEmpty(waterSets);
        Assert.All(waterSets, set =>
        {
            var range = Assert.IsType<GpuBufferRange>(set.Resources[6]);
            Assert.True(range.Buffer.SizeInBytes >= 6 * WaterRenderer.SlotBytes,
                "Preparation must reserve legacy and native water slots before creating resource sets.");
        });
        using var commands = new NullGpuCommandList();
        using var target = factory.CreateFramebuffer(null, scene.ResolvedDepthTarget);
        scene.RenderInternal(commands, 128, 96, target);
        Assert.All(waterSets, set => Assert.False(set.Disposed));
        Assert.Equal(new WaterColumnDiagnostics(column ? 5 : 0, column,
            column ? WaterColumnCopyPath.SingleSampleCopy : WaterColumnCopyPath.None), scene.WaterColumnLightDiagnostics);
    }
}
