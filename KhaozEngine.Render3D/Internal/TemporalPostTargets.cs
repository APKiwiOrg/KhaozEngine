using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// THE DISPLAY-RESOLUTION POST CHAIN OF A TEMPORAL FRAME (TEMPORAL-RESOLVE-UPSCALING-DESIGN section 1), and the
    /// internal-resolution opaque-only copy the resolve's reactive estimate reads. Everything after the resolve runs at
    /// the display size, so this owns a display-size ping pair and, while bloom is on, a half-display-size bloom pair, in
    /// the scene's colour format. The chain's source is the history colour the resolve wrote this frame, which alternates
    /// between <see cref="TemporalHistory"/>'s two targets, which is why it reports two source slots.
    /// <para>The edge outline reads the internal normal and depth attachments, and the distortion apply reads the
    /// internal offset field, both from <see cref="RenderResources"/>, at normalised coordinates.</para>
    /// <para>Created on the first frame the resolve runs and released on the first frame it does not, so a scene that
    /// never selects temporal anti-aliasing allocates nothing here.</para>
    /// </summary>
    internal sealed class TemporalPostTargets : IPostChainTargets, IDisposable
    {
        readonly IGpuDevice _gd;
        RenderResources? _res;
        TemporalHistory? _history;
        IGpuTexture? _pingA, _pingB, _bloomA, _bloomB, _opaque;
        IGpuFramebuffer? _pingAFB, _pingBFB, _bloomAFB, _bloomBFB;
        GpuPixelFormat _colorFormat;
        int _resGeneration = int.MinValue, _historyGeneration = int.MinValue;

        public TemporalPostTargets(IGpuDevice gd) => _gd = gd;

        /// <summary>The display size of the ping pair. Zero while released.</summary>
        public int Width { get; private set; }
        public int Height { get; private set; }
        /// <summary>Bumped when a texture this reports is replaced here or upstream (the scene's targets, the history's).</summary>
        public int Generation { get; private set; }
        public bool BloomAllocated { get; private set; }
        public int BloomWidth { get; private set; }
        public int BloomHeight { get; private set; }

        /// <summary>Whether the display targets exist.</summary>
        public bool Allocated => _pingA is not null;

        /// <summary>The lit colour after the opaque passes and the background, before any transparent pass, at the
        /// internal size in the scene's colour format.</summary>
        public IGpuTexture OpaqueColor => _opaque ?? throw NotAllocated();

        /// <summary>Size the display targets for <paramref name="displayWidth"/> by <paramref name="displayHeight"/> and the
        /// opaque copy for <paramref name="res"/>, in its colour format, keeping whatever already matches. Drains the device
        /// before replacing a texture the last frame may still read.</summary>
        public void Ensure(RenderResources res, TemporalHistory history, int displayWidth, int displayHeight, bool bloomEnabled)
        {
            int w = Math.Max(1, displayWidth), h = Math.Max(1, displayHeight);
            GpuPixelFormat format = res.HdrColor ? GpuPixelFormat.R16G16B16A16Float : GpuPixelFormat.R8G8B8A8UNorm;
            bool display = _pingA is null || w != Width || h != Height || format != _colorFormat
                || bloomEnabled != BloomAllocated;
            bool opaque = _opaque is null || _opaque.Width != (uint)res.Width || _opaque.Height != (uint)res.Height
                || _opaque.Format != format;
            if (display || opaque) _gd.WaitForIdle();
            if (display) CreateDisplay(w, h, format, bloomEnabled);
            if (opaque) CreateOpaque((uint)res.Width, (uint)res.Height, format);
            bool upstream = !ReferenceEquals(res, _res) || !ReferenceEquals(history, _history)
                || res.Generation != _resGeneration || history.TargetGeneration != _historyGeneration;
            if (display || opaque || upstream) Generation++;
            _res = res;
            _history = history;
            _resGeneration = res.Generation;
            _historyGeneration = history.TargetGeneration;
        }

        /// <summary>Copy the lit colour's mip 0 into <see cref="OpaqueColor"/>. The scene calls it after the opaque passes
        /// and the background, before the transparent model-pass writers.</summary>
        public void CopyOpaque(IGpuCommandList cl)
        {
            RenderResources res = _res ?? throw NotAllocated();
            cl.CopyTextureSubresource(res.ColorTex, 0, 0, OpaqueColor, (uint)res.Width, (uint)res.Height);
        }

        /// <summary>Free every target. Drains the device first. Safe to call when nothing is allocated.</summary>
        public void Release()
        {
            if (_pingA is null && _opaque is null) return;
            _gd.WaitForIdle();
            DisposeDisplay();
            _opaque?.Dispose();
            _opaque = null;
            _res = null;
            _history = null;
            _resGeneration = _historyGeneration = int.MinValue;
            Generation++;
        }

        public void Dispose() => Release();

        void CreateDisplay(int w, int h, GpuPixelFormat format, bool bloomEnabled)
        {
            DisposeDisplay();
            IGpuResourceFactory f = _gd.Factory;
            const GpuTextureUsage usage = GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled;
            _pingA = f.CreateTexture(GpuTextureDescription.Texture2D((uint)w, (uint)h, format, usage));
            _pingB = f.CreateTexture(GpuTextureDescription.Texture2D((uint)w, (uint)h, format, usage));
            _pingAFB = f.CreateFramebuffer(null, _pingA);
            _pingBFB = f.CreateFramebuffer(null, _pingB);
            if (bloomEnabled)
            {
                var (bw, bh) = BloomMath.HalfResSize(w, h);
                _bloomA = f.CreateTexture(GpuTextureDescription.Texture2D((uint)bw, (uint)bh, format, usage));
                _bloomB = f.CreateTexture(GpuTextureDescription.Texture2D((uint)bw, (uint)bh, format, usage));
                _bloomAFB = f.CreateFramebuffer(null, _bloomA);
                _bloomBFB = f.CreateFramebuffer(null, _bloomB);
                BloomWidth = bw;
                BloomHeight = bh;
            }
            Width = w;
            Height = h;
            BloomAllocated = bloomEnabled;
            _colorFormat = format;
        }

        void CreateOpaque(uint w, uint h, GpuPixelFormat format)
        {
            _opaque?.Dispose();
            _opaque = _gd.Factory.CreateTexture(GpuTextureDescription.Texture2D(w, h, format,
                GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        }

        void DisposeDisplay()
        {
            _pingAFB?.Dispose(); _pingBFB?.Dispose(); _bloomAFB?.Dispose(); _bloomBFB?.Dispose();
            _pingA?.Dispose(); _pingB?.Dispose(); _bloomA?.Dispose(); _bloomB?.Dispose();
            _pingAFB = _pingBFB = _bloomAFB = _bloomBFB = null;
            _pingA = _pingB = _bloomA = _bloomB = null;
            Width = Height = BloomWidth = BloomHeight = 0;
            BloomAllocated = false;
        }

        static InvalidOperationException NotAllocated() => new(
            "The temporal post targets are not allocated. They exist only while the temporal resolve runs, after "
            + "TemporalPostTargets.Ensure.");

        // IPostChainTargets: the display-resolution chain after the resolve.
        public int SourceSlotCount => 2;
        public int SourceSlot => (_history ?? throw NotAllocated()).WriteIndex;
        public IGpuTexture Source(int slot) => (_history ?? throw NotAllocated()).Color(slot);
        public IGpuTexture NormalTex => (_res ?? throw NotAllocated()).NormalTex;
        public IGpuTexture DepthColorTex => (_res ?? throw NotAllocated()).DepthColorTex;
        public IGpuTexture PingA => _pingA ?? throw NotAllocated();
        public IGpuTexture PingB => _pingB ?? throw NotAllocated();
        public IGpuFramebuffer PingAFB => _pingAFB ?? throw NotAllocated();
        public IGpuFramebuffer PingBFB => _pingBFB ?? throw NotAllocated();
        public IGpuTexture? BloomA => _bloomA;
        public IGpuTexture? BloomB => _bloomB;
        public IGpuFramebuffer? BloomAFB => _bloomAFB;
        public IGpuFramebuffer? BloomBFB => _bloomBFB;
        public bool DistortAllocated => _res?.DistortAllocated ?? false;
        public IGpuTexture? DistortTex => _res?.DistortTex;
    }
}
