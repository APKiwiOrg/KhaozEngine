using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// The point-light shadow atlas: ONE R32Float colour target of six face columns by <see cref="Rows"/> light
    /// rows, its depth-stencil companion, and the framebuffer the pass renders through. The same shape the cascade
    /// atlas has proven on Metal, Direct3D 11 and Vulkan (design decision 1): a plain 2D target written by a
    /// depth-only pass and sampled as a plain <c>texture2D</c>, so there is no cube map, no array target and no new
    /// GPU API anywhere in the feature.
    /// </summary>
    /// <remarks>
    /// A cell holds LINEAR DISTANCE OVER THE LIGHT RADIUS, cleared to 1.0, which is why the colour format is R32F
    /// rather than a depth format a receiver would have to compare through a sampler (decision 2). The depth
    /// attachment is the pass's own working buffer: it resolves which of two casters covering one texel is nearer,
    /// and nothing ever samples it.
    /// <para>
    /// Allocation can fail (a device out of memory, a layout the driver refuses), and the caller must survive it
    /// with its previous atlas intact, so <see cref="TryCreate"/> answers <c>null</c> instead of throwing and
    /// disposes whatever it had already built. That mirrors <c>ModelRenderer.ReplaceShadowLayout</c>, which catches
    /// the same way around <c>ShadowMapRenderer.BuildReplacement</c>.
    /// </para>
    /// </remarks>
    internal sealed class PointShadowAtlas : IDisposable
    {
        /// <summary>The smallest face a cell may be. Below this the 2x2 receiver tap pattern covers most of the
        /// face and the map stops meaning anything. The player-facing clamps are the settings' (Task 2); this is
        /// the floor the allocation itself will not go under.</summary>
        public const int MinFaceResolution = 16;

        PointShadowAtlas(int faceResolution, int rows, IGpuTexture texture, IGpuTexture depthStencil,
            IGpuFramebuffer framebuffer)
        {
            FaceResolution = faceResolution;
            Rows = rows;
            Texture = texture;
            DepthStencil = depthStencil;
            Framebuffer = framebuffer;
        }

        /// <summary>One cell's size per axis, in texels.</summary>
        public int FaceResolution { get; }

        /// <summary>How many light rows the atlas carries (its slot capacity).</summary>
        public int Rows { get; }

        /// <summary>Atlas width in texels: six face columns.</summary>
        public uint Width => (uint)(FaceResolution * PointShadowMath.FaceCount);

        /// <summary>Atlas height in texels: one row per light slot.</summary>
        public uint Height => (uint)(FaceResolution * Rows);

        /// <summary>Live colour and depth storage in bytes, with six R32F faces and a five-byte depth-stencil face.</summary>
        public long ByteSize => 6L * FaceResolution * Rows * FaceResolution * 9L;

        /// <summary>Whether the first full-atlas clear has been recorded for this atlas.</summary>
        internal bool IsCleared { get; set; }

        /// <summary>The R32F distance atlas the receivers sample.</summary>
        public IGpuTexture Texture { get; }

        /// <summary>The pass's working depth buffer. Never sampled.</summary>
        public IGpuTexture DepthStencil { get; }

        /// <summary>The framebuffer the pass binds (depth plus the one colour attachment).</summary>
        public IGpuFramebuffer Framebuffer { get; }

        /// <summary>Allocate an atlas for <paramref name="faceResolution"/> texels per cell and
        /// <paramref name="rows"/> light rows, or <c>null</c> when the device refuses it. Both arguments are
        /// normalized first, so <see cref="MatchesLayout"/> compares what was actually built.</summary>
        public static PointShadowAtlas? TryCreate(IGpuDevice gd, int faceResolution, int rows)
        {
            ArgumentNullException.ThrowIfNull(gd);
            int res = NormalizeFaceResolution(faceResolution);
            int n = NormalizeRows(rows);
            long width = (long)res * PointShadowMath.FaceCount;
            long height = (long)res * n;
            if (width > PointShadowSettings.MaxAtlasExtent || height > PointShadowSettings.MaxAtlasExtent)
                return null;
            uint w = (uint)width;
            uint h = (uint)height;
            IGpuResourceFactory f = gd.Factory;
            IGpuTexture? texture = null;
            IGpuTexture? depthStencil = null;
            IGpuFramebuffer? framebuffer = null;
            try
            {
                texture = f.CreateTexture(GpuTextureDescription.Texture2D(
                    w, h, GpuPixelFormat.R32Float, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
                depthStencil = f.CreateTexture(GpuTextureDescription.Texture2D(
                    w, h, GpuPixelFormat.D32FloatS8UInt, GpuTextureUsage.DepthStencil));
                framebuffer = f.CreateFramebuffer(depthStencil, texture);
                return new PointShadowAtlas(res, n, texture, depthStencil, framebuffer);
            }
            catch
            {
                // Same shape as ModelRenderer.ReplaceShadowLayout: an allocation failure leaves the caller's
                // previous atlas untouched and rendering, rather than taking the frame down.
                framebuffer?.Dispose();
                depthStencil?.Dispose();
                texture?.Dispose();
                return null;
            }
        }

        /// <summary>Whether this atlas is already the layout a caller is asking for (after the same
        /// normalization <see cref="TryCreate"/> applies), so a reconfigure to the same numbers is a no-op.</summary>
        public bool MatchesLayout(int faceResolution, int rows) =>
            FaceResolution == NormalizeFaceResolution(faceResolution) && Rows == NormalizeRows(rows);

        static int NormalizeFaceResolution(int faceResolution) => Math.Max(MinFaceResolution, faceResolution);

        static int NormalizeRows(int rows) => Math.Max(1, rows);

        public void Dispose()
        {
            Framebuffer.Dispose();
            Texture.Dispose();
            DepthStencil.Dispose();
        }
    }
}
