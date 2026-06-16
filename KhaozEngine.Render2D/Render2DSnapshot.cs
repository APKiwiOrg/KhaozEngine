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
        public SpriteFont LoadFont(string ttfPath, float pixelHeight) => _core.LoadFont(ttfPath, pixelHeight);
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
                return Readback(gd, target, width, height);
            }
            finally { cl.Dispose(); fb.Dispose(); target.Dispose(); core.Dispose(); }
        }

        static byte[] Readback(IGpuDevice gd, IGpuTexture src, int w, int h)
        {
            var f = gd.Factory;
            using IGpuTexture staging = f.CreateTexture(GpuTextureDescription.Texture2D(
                (uint)w, (uint)h, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Staging));
            using (IGpuCommandList cl = f.CreateCommandList())
            {
                cl.Begin(); cl.CopyTexture(src, staging); cl.End();
                gd.Submit(cl); gd.WaitForIdle();
            }
            var outBytes = new byte[w * h * 4];
            MappedData map = gd.Map(staging, GpuMapMode.Read);
            unsafe
            {
                byte* data = (byte*)map.Data;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        uint si = (uint)(y * (int)map.RowPitch + x * 4);
                        int di = (y * w + x) * 4;
                        outBytes[di] = data[si]; outBytes[di + 1] = data[si + 1]; outBytes[di + 2] = data[si + 2]; outBytes[di + 3] = data[si + 3];
                    }
            }
            gd.Unmap(staging);
            return outBytes;
        }
    }
}
