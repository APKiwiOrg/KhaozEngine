using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render2D.Internal
{
    /// <summary>Owns the GPU device + SpriteBatch and the texture/font factories. Shared by the host and the snapshot.</summary>
    internal sealed class Render2DCore : IDisposable
    {
        public IGpuDevice Gd { get; }
        public SpriteBatch Batch { get; }
        readonly bool _ownsDevice;

        public Render2DCore(IGpuDevice gd, GpuOutputDescription output, bool ownsDevice = true)
        {
            Gd = gd;
            _ownsDevice = ownsDevice;
            Batch = new SpriteBatch(gd, output);
        }

        public Texture2D CreateTexture(byte[] rgba, int width, int height)
        {
            var t = Gd.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                (uint)width, (uint)height, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            Gd.UpdateTexture(t, rgba, 0, 0, (uint)width, (uint)height);
            return new Texture2D(Gd, t, width, height, ownsHandle: true);
        }

        public Texture2D LoadTexture(string pngPath)
        {
            ImageRgba img = ImageRgba.Load(pngPath);
            return CreateTexture(img.Pixels, img.Width, img.Height);
        }

        public SpriteFont LoadFont(string ttfPath, float pixelHeight, int oversample = 1) =>
            SpriteFont.Build(Gd, File.ReadAllBytes(ttfPath), pixelHeight, oversample);

        public SpriteFont LoadFont(byte[] ttf, float pixelHeight, int oversample = 1) =>
            SpriteFont.Build(Gd, ttf, pixelHeight, oversample);

        public SpriteFont LoadDefaultFont(float pixelHeight, int oversample = 1) =>
            SpriteFont.Build(Gd, DefaultFont.Bytes, pixelHeight, oversample);

        // A DpiFont bakes at the live device-pixel scale and re-bakes only when it changes; the baker closes over
        // this core's device so each (re)bake lands on the same GPU as the batch it will draw through. cacheSlots > 1
        // keeps several scales baked at once (for one face drawn at several scales in a pass, e.g. the boot screen).
        public DpiFont CreateDpiFont(byte[] ttf, float pixelHeight, int cacheSlots = 1) =>
            new DpiFont(pixelHeight, density => SpriteFont.Build(Gd, ttf, pixelHeight, density), cacheSlots);

        public DpiFont CreateDpiFont(string ttfPath, float pixelHeight, int cacheSlots = 1) =>
            CreateDpiFont(File.ReadAllBytes(ttfPath), pixelHeight, cacheSlots);

        public DpiFont CreateDefaultDpiFont(float pixelHeight, int cacheSlots = 1) =>
            CreateDpiFont(DefaultFont.Bytes, pixelHeight, cacheSlots);

        public void Dispose() { Batch.Dispose(); if (_ownsDevice) Gd.Dispose(); }

        /// <summary>
        /// Offscreen 2D render into a fresh sampleable <see cref="Texture2D"/> on <paramref name="gd"/>. Unlike
        /// <see cref="Render2DSnapshot.Capture"/> (which owns a throwaway headless device and reads back to the
        /// CPU), this stays on the supplied live device, so the <paramref name="draw"/> callback can use
        /// textures/fonts already created on that device and the result is a GPU texture you can sample. The
        /// returned <see cref="Texture2D"/> owns the GPU target; the caller disposes it.
        /// </summary>
        public static Texture2D RenderToTexture(IGpuDevice gd, int width, int height, Color clear, Action<SpriteBatch> draw)
        {
            ArgumentNullException.ThrowIfNull(draw);
            width = Math.Max(1, width);
            height = Math.Max(1, height);

            var f = gd.Factory;
            IGpuTexture target = f.CreateTexture(GpuTextureDescription.Texture2D(
                (uint)width, (uint)height, GpuPixelFormat.R8G8B8A8UNorm,
                GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            IGpuFramebuffer fb = f.CreateFramebuffer(null, target);
            // A batch whose pipeline targets the offscreen framebuffer's format, sharing the same device so any
            // texture/font created on it draws straight in. Disposed here; only the target texture survives.
            var batch = new SpriteBatch(gd, fb.Outputs);
            IGpuCommandList cl = f.CreateCommandList();
            try
            {
                using (GpuRecording.Open(gd, cl, "Render2DSurface.CaptureToTexture"))
                {
                    cl.SetFramebuffer(fb);
                    cl.ClearColorTarget(0, clear);
                    batch.NewFrame(cl, width, height);
                    draw(batch);
                }
                gd.Submit(cl);
                gd.WaitForIdle();
            }
            finally
            {
                cl.Dispose();
                batch.Dispose();
                fb.Dispose();
            }

            return new Texture2D(gd, target, width, height, ownsHandle: true);
        }

        /// <summary>
        /// As <see cref="RenderToTexture"/>, but reads the result back to a tightly-packed CPU RGBA8 buffer
        /// (<c>width * height * 4</c> bytes, row-major, top-left origin) and frees the GPU target. The on-device
        /// equivalent of <see cref="Render2DSnapshot.Capture"/> - it reuses the live device's textures/fonts
        /// rather than a throwaway headless one. For pixels a game needs on the CPU (e.g. a clipboard copy).
        /// </summary>
        public static byte[] RenderToRgba(IGpuDevice gd, int width, int height, Color clear, Action<SpriteBatch> draw)
        {
            ArgumentNullException.ThrowIfNull(draw);
            width = Math.Max(1, width);
            height = Math.Max(1, height);

            var f = gd.Factory;
            IGpuTexture target = f.CreateTexture(GpuTextureDescription.Texture2D(
                (uint)width, (uint)height, GpuPixelFormat.R8G8B8A8UNorm,
                GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            IGpuFramebuffer fb = f.CreateFramebuffer(null, target);
            var batch = new SpriteBatch(gd, fb.Outputs);
            IGpuCommandList cl = f.CreateCommandList();
            try
            {
                using (GpuRecording.Open(gd, cl, "Render2DSurface.CaptureToRgba"))
                {
                    cl.SetFramebuffer(fb);
                    cl.ClearColorTarget(0, clear);
                    batch.NewFrame(cl, width, height);
                    draw(batch);
                }
                gd.Submit(cl);
                gd.WaitForIdle();
                // The readback opens a list of its own, so it goes AFTER the scope above closes, not inside it.
                return GpuReadback.ToRgba(gd, target, width, height);
            }
            finally
            {
                cl.Dispose();
                batch.Dispose();
                fb.Dispose();
                target.Dispose();
            }
        }
    }
}
