using System;

namespace KhaozEngine.Render2D.Vfx
{
    /// <summary>
    /// CPU-baked VFX textures - no shipped asset. <see cref="BakeGlowPixels"/>, <see cref="BakeRingPixels"/> and
    /// <see cref="BakeArcBandPixels"/> produce tightly-packed RGBA8 buffers (row-major, top-left origin) usable
    /// headlessly. The <c>BakeGlow</c>/<c>BakeRing</c>/<c>BakeArcBand</c>/<c>White</c> overloads upload one to a
    /// sampleable <see cref="Texture2D"/> on a live <see cref="Render2DSurface"/> or a snapshot
    /// <see cref="Render2DContext"/>. All of them are white RGB with the shape carried in alpha, so an additive
    /// draw of the glow reads as a soft dot (sprite halos, beam flares, bloom) and an alpha-blended draw of the
    /// arc band reads as a smooth HUD arc.
    /// </summary>
    public static partial class VfxTextures
    {
        /// <summary>
        /// Bakes a square radial-glow RGBA8 buffer of <paramref name="size"/>x<paramref name="size"/> pixels:
        /// white RGB with alpha = <c>saturate(1 - d)^<paramref name="falloff"/></c>, where <c>d</c> is the
        /// distance from the centre normalised so the edge mid-point is 1. Higher <paramref name="falloff"/>
        /// tightens the core. Pure / headless. <paramref name="size"/> is clamped to at least 2.
        /// </summary>
        public static byte[] BakeGlowPixels(int size, float falloff = 2f)
        {
            size = Math.Max(2, size);
            if (falloff <= 0f) falloff = 1f;
            var px = new byte[size * size * 4];
            float centre = (size - 1) * 0.5f;
            float maxR = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - centre) / maxR;
                    float dy = (y - centre) / maxR;
                    float d = MathF.Sqrt(dx * dx + dy * dy);
                    float a = MathF.Pow(Math.Clamp(1f - d, 0f, 1f), falloff);
                    int i = (y * size + x) * 4;
                    px[i] = 255; px[i + 1] = 255; px[i + 2] = 255;
                    px[i + 3] = (byte)Math.Clamp((int)(a * 255f + 0.5f), 0, 255);
                }
            }
            return px;
        }

        /// <summary>
        /// Bakes a square hollow-ring RGBA8 buffer of <paramref name="size"/>x<paramref name="size"/> pixels:
        /// white RGB, opaque on a band centred at <paramref name="innerRadius01"/> + half
        /// <paramref name="thickness01"/> (both as fractions of the half-extent) and feathering to zero at the
        /// band edges. For one-shot impact/flash rings. Pure / headless.
        /// </summary>
        public static byte[] BakeRingPixels(int size, float innerRadius01 = 0.55f, float thickness01 = 0.25f)
        {
            size = Math.Max(2, size);
            innerRadius01 = Math.Clamp(innerRadius01, 0f, 1f);
            thickness01 = Math.Max(0.01f, thickness01);
            var px = new byte[size * size * 4];
            float centre = (size - 1) * 0.5f;
            float maxR = size * 0.5f;
            float mid = innerRadius01 + thickness01 * 0.5f;
            float half = thickness01 * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - centre) / maxR;
                    float dy = (y - centre) / maxR;
                    float d = MathF.Sqrt(dx * dx + dy * dy);
                    // Triangular feather: 1 at the band centre, 0 at +/- half the thickness.
                    float a = Math.Clamp(1f - MathF.Abs(d - mid) / half, 0f, 1f);
                    int i = (y * size + x) * 4;
                    px[i] = 255; px[i + 1] = 255; px[i + 2] = 255;
                    px[i + 3] = (byte)Math.Clamp((int)(a * 255f + 0.5f), 0, 255);
                }
            }
            return px;
        }

        /// <summary>Bakes a radial glow and uploads it to a sampleable texture on <paramref name="surface"/>'s device.</summary>
        public static Texture2D BakeGlow(Render2DSurface surface, int size = 64, float falloff = 2f)
        {
            ArgumentNullException.ThrowIfNull(surface);
            size = Math.Max(2, size);
            return surface.CreateTexture(BakeGlowPixels(size, falloff), size, size);
        }

        /// <summary>Bakes a radial glow and uploads it to a sampleable texture on the snapshot <paramref name="context"/>'s device.</summary>
        public static Texture2D BakeGlow(Render2DContext context, int size = 64, float falloff = 2f)
        {
            ArgumentNullException.ThrowIfNull(context);
            size = Math.Max(2, size);
            return context.CreateTexture(BakeGlowPixels(size, falloff), size, size);
        }

        /// <summary>Bakes a hollow ring and uploads it to a sampleable texture on <paramref name="surface"/>'s device.</summary>
        public static Texture2D BakeRing(Render2DSurface surface, int size = 64, float innerRadius01 = 0.55f, float thickness01 = 0.25f)
        {
            ArgumentNullException.ThrowIfNull(surface);
            size = Math.Max(2, size);
            return surface.CreateTexture(BakeRingPixels(size, innerRadius01, thickness01), size, size);
        }

        /// <summary>Bakes a hollow ring and uploads it to a sampleable texture on the snapshot <paramref name="context"/>'s device.</summary>
        public static Texture2D BakeRing(Render2DContext context, int size = 64, float innerRadius01 = 0.55f, float thickness01 = 0.25f)
        {
            ArgumentNullException.ThrowIfNull(context);
            size = Math.Max(2, size);
            return context.CreateTexture(BakeRingPixels(size, innerRadius01, thickness01), size, size);
        }

        /// <summary>Creates a 1x1 opaque white texture on <paramref name="surface"/>'s device (for solid VFX quads).</summary>
        public static Texture2D White(Render2DSurface surface)
            => Render2DTextures.White(surface);

        /// <summary>Creates a 1x1 opaque white texture on the snapshot <paramref name="context"/>'s device.</summary>
        public static Texture2D White(Render2DContext context)
            => Render2DTextures.White(context);
    }
}
