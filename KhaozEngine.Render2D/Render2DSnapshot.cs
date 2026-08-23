using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using KhaozEngine.Render2D.Internal;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// Draw surface handed to a <see cref="Render2DSnapshot"/> callback (backend-free).
    ///
    /// <para><b>EVERY GPU RESOURCE THIS CONTEXT HANDS OUT IS OWNED BY THE CAPTURE, WHICH FREES IT.</b> The
    /// callback creates and forgets. It still must not dispose what it made, because the recorded command list
    /// names that resource until the submit that happens after the callback returns, and it no longer leaves the
    /// resource to the per-capture device teardown either. That older contract read as harmless because a
    /// Veldrid backend reclaims a survivor silently, and it is a spec violation on the native Vulkan backend,
    /// which reports each survivor as a <c>VUID-vkDestroyDevice-device-05137</c> object leak
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/618).</para>
    ///
    /// <para>Going through this context is what makes a resource covered, and every offscreen builder the
    /// callback can reach does exactly that: <see cref="PrimitiveRenderer"/>'s owned white pixel,
    /// <c>IconAtlas.Bake</c>'s atlas and <c>VfxRenderer</c>'s three baked textures are all
    /// <see cref="CreateTexture"/> calls underneath. Something built straight off the device behind
    /// <see cref="Batch"/> stays the caller's own, as it always was.</para>
    /// </summary>
    public sealed class Render2DContext
    {
        readonly Render2DCore _core;

        // Freed newest first, so a resource is never released before something built on top of it. Nothing here
        // outlives the capture in any case: the per-capture device goes with it.
        readonly List<IDisposable> _owned = new();

        public int Width { get; }
        public int Height { get; }
        internal Render2DContext(Render2DCore core, int w, int h) { _core = core; Width = w; Height = h; }

        public SpriteBatch Batch => _core.Batch;
        public Texture2D LoadTexture(string pngPath) => Own(_core.LoadTexture(pngPath));
        public Texture2D CreateTexture(byte[] rgba, int width, int height) => Own(_core.CreateTexture(rgba, width, height));
        public SpriteFont LoadFont(string ttfPath, float pixelHeight, int oversample = 1) => Own(_core.LoadFont(ttfPath, pixelHeight, oversample));
        /// <summary>Bake a <see cref="SpriteFont"/> from raw TTF bytes (no filesystem path) - the cross-platform core path.</summary>
        public SpriteFont LoadFont(byte[] ttf, float pixelHeight, int oversample = 1) => Own(_core.LoadFont(ttf, pixelHeight, oversample));
        /// <summary>Bake a <see cref="SpriteFont"/> from a <see cref="FontManager"/> key (resolved to bytes, then baked).</summary>
        public SpriteFont LoadFont(FontManager fonts, string key, float pixelHeight, int oversample = 1) => Own(_core.LoadFont(fonts.GetFontBytes(key), pixelHeight, oversample));
        /// <summary>Bake a <see cref="SpriteFont"/> from the engine's embedded default face (<see cref="DefaultFont"/>); no system font, no path.</summary>
        public SpriteFont LoadDefaultFont(float pixelHeight, int oversample = 1) => Own(_core.LoadDefaultFont(pixelHeight, oversample));

        /// <summary>A <see cref="DpiFont"/> baking at the live DPI scale (call <c>For(dpiScale)</c>); the offscreen
        /// analogue used to verify point-space UI crispness headlessly. <paramref name="cacheSlots"/> &gt; 1 keeps
        /// several scales baked at once (a face drawn at several scales in one pass).</summary>
        public DpiFont LoadDpiFont(byte[] ttf, float pixelHeight, int cacheSlots = 1) => Own(_core.CreateDpiFont(ttf, pixelHeight, cacheSlots));

        /// <summary>A <see cref="DpiFont"/> from the engine's embedded default face. See <see cref="LoadDpiFont(byte[], float, int)"/>.</summary>
        public DpiFont LoadDefaultDpiFont(float pixelHeight, int cacheSlots = 1) => Own(_core.CreateDefaultDpiFont(pixelHeight, cacheSlots));

        /// <summary>How many resources this context handed out and still owes a dispose. Internal, for the test
        /// that pins the tracking as a counted fact rather than a claim.</summary>
        internal int OwnedCount => _owned.Count;

        /// <summary>Free everything the callback created through this context. Called by
        /// <see cref="Render2DSnapshot.Capture"/> once the submit has drained and before the per-capture device
        /// goes. Idempotent: a second call has nothing left to free.</summary>
        internal void DisposeOwned()
        {
            for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
            _owned.Clear();
        }

        T Own<T>(T resource) where T : IDisposable
        {
            _owned.Add(resource);
            return resource;
        }
    }

    /// <summary>Headless offscreen 2D render to a CPU RGBA buffer (no window). For tooling/tests; needs a Metal GPU.</summary>
    public static class Render2DSnapshot
    {
        /// <summary>
        /// Renders one offscreen frame via <paramref name="draw"/> and reads it back as RGBA bytes.
        /// LIFETIME CONTRACT: the callback runs mid-command-recording, so a GPU resource it creates (a texture,
        /// a font, a <c>VfxRenderer</c>) must NOT be disposed inside the callback - the recorded command list still
        /// references it until the submit that happens after the callback returns. Veldrid's Vulkan backend rejects
        /// a submit referencing a disposed resource (other backends tolerate it, so the bug hides off Vulkan).
        /// THE LATER DISPOSE POINT IS HERE: every resource the callback created through its
        /// <see cref="Render2DContext"/> is freed below, after the submit has drained and before the per-capture
        /// device goes. Leaving them to the device teardown instead was the old contract, and the native Vulkan
        /// backend reports each survivor as a <c>VUID-vkDestroyDevice-device-05137</c> object leak
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/618).
        /// </summary>
        public static byte[] Capture(int width, int height, Color clear, Action<Render2DContext> draw)
        {
            // NOTE: the context is intentionally NOT disposed here, as in the original inline CreateMetal path,
            // tearing down the Metal device after this 2D font/texture pass crashes in the backend. The device
            // is left to process teardown (this is a tooling/test-only snapshot helper). Matches baseline behaviour.
            // The headless half of the 18.0.0 registration. AppWindow does this for a windowed game and there is
            // no AppWindow here, so a snapshot tool would otherwise have to know that KhaozEngine.Gpu builds no
            // device of its own any more. Registers the kind the selector resolves to
            // (KE_GRAPHICS_BACKEND decides it here, since a headless host stores no player preference) plus
            // this platform's own as the fallback target, and only where nothing is registered already, so a
            // harness that seated its own provider (the GPU test suite does) keeps it.
            GpuBackends.RegisterResolvedIfUnregistered();
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
            var context = new Render2DContext(core, width, height);
            try
            {
                using (GpuRecording.Open(gd, cl, "Render2DSnapshot.Capture"))
                {
                    cl.SetFramebuffer(fb);
                    cl.ClearColorTarget(0, clear);
                    core.Batch.NewFrame(cl, width, height);
                    draw(context);
                }
                gd.Submit(cl);
                gd.WaitForIdle();
                return GpuReadback.ToRgba(gd, target, width, height);
            }
            // The callback's resources go with the capture's own, and before the device that owns them all. On
            // the throwing path the submit may never have happened, which is why each Texture2D drains the device
            // for itself rather than trusting the WaitForIdle above.
            finally { cl.Dispose(); fb.Dispose(); target.Dispose(); context.DisposeOwned(); core.Dispose(); }
        }

        /// <summary>
        /// As <see cref="Capture"/>, but encodes the result to a PNG (via the dependency-free <see cref="Png"/>
        /// encoder) and writes it to <paramref name="path"/>. Returns the raw RGBA buffer too (for assertions /
        /// further processing). The one-call path a game's snapshot tool needs.
        /// </summary>
        public static byte[] CaptureToPng(string path, int width, int height, Color clear, Action<Render2DContext> draw)
        {
            byte[] rgba = Capture(width, height, clear, draw);
            Png.Write(path, rgba, width, height);
            return rgba;
        }

    }
}
