using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render2D.Internal;

namespace KhaozEngine.Render2D
{
    /// <summary>Draw surface handed to a <see cref="Render2DSnapshot"/> callback (backend-free).</summary>
    public sealed class Render2DContext
    {
        readonly Render2DCore _core;
        public int Width { get; }
        public int Height { get; }
        internal Render2DContext(Render2DCore core, int w, int h) { _core = core; Width = w; Height = h; }

        public SpriteBatch Batch => _core.Batch;
        public Texture2D LoadTexture(string pngPath) => _core.LoadTexture(pngPath);
        public Texture2D CreateTexture(byte[] rgba, int width, int height) => _core.CreateTexture(rgba, width, height);
        public SpriteFont LoadFont(string ttfPath, float pixelHeight, int oversample = 1) => _core.LoadFont(ttfPath, pixelHeight, oversample);
    }

    /// <summary>Headless offscreen 2D render to a CPU RGBA buffer (no window). For tooling/tests; needs a Metal GPU.</summary>
    public static class Render2DSnapshot
    {
        public static byte[] Capture(int width, int height, Vector4 clear, Action<Render2DContext> draw)
        {
            // NOTE: the context is intentionally NOT disposed here — as in the original inline CreateMetal path,
            // tearing down the Metal device after this 2D font/texture pass crashes in the backend. The device
            // is left to process teardown (this is a tooling/test-only snapshot helper). Matches baseline behaviour.
            GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            var f = gd.Factory;
            IGpuTexture target = f.CreateTexture(GpuTextureDescription.Texture2D(
                (uint)width, (uint)height, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            IGpuFramebuffer fb = f.CreateFramebuffer(null, target);
            // Match baseline: core owns the device's disposal (the GpuDeviceContext wrapper is intentionally left
            // undisposed so the device is torn down exactly once, here, via core.Dispose()).
            var core = new Render2DCore(gd, fb.Outputs, ownsDevice: true);
            IGpuCommandList cl = f.CreateCommandList();
            try
            {
                cl.Begin();
                cl.SetFramebuffer(fb);
                cl.ClearColorTarget(0, clear);
                core.Batch.NewFrame(cl, width, height);
                draw(new Render2DContext(core, width, height));
                cl.End();
                gd.Submit(cl);
                gd.WaitForIdle();
                return GpuReadback.ToRgba(gd, target, width, height);
            }
            finally { cl.Dispose(); fb.Dispose(); target.Dispose(); core.Dispose(); }
        }

        /// <summary>
        /// As <see cref="Capture"/>, but encodes the result to a PNG (via the dependency-free <see cref="Png"/>
        /// encoder) and writes it to <paramref name="path"/>. Returns the raw RGBA buffer too (for assertions /
        /// further processing). The one-call path a game's snapshot tool needs.
        /// </summary>
        public static byte[] CaptureToPng(string path, int width, int height, Vector4 clear, Action<Render2DContext> draw)
        {
            byte[] rgba = Capture(width, height, clear, draw);
            Png.Write(path, rgba, width, height);
            return rgba;
        }

    }
}
