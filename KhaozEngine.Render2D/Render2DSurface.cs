using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D.Internal;
using KhaozEngine.Windowing;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// A 2D drawing surface bound to a <see cref="AppWindow"/> (from KhaozEngine.Windowing): it builds a
    /// <see cref="SpriteBatch"/> + texture/font loaders on the window's GPU device, so you draw a 2D scene
    /// into the window's frames. The window owns the device and the frame loop; this just renders into it.
    /// </summary>
    public sealed class Render2DSurface : IDisposable
    {
        readonly Render2DCore _core;

        public Render2DSurface(AppWindow window)
        {
            // borrow the window's GPU device (it owns it); don't dispose it here.
            _core = new Render2DCore(window.GpuDevice, window.GpuDevice.SwapchainFramebuffer!.Outputs, ownsDevice: false);
        }

        public SpriteBatch Batch => _core.Batch;
        public Texture2D LoadTexture(string pngPath) => _core.LoadTexture(pngPath);
        public Texture2D CreateTexture(byte[] rgba, int width, int height) => _core.CreateTexture(rgba, width, height);

        /// <summary>
        /// Decode an image file (PNG/JPG/...) to a CPU-side <see cref="ImageRgba"/> - the raw RGBA pixels, no GPU
        /// texture and no GPU round-trip. For pixels a game wants on the CPU, e.g. rebuilding an opaque-pixel
        /// collision mask. To also draw it, pass <c>img.Pixels</c> to <see cref="CreateTexture"/> (no re-decode).
        /// </summary>
        public ImageRgba LoadImageRgba(string path) => ImageRgba.Load(path);
        /// <summary>
        /// Bake a TTF into a <see cref="SpriteFont"/>. Pass <paramref name="oversample"/> &gt; 1 (2-3) for crisp text
        /// when a design-space viewport upscales to a higher-resolution framebuffer; layout is unchanged.
        /// </summary>
        public SpriteFont LoadFont(string ttfPath, float pixelHeight, int oversample = 1) => _core.LoadFont(ttfPath, pixelHeight, oversample);

        /// <summary>Bake a <see cref="SpriteFont"/> from raw TTF bytes (no filesystem path) - the cross-platform core path.</summary>
        public SpriteFont LoadFont(byte[] ttf, float pixelHeight, int oversample = 1) => _core.LoadFont(ttf, pixelHeight, oversample);

        /// <summary>Bake a <see cref="SpriteFont"/> from a <see cref="FontManager"/> key (resolved to bytes, then baked).</summary>
        public SpriteFont LoadFont(FontManager fonts, string key, float pixelHeight, int oversample = 1) =>
            _core.LoadFont(fonts.GetFontBytes(key), pixelHeight, oversample);

        /// <summary>Bake a <see cref="SpriteFont"/> from the engine's embedded default face (<see cref="DefaultFont"/>); no system font, no path.</summary>
        public SpriteFont LoadDefaultFont(float pixelHeight, int oversample = 1) => _core.LoadDefaultFont(pixelHeight, oversample);

        /// <summary>Bind this frame's command list/viewport to the batch. Call once per frame before drawing.</summary>
        public void NewFrame(Frame frame) => _core.Batch.NewFrame(frame.Commands, frame.Width, frame.Height);

        /// <summary>
        /// Render an offscreen 2D pass into a fresh sampleable <see cref="Texture2D"/> on THIS surface's live GPU
        /// device. The <paramref name="draw"/> callback gets its own batch (do the usual <c>Begin(..)/End()</c>
        /// passes inside it) and can draw textures/fonts already loaded on this surface - unlike
        /// <see cref="Render2DSnapshot.Capture"/>, which spins up a throwaway headless device and cannot reuse
        /// live assets. Intended for one-shot captures (e.g. a freeze-frame screenshot). The returned texture is
        /// owned by the caller - dispose it when done. Runs synchronously (submits + waits for the GPU).
        /// </summary>
        /// <param name="width">Target width in pixels (clamped to &gt;= 1).</param>
        /// <param name="height">Target height in pixels (clamped to &gt;= 1).</param>
        /// <param name="clear">Colour the target is cleared to before <paramref name="draw"/> runs.</param>
        /// <param name="draw">Draws the scene into the supplied offscreen batch.</param>
        public Texture2D CaptureToTexture(int width, int height, Color clear, Action<SpriteBatch> draw) =>
            Render2DCore.RenderToTexture(_core.Gd, width, height, clear, draw);

        /// <summary>
        /// As <see cref="CaptureToTexture"/>, but returns a tightly-packed CPU RGBA8 buffer
        /// (<c>width * height * 4</c> bytes, row-major, top-left origin) instead of a GPU texture - for pixels a
        /// game needs on the CPU (e.g. a clipboard image copy). Reuses this surface's live device + assets. Runs
        /// synchronously (submits + waits for the GPU).
        /// </summary>
        public byte[] CaptureToRgba(int width, int height, Color clear, Action<SpriteBatch> draw) =>
            Render2DCore.RenderToRgba(_core.Gd, width, height, clear, draw);

        public void Dispose() => _core.Dispose();
    }
}
