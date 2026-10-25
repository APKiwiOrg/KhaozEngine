using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Render3DPreview owns its target size, so it prepares bound water through the viewport path itself.</summary>
public sealed class Render3DPreviewWaterPrepareTests
{
    const int W = 128, H = 96;

    sealed class Host : IDisposable
    {
        internal readonly FakeGpuDevice Device = new();
        internal readonly Render3DPreview Preview;
        internal readonly WaterPlane Plane;
        internal FakeGpuResourceFactory Factory => (FakeGpuResourceFactory)Device.Factory;
        internal Host()
        {
            Preview = new Render3DPreview(Device, W, H);
            Preview.Scene.Post.RenderScale = RenderScale.MatchViewport;
            Preview.Scene.Post.Water.SwellAmplitude = 0f;
            WaterOpticsHandle handle = Preview.Scene.LoadWaterOpticsField(new WaterOpticsField(2, 2, [2f, 2f, 2f, 2f]));
            Plane = new WaterPlane(0f, 0f, 0f, 5f,
                optics: new WaterOpticsBinding(handle, new Vector3(-5f, 0f, -5f), new Vector2(10f)));
        }
        public void Dispose() { Preview.Dispose(); Device.Dispose(); }
    }

    [Fact]
    public void CapturePreparesBoundWaterAtThePreviewSize()
    {
        using var h = new Host();
        h.Preview.Capture(scene => scene.DrawWater(h.Plane));
        Assert.Equal((uint)W, h.Preview.Scene.ResolvedDepthTarget.Width);
        Assert.Equal((uint)H, h.Preview.Scene.ResolvedDepthTarget.Height);
        h.Preview.Resize(160, 120);
        h.Preview.Capture(scene => scene.DrawWater(h.Plane));
        Assert.Equal(160u, h.Preview.Scene.ResolvedDepthTarget.Width);
        Assert.Equal(120u, h.Preview.Scene.ResolvedDepthTarget.Height);
        Assert.Equal(1, h.Preview.Scene.WaterOpticsStats.UploadCount);
    }

    [Fact]
    public void CallbackThatPreparesItselfMatchesTheHostPreparation()
    {
        using var hosted = new Host();
        using var manual = new Host();
        for (int frame = 0; frame < 2; frame++)
        {
            hosted.Preview.Capture(scene => scene.DrawWater(hosted.Plane));
            manual.Preview.Capture(scene =>
            {
                scene.DrawWater(manual.Plane);
                scene.PrepareFrame(W, H);
            });
        }
        Assert.Equal(hosted.Preview.Scene.WaterOpticsStats, manual.Preview.Scene.WaterOpticsStats);
        Assert.Equal(hosted.Preview.Scene.ResolvedDepthTarget.Width, manual.Preview.Scene.ResolvedDepthTarget.Width);
        Assert.Equal(hosted.Preview.Scene.ResolvedDepthTarget.Height, manual.Preview.Scene.ResolvedDepthTarget.Height);
        Assert.Equal(hosted.Factory.Textures.Count, manual.Factory.Textures.Count);
        Assert.Equal(hosted.Factory.ResourceSets.Count, manual.Factory.ResourceSets.Count);
        Assert.Equal(hosted.Factory.Buffers.Count, manual.Factory.Buffers.Count);
    }
}
