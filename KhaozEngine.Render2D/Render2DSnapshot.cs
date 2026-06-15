using System;
using System.Numerics;
using Veldrid;
using KhaozEngine.Render2D.Internal;

namespace KhaozEngine.Render2D
{
    /// <summary>Draw surface handed to a <see cref="Render2DSnapshot"/> callback (Veldrid-free).</summary>
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
            var opts = new GraphicsDeviceOptions(false, null, false, ResourceBindingModel.Improved, true, true);
            GraphicsDevice gd = GraphicsDevice.CreateMetal(opts);
            var f = gd.ResourceFactory;
            Texture target = f.CreateTexture(TextureDescription.Texture2D(
                (uint)width, (uint)height, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.RenderTarget | TextureUsage.Sampled));
            Framebuffer fb = f.CreateFramebuffer(new FramebufferDescription(null, target));
            var core = new Render2DCore(gd, fb.OutputDescription);
            CommandList cl = f.CreateCommandList();
            try
            {
                cl.Begin();
                cl.SetFramebuffer(fb);
                cl.ClearColorTarget(0, new RgbaFloat(clear.X, clear.Y, clear.Z, clear.W));
                core.Batch.NewFrame(cl, width, height);
                draw(new Render2DContext(core, width, height));
                cl.End();
                gd.SubmitCommands(cl);
                gd.WaitForIdle();
                return Readback(gd, target, width, height);
            }
            finally { cl.Dispose(); fb.Dispose(); target.Dispose(); core.Dispose(); }
        }

        static byte[] Readback(GraphicsDevice gd, Texture src, int w, int h)
        {
            var f = gd.ResourceFactory;
            using Texture staging = f.CreateTexture(TextureDescription.Texture2D(
                (uint)w, (uint)h, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Staging));
            using (CommandList cl = f.CreateCommandList())
            {
                cl.Begin(); cl.CopyTexture(src, staging); cl.End();
                gd.SubmitCommands(cl); gd.WaitForIdle();
            }
            var outBytes = new byte[w * h * 4];
            MappedResource map = gd.Map(staging, MapMode.Read);
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
