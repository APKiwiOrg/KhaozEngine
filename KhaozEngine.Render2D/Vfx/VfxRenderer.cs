using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render2D.Vfx
{
    /// <summary>
    /// Convenience owner for the 2D VFX helpers that need a texture: it bakes a radial glow, a hollow ring, and a
    /// 1x1 white pixel <em>at construction</em> (no shipped asset) and exposes them plus ready-made additive draws
    /// - glows/halos (<see cref="DrawGlow"/>), impact rings (<see cref="DrawRing"/>), and energy beams
    /// (<see cref="DrawBeam"/>). Pair with a <see cref="Particle2DSystem"/> by passing <see cref="WhitePixel"/> or
    /// <see cref="GlowTexture"/> to its <c>Draw</c>. Owns its textures; dispose it to free them.
    /// </summary>
    public sealed class VfxRenderer : IDisposable
    {
        /// <summary>The baked radial-glow texture (white RGB, radial alpha falloff).</summary>
        public Texture2D GlowTexture { get; }

        /// <summary>The baked hollow-ring texture.</summary>
        public Texture2D RingTexture { get; }

        /// <summary>A 1x1 opaque white texture for solid VFX quads (e.g. particle squares, beam cores).</summary>
        public Texture2D WhitePixel { get; }

        /// <summary>Bakes the glow/ring/white textures on <paramref name="surface"/>'s device.</summary>
        public VfxRenderer(Render2DSurface surface, int glowSize = 64, float glowFalloff = 2f, int ringSize = 64)
        {
            ArgumentNullException.ThrowIfNull(surface);
            GlowTexture = VfxTextures.BakeGlow(surface, glowSize, glowFalloff);
            RingTexture = VfxTextures.BakeRing(surface, ringSize);
            WhitePixel = VfxTextures.White(surface);
        }

        /// <summary>Bakes the glow/ring/white textures on the snapshot <paramref name="context"/>'s device.</summary>
        public VfxRenderer(Render2DContext context, int glowSize = 64, float glowFalloff = 2f, int ringSize = 64)
        {
            ArgumentNullException.ThrowIfNull(context);
            GlowTexture = VfxTextures.BakeGlow(context, glowSize, glowFalloff);
            RingTexture = VfxTextures.BakeRing(context, ringSize);
            WhitePixel = VfxTextures.White(context);
        }

        /// <summary>
        /// Draws a soft radial glow of <paramref name="radius"/> pixels centred at <paramref name="center"/>
        /// (sprite halo, impact flare, bloom). Additive by default; the batch's blend mode is restored afterwards.
        /// </summary>
        public void DrawGlow(SpriteBatch batch, Vector2 center, float radius, Color color, BlendMode blend = BlendMode.Additive)
        {
            ArgumentNullException.ThrowIfNull(batch);
            if (radius <= 0f) return;
            BlendMode prev = batch.BlendMode;
            batch.BlendMode = blend;
            float d = radius * 2f;
            batch.Draw(GlowTexture, center, new Vector2(d, d), new Vector2(0.5f, 0.5f), 0f, PrimitiveRenderer.FullUV, color);
            batch.BlendMode = prev;
        }

        /// <summary>
        /// Draws a one-shot hollow ring of outer <paramref name="radius"/> pixels centred at
        /// <paramref name="center"/> (impact/shockwave flash). Additive by default; the batch's blend mode is
        /// restored afterwards.
        /// </summary>
        public void DrawRing(SpriteBatch batch, Vector2 center, float radius, Color color, BlendMode blend = BlendMode.Additive)
        {
            ArgumentNullException.ThrowIfNull(batch);
            if (radius <= 0f) return;
            BlendMode prev = batch.BlendMode;
            batch.BlendMode = blend;
            float d = radius * 2f;
            batch.Draw(RingTexture, center, new Vector2(d, d), new Vector2(0.5f, 0.5f), 0f, PrimitiveRenderer.FullUV, color);
            batch.BlendMode = prev;
        }

        /// <summary>
        /// Draws an animated additive energy beam from <paramref name="a"/> to <paramref name="b"/> using the owned
        /// white (band/core) and glow (endpoint flares) textures. Forwards to <see cref="EnergyBeam.Draw"/>.
        /// </summary>
        public void DrawBeam(SpriteBatch batch, Vector2 a, Vector2 b, in BeamParams p, float timeSeconds)
            => EnergyBeam.Draw(batch, WhitePixel, GlowTexture, a, b, p, timeSeconds);

        /// <summary>
        /// Draws an additive attention pulse (expanding sonar rings + twinkling glints) centered at
        /// <paramref name="center"/> using the owned ring (sonar rings) and glow (glints) textures. Forwards to
        /// <see cref="AttentionBeacon.Draw"/>. Pass an unscaled real-time accumulator as
        /// <paramref name="timeSeconds"/> so the pulse animates regardless of game time-scale.
        /// </summary>
        public void DrawAttentionBeacon(SpriteBatch batch, Vector2 center, in AttentionBeaconParams p, float timeSeconds)
            => AttentionBeacon.Draw(batch, RingTexture, GlowTexture, center, p, timeSeconds);

        /// <summary>Frees the baked textures.</summary>
        public void Dispose()
        {
            GlowTexture.Dispose();
            RingTexture.Dispose();
            WhitePixel.Dispose();
        }
    }
}
