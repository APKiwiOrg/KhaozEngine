using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// Owns the low-res GPU targets for one resolution: a 3-attachment MRT (lit color, encoded normal,
    /// linear depth) plus a depth-stencil for the model pass, and two single-target ping-pong buffers for
    /// the post chain. Recreated on resolution / mip-mode / MSAA-sample-count / bloom-enabled / HDR-format change.
    /// Also owns an optional half-resolution ping-pong pair (<see cref="BloomA"/>/<see cref="BloomB"/>) for the
    /// bloom bright-pass + separable blur, allocated only while bloom is enabled (see <see cref="BloomAllocated"/>).
    /// The colour-carrying targets (lit colour + both ping-pong pairs) render at <c>R16G16B16A16Float</c> when
    /// <see cref="HdrColor"/> is set (over-range headroom for the tonemap) or <c>R8G8B8A8UNorm</c> otherwise (the
    /// legacy path). The encoded-normal (UNorm) and linear-depth (R32Float) attachments are format-fixed either way.
    /// </summary>
    /// <remarks>
    /// Under MSAA (<see cref="SampleCount"/> &gt; 1) the model pass renders into a MULTISAMPLED MRT
    /// (<see cref="MsColor"/> / <see cref="MsNormal"/> / <see cref="MsDepthColor"/> + a multisampled
    /// <see cref="DepthStencil"/>); those are resolved (<see cref="IGpuCommandList.ResolveTexture"/>) into the
    /// single-sample <see cref="ColorTex"/> / <see cref="NormalTex"/> / <see cref="DepthColorTex"/> the post chain
    /// samples. Single-sample (the default) leaves <c>Ms*</c> null and <see cref="ColorTex"/> etc. ARE the MRT
    /// attachments - byte-identical to the pre-MSAA path.
    /// </remarks>
    internal sealed class RenderResources : IDisposable
    {
        readonly IGpuDevice _gd;

        public int Width { get; private set; }
        public int Height { get; private set; }

        /// <summary>Whether the colour-carrying targets (<see cref="ColorTex"/> / <see cref="MsColor"/> /
        /// <see cref="PingA"/> / <see cref="PingB"/> / <see cref="BloomA"/> / <see cref="BloomB"/>) render at
        /// <c>R16G16B16A16Float</c> (HDR headroom above 1.0 for the tonemap) rather than the legacy
        /// <c>R8G8B8A8UNorm</c>. Mirrors the <c>hdrColor</c> argument of the last <see cref="Create"/> /
        /// <see cref="Resize"/> call. The normal (UNorm) and linear-depth (R32Float) attachments are unaffected.</summary>
        public bool HdrColor { get; private set; }

        /// <summary>Whether the three blit-source colour targets (<see cref="ColorTex"/> / <see cref="PingA"/> /
        /// <see cref="PingB"/>) carry a full mip chain + <see cref="GpuTextureUsage.GenerateMipmaps"/>, so the final
        /// downscale blit can trilinear-filter a supersampled target correctly at ANY factor (see
        /// <see cref="Scene3D.WantsMipDownsample"/>). False = single-mip (the historical path): a 1:1 / upscale blit
        /// never needs mips, and a mip chain left ungenerated would feed the trilinear sampler undefined levels, so
        /// only the genuine MatchViewport-downscale case allocates them. The MRT's normal/linear-depth/depth-stencil
        /// attachments stay single-mip regardless (sampled only 1:1 by the edge pass).</summary>
        public bool Mipped { get; private set; }

        /// <summary>MSAA sample count of the model-pass MRT (1 = single-sample, the default). &gt; 1 makes
        /// <see cref="ModelFB"/>/<see cref="ColorDepthFB"/> multisampled; resolve to the single-sample post targets
        /// before the post chain.</summary>
        public int SampleCount { get; private set; }
        /// <summary>Whether the MRT is multisampled (<see cref="SampleCount"/> &gt; 1) and therefore needs a resolve.</summary>
        public bool Msaa => SampleCount > 1;

        // Single-sample targets the POST chain samples (also the MRT attachments when SampleCount == 1).
        public IGpuTexture ColorTex = null!;
        public IGpuTexture NormalTex = null!;
        public IGpuTexture DepthColorTex = null!;
        public IGpuTexture DepthStencil = null!;   // multisampled under MSAA (never sampled, only depth-tested)
        public IGpuFramebuffer ModelFB = null!;
        public IGpuFramebuffer ColorDepthFB = null!;

        // Multisampled MRT attachments (only allocated when Msaa; resolved into the single-sample targets above).
        public IGpuTexture? MsColor;
        public IGpuTexture? MsNormal;
        public IGpuTexture? MsDepthColor;

        public IGpuTexture PingA = null!, PingB = null!;
        public IGpuFramebuffer PingAFB = null!, PingBFB = null!;

        /// <summary>Half-resolution ping-pong pair for the bloom bright-pass + separable blur (see
        /// <see cref="Internal.BloomMath.HalfResSize"/>). Allocated ONLY when <see cref="BloomAllocated"/> is
        /// requested (<see cref="BloomSettings.Enabled"/> at the time of the last <see cref="Resize"/>), so bloom
        /// off costs zero extra GPU memory - the historical, pre-bloom footprint. Recreated alongside the main
        /// targets on any resize while bloom is enabled; freed the next resize after it is disabled.</summary>
        public IGpuTexture? BloomA;
        public IGpuTexture? BloomB;
        public IGpuFramebuffer? BloomAFB;
        public IGpuFramebuffer? BloomBFB;
        public int BloomWidth { get; private set; }
        public int BloomHeight { get; private set; }

        /// <summary>Whether the bloom half-res targets are currently allocated (mirrors the <c>bloomEnabled</c>
        /// argument passed to the last <see cref="Create"/>/<see cref="Resize"/> call).</summary>
        public bool BloomAllocated { get; private set; }

        public RenderResources(IGpuDevice gd, int w, int h, bool hdrColor)
        {
            _gd = gd;
            Create(w, h, mipped: false, sampleCount: 1, bloomEnabled: false, hdrColor: hdrColor);
        }

        public void Resize(int w, int h, bool mipped, int sampleCount, bool bloomEnabled, bool hdrColor)
        {
            if (w == Width && h == Height && mipped == Mipped && sampleCount == SampleCount
                && bloomEnabled == BloomAllocated && hdrColor == HdrColor) return;
            DisposeTargets();
            Create(w, h, mipped, sampleCount, bloomEnabled, hdrColor);
        }

        IGpuTexture Tex(uint w, uint h, GpuPixelFormat fmt, GpuTextureUsage usage, uint mipLevels = 1, uint samples = 1) =>
            _gd.Factory.CreateTexture(new GpuTextureDescription(w, h, fmt, usage, mipLevels, 1, samples));

        void Create(int w, int h, bool mipped, int sampleCount, bool bloomEnabled, bool hdrColor)
        {
            Width = w; Height = h; Mipped = mipped; SampleCount = sampleCount < 1 ? 1 : sampleCount;
            HdrColor = hdrColor;
            uint uw = (uint)w, uh = (uint)h, s = (uint)SampleCount;
            var rt = GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled;
            // The colour-carrying targets go float16 in HDR mode for over-range headroom; the encoded-normal (UNorm)
            // and linear-depth (R32Float) attachments are format-fixed regardless.
            var colorFmt = hdrColor ? GpuPixelFormat.R16G16B16A16Float : GpuPixelFormat.R8G8B8A8UNorm;

            // The blit-source colour targets get a full mip chain (+ GenerateMipmaps) only when the final blit is a
            // genuine downscale (MatchViewport supersampling); the trilinear sampler then picks LOD ~= log2(ratio) so
            // 3:1 / 4:1 stop under-sampling. mip 0 is the render target; the chain is regenerated per frame before the
            // blit. The MRT's other attachments are only ever sampled 1:1 by the edge pass, so they stay single-mip.
            uint blitMips = mipped ? SplatMaterialConfig.MipLevelCount(w, h) : 1u;
            var blitRt = mipped ? rt | GpuTextureUsage.GenerateMipmaps : rt;

            // Single-sample targets the post chain samples. Under MSAA these are the RESOLVE destinations; otherwise
            // they ARE the MRT attachments (the historical path).
            ColorTex = Tex(uw, uh, colorFmt, blitRt, blitMips);
            NormalTex = Tex(uw, uh, GpuPixelFormat.R8G8B8A8UNorm, rt);
            DepthColorTex = Tex(uw, uh, GpuPixelFormat.R32Float, rt);

            if (Msaa)
            {
                // Multisampled MRT (render target only; a multisampled texture cannot be sampled directly). The
                // depth-stencil is multisampled too; it is only depth-tested (never sampled), so it needs no resolve.
                var rtOnly = GpuTextureUsage.RenderTarget;
                MsColor = Tex(uw, uh, colorFmt, rtOnly, 1, s);
                MsNormal = Tex(uw, uh, GpuPixelFormat.R8G8B8A8UNorm, rtOnly, 1, s);
                MsDepthColor = Tex(uw, uh, GpuPixelFormat.R32Float, rtOnly, 1, s);
                DepthStencil = Tex(uw, uh, GpuPixelFormat.D32FloatS8UInt, GpuTextureUsage.DepthStencil, 1, s);
                ModelFB = _gd.Factory.CreateFramebuffer(DepthStencil, MsColor, MsNormal, MsDepthColor);
                ColorDepthFB = _gd.Factory.CreateFramebuffer(DepthStencil, MsColor);
            }
            else
            {
                DepthStencil = Tex(uw, uh, GpuPixelFormat.D32FloatS8UInt, GpuTextureUsage.DepthStencil);
                ModelFB = _gd.Factory.CreateFramebuffer(DepthStencil, ColorTex, NormalTex, DepthColorTex);
                // Lit color attachment + the scene depth-stencil, so the ground-decal pass can blend into ColorTex AND
                // use a read-only hardware depth test to reject no-geometry background pixels (cleared to the far
                // plane), while sampling the separate linear-depth texture (DepthColorTex) for world reconstruction.
                ColorDepthFB = _gd.Factory.CreateFramebuffer(DepthStencil, ColorTex);
            }

            PingA = Tex(uw, uh, colorFmt, blitRt, blitMips);
            PingB = Tex(uw, uh, colorFmt, blitRt, blitMips);
            PingAFB = _gd.Factory.CreateFramebuffer(null, PingA);
            PingBFB = _gd.Factory.CreateFramebuffer(null, PingB);

            BloomAllocated = bloomEnabled;
            if (bloomEnabled)
            {
                var (bw, bh) = BloomMath.HalfResSize(w, h);
                BloomWidth = bw; BloomHeight = bh;
                uint ubw = (uint)bw, ubh = (uint)bh;
                BloomA = Tex(ubw, ubh, colorFmt, rt);
                BloomB = Tex(ubw, ubh, colorFmt, rt);
                BloomAFB = _gd.Factory.CreateFramebuffer(null, BloomA);
                BloomBFB = _gd.Factory.CreateFramebuffer(null, BloomB);
            }
            else
            {
                BloomWidth = 0; BloomHeight = 0;
            }
        }

        /// <summary>Resolve the multisampled MRT's linear-depth into the single-sample <see cref="DepthColorTex"/> the
        /// ground-decal pass + post edge pass sample. Call after the geometry passes, before the decal pass. No-op
        /// when not <see cref="Msaa"/>.</summary>
        public void ResolveDepth(IGpuCommandList cl)
        {
            if (Msaa) cl.ResolveTexture(MsDepthColor!, DepthColorTex);
        }

        /// <summary>Resolve the multisampled lit colour + encoded normal into the single-sample targets the post
        /// chain samples. Call after all MRT writers (geometry + decals), before the post chain. No-op when not
        /// <see cref="Msaa"/>.</summary>
        public void ResolveColorNormal(IGpuCommandList cl)
        {
            if (!Msaa) return;
            cl.ResolveTexture(MsColor!, ColorTex);
            cl.ResolveTexture(MsNormal!, NormalTex);
        }

        void DisposeTargets()
        {
            ModelFB?.Dispose(); ColorDepthFB?.Dispose(); PingAFB?.Dispose(); PingBFB?.Dispose();
            ColorTex?.Dispose(); NormalTex?.Dispose(); DepthColorTex?.Dispose(); DepthStencil?.Dispose();
            MsColor?.Dispose(); MsNormal?.Dispose(); MsDepthColor?.Dispose();
            MsColor = MsNormal = MsDepthColor = null;
            PingA?.Dispose(); PingB?.Dispose();
            BloomAFB?.Dispose(); BloomBFB?.Dispose(); BloomA?.Dispose(); BloomB?.Dispose();
            BloomAFB = BloomBFB = null; BloomA = BloomB = null;
        }

        public void Dispose() => DisposeTargets();
    }
}
