using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// Owns the low-res GPU targets for one resolution: a 3-attachment MRT (lit color, encoded normal,
    /// linear depth) plus a depth-stencil for the model pass, and two single-target ping-pong buffers for
    /// the post chain. Recreated on resolution change.
    /// </summary>
    internal sealed class RenderResources : IDisposable
    {
        readonly IGpuDevice _gd;

        public int Width { get; private set; }
        public int Height { get; private set; }

        public IGpuTexture ColorTex = null!;
        public IGpuTexture NormalTex = null!;
        public IGpuTexture DepthColorTex = null!;
        public IGpuTexture DepthStencil = null!;
        public IGpuFramebuffer ModelFB = null!;
        public IGpuFramebuffer ColorOnlyFB = null!;

        public IGpuTexture PingA = null!, PingB = null!;
        public IGpuFramebuffer PingAFB = null!, PingBFB = null!;

        public RenderResources(IGpuDevice gd, int w, int h)
        {
            _gd = gd;
            Create(w, h);
        }

        public void Resize(int w, int h)
        {
            if (w == Width && h == Height) return;
            DisposeTargets();
            Create(w, h);
        }

        IGpuTexture Tex(uint w, uint h, GpuPixelFormat fmt, GpuTextureUsage usage) =>
            _gd.Factory.CreateTexture(GpuTextureDescription.Texture2D(w, h, fmt, usage));

        void Create(int w, int h)
        {
            Width = w; Height = h;
            uint uw = (uint)w, uh = (uint)h;
            var rt = GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled;

            ColorTex = Tex(uw, uh, GpuPixelFormat.R8G8B8A8UNorm, rt);
            NormalTex = Tex(uw, uh, GpuPixelFormat.R8G8B8A8UNorm, rt);
            DepthColorTex = Tex(uw, uh, GpuPixelFormat.R32Float, rt);
            DepthStencil = Tex(uw, uh, GpuPixelFormat.D32FloatS8UInt, GpuTextureUsage.DepthStencil);
            ModelFB = _gd.Factory.CreateFramebuffer(DepthStencil, ColorTex, NormalTex, DepthColorTex);

            // Single-target view over the lit color attachment (no depth), so a pass can blend into ColorTex while
            // sampling DepthColorTex (a different texture) - used by the ground-decal pass before the post chain.
            ColorOnlyFB = _gd.Factory.CreateFramebuffer(null, ColorTex);

            PingA = Tex(uw, uh, GpuPixelFormat.R8G8B8A8UNorm, rt);
            PingB = Tex(uw, uh, GpuPixelFormat.R8G8B8A8UNorm, rt);
            PingAFB = _gd.Factory.CreateFramebuffer(null, PingA);
            PingBFB = _gd.Factory.CreateFramebuffer(null, PingB);
        }

        void DisposeTargets()
        {
            ModelFB?.Dispose(); ColorOnlyFB?.Dispose(); PingAFB?.Dispose(); PingBFB?.Dispose();
            ColorTex?.Dispose(); NormalTex?.Dispose(); DepthColorTex?.Dispose(); DepthStencil?.Dispose();
            PingA?.Dispose(); PingB?.Dispose();
        }

        public void Dispose() => DisposeTargets();
    }
}
