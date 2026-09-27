using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A STEADY FRAME THAT PRESENTS ALLOCATES NOTHING. Each frame renders a scene with staged uploads into an
    /// offscreen target, submits, drains and then calls <see cref="IGpuDevice.Present"/>, which is the frame boundary
    /// a windowed game crosses every frame.
    /// <para>
    /// <b>HEADLESS, AND THAT IS STILL THE DEVICE'S HALF OF A PRESENT.</b> A headless device has no swapchain, so the
    /// present itself is skipped, and what runs is the device's own frame boundary: the uniform ring opening its next
    /// segment and, on the native Vulkan backend, the retire list draining every deferred destroy the timeline has
    /// passed. That drain once built a closure on every call, 88 bytes per present, which no frame reading saw because
    /// none of them presented.
    /// </para>
    /// </summary>
    [Collection("AllocSensitive")]
    public sealed class PresentFrameAllocationGpuTests(ITestOutputHelper output)
    {
        const int W = 320, H = 180, Warm = 8, Measured = 16;

        [GpuFact]
        public void A_steady_frame_that_presents_allocates_nothing()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            using IGpuTexture target = gd.Factory.CreateTexture(GpuTextureDescription.Texture2D(W, H,
                GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer fb = gd.Factory.CreateFramebuffer(null, target);
            using IGpuCommandList cl = gd.Factory.CreateCommandList();
            using var scene = new Scene3D(gd, fb.Outputs);
            scene.Post.UseSmoothPreset();
            scene.Post.Bloom.Enabled = true;
            scene.Camera.OrthoSize = 10f;
            MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1f));
            int n = 0;
            long presentBytes = 0;

            void Frame()
            {
                scene.Begin();
                scene.EffectTimeSeconds = n / 60f;
                scene.Camera.Target = new Vector3(0.05f * n, 0f, 0f);
                scene.Draw(box, Matrix4x4.CreateScale(40f, 0.2f, 40f) * Matrix4x4.CreateTranslation(0f, -0.6f, 0f));
                for (int i = 0; i < 16; i++)
                    scene.Draw(box, Matrix4x4.CreateTranslation(i - 8f, 0.5f, 0.02f * n));
                scene.DrawBeam(new Vector3(-4f, 1f, 0f), new Vector3(4f, 1f, 0f), 0.3f, Color.White);
                scene.PrepareFrame();
                using (GpuRecording.Open(gd, cl, nameof(PresentFrameAllocationGpuTests)))
                    scene.RenderInternal(cl, W, H, fb);
                gd.Submit(cl);
                gd.WaitForIdle();
                long before = GC.GetAllocatedBytesForCurrentThread();
                gd.Present();
                presentBytes += GC.GetAllocatedBytesForCurrentThread() - before;
                n++;
            }
            void Frames(int count) { for (int i = 0; i < count; i++) Frame(); }

            Frames(Warm);

            presentBytes = 0;
            long first = Allocated(() => Frames(Measured));
            long firstPresent = presentBytes;
            long retry = first == 0 ? 0 : Allocated(() => Frames(Measured));   // one retry, as AllocAssert allows

            output.WriteLine($"{Measured} steady presenting frames on {gd.Backend}: {first} bytes ({firstPresent} in "
                + $"Present), retry {retry}");
            Assert.True(retry == 0, $"{Measured} steady presenting frames on {gd.Backend} allocated {first} bytes, "
                + $"{firstPresent} of them in Present, and {retry} on the retry");
        }

        static long Allocated(Action frames)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            frames();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }
}
