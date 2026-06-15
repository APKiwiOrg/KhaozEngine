using System;
using Veldrid;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// Owns the low-res GPU targets for one resolution: a 3-attachment MRT (lit color, encoded normal,
    /// linear depth) plus a depth-stencil for the model pass, and two single-target ping-pong buffers for
    /// the post chain. Recreated on resolution change.
    /// </summary>
    internal sealed class RenderResources : IDisposable
    {
        readonly GraphicsDevice _gd;

        public int Width { get; private set; }
        public int Height { get; private set; }

        public Texture ColorTex = null!;
        public Texture NormalTex = null!;
        public Texture DepthColorTex = null!;
        public Texture DepthStencil = null!;
        public Framebuffer ModelFB = null!;

        public Texture PingA = null!, PingB = null!;
        public Framebuffer PingAFB = null!, PingBFB = null!;

        public RenderResources(GraphicsDevice gd, int w, int h)
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

        Texture Tex(uint w, uint h, PixelFormat fmt, TextureUsage usage) =>
            _gd.ResourceFactory.CreateTexture(TextureDescription.Texture2D(w, h, 1, 1, fmt, usage));

        void Create(int w, int h)
        {
            Width = w; Height = h;
            uint uw = (uint)w, uh = (uint)h;
            var rt = TextureUsage.RenderTarget | TextureUsage.Sampled;

            ColorTex = Tex(uw, uh, PixelFormat.R8_G8_B8_A8_UNorm, rt);
            NormalTex = Tex(uw, uh, PixelFormat.R8_G8_B8_A8_UNorm, rt);
            DepthColorTex = Tex(uw, uh, PixelFormat.R32_Float, rt);
            DepthStencil = Tex(uw, uh, PixelFormat.D32_Float_S8_UInt, TextureUsage.DepthStencil);
            ModelFB = _gd.ResourceFactory.CreateFramebuffer(
                new FramebufferDescription(DepthStencil, ColorTex, NormalTex, DepthColorTex));

            PingA = Tex(uw, uh, PixelFormat.R8_G8_B8_A8_UNorm, rt);
            PingB = Tex(uw, uh, PixelFormat.R8_G8_B8_A8_UNorm, rt);
            PingAFB = _gd.ResourceFactory.CreateFramebuffer(new FramebufferDescription(null, PingA));
            PingBFB = _gd.ResourceFactory.CreateFramebuffer(new FramebufferDescription(null, PingB));
        }

        void DisposeTargets()
        {
            ModelFB?.Dispose(); PingAFB?.Dispose(); PingBFB?.Dispose();
            ColorTex?.Dispose(); NormalTex?.Dispose(); DepthColorTex?.Dispose(); DepthStencil?.Dispose();
            PingA?.Dispose(); PingB?.Dispose();
        }

        public void Dispose() => DisposeTargets();
    }
}
