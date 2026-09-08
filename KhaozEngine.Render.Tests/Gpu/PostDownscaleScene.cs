using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

public sealed class PostDownscaleScene : IDisposable
{
    public const int Width = 1280;
    public const int Height = 720;
    GpuDeviceContext? gpu;
    IGpuTexture? target;
    IGpuFramebuffer? framebuffer;
    IGpuCommandList? commands;
    Scene3D? scene;
    MeshHandle bar;

    public byte[] Capture(bool mipFilter, bool fxaa, float offset)
    {
        if (gpu is null)
        {
            gpu = GpuDeviceContext.CreateHeadless();
            IGpuResourceFactory factory = gpu.GpuDevice.Factory;
            target = factory.CreateTexture(GpuTextureDescription.Texture2D(Width, Height,
                GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            framebuffer = factory.CreateFramebuffer(null, target);
            commands = factory.CreateCommandList();
            scene = new Scene3D(gpu.GpuDevice, framebuffer.Outputs);
            scene.Post.UseSmoothPreset();
            scene.Post.RenderScale = RenderScale.FixedInternal;
            scene.Post.Hdr.Enabled = true;
            scene.Post.AmbientColor = Color.White;
            scene.Camera.Azimuth = 0;
            scene.Camera.Elevation = 0;
            scene.Camera.AspectRatio = Width / (float)Height;
            scene.Camera.OrthoSize = 5;
            scene.Camera.Target = Vector3.Zero;
            bar = scene.LoadMesh(MeshPrimitives.Box(1));
        }

        scene!.Post.MipFilterFixedInternalDownscale = mipFilter;
        scene.Post.Quality.AntiAliasing = fxaa ? AntiAliasing.Fxaa : AntiAliasing.Off;
        scene.Begin();
        for (int index = 0; index < 32; index++)
        {
            Matrix4x4 world = Matrix4x4.CreateScale(.12f, 8, .1f)
                * Matrix4x4.CreateRotationZ(.52f)
                * Matrix4x4.CreateTranslation(-7.5f + index * .48f + offset, 0, 0);
            Color tint = (index % 3) switch
            {
                0 => new Color(.9f, .25f, .15f, 1),
                1 => new Color(.2f, .9f, .3f, 1),
                _ => new Color(.2f, .35f, .95f, 1),
            };
            scene.Draw(bar, world, tint);
        }
        scene.PrepareFrame();
        using (GpuRecording.Open(gpu.GpuDevice, commands!, "PostDownscaleTests"))
            scene.RenderInternal(commands!, Width, Height, framebuffer!);
        gpu.GpuDevice.Submit(commands!);
        gpu.GpuDevice.WaitForIdle();
        return GpuReadback.ToRgba(gpu.GpuDevice, target!, Width, Height);
    }

    public void Dispose()
    {
        scene?.Dispose();
        commands?.Dispose();
        framebuffer?.Dispose();
        target?.Dispose();
        gpu?.Dispose();
    }
}
