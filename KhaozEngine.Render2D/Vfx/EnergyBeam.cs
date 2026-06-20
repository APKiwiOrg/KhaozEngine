using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render2D.Vfx
{
    /// <summary>
    /// Draws an animated additive energy beam from A to B: a soft glow band under a bright (optionally dashed,
    /// pulsing, jittering) core, with glow flares at the endpoints. Time-driven and stateless - the caller passes
    /// the elapsed time in seconds, so there is no hidden mutable state and the same time always renders the same
    /// frame. The beam is composited additively regardless of the batch's current <see cref="BlendMode"/> (it is
    /// set and restored around the draw).
    /// </summary>
    public static class EnergyBeam
    {
        /// <summary>
        /// Draws the beam between <paramref name="a"/> and <paramref name="b"/> (screen-space points) on
        /// <paramref name="batch"/>. <paramref name="white"/> is a 1x1 (or solid) white texture for the band/core;
        /// <paramref name="glow"/> is an optional radial-glow texture used for the endpoint flares (no flare is
        /// drawn when it is null). <paramref name="timeSeconds"/> drives the dash flow, pulse, and jitter.
        /// </summary>
        public static void Draw(SpriteBatch batch, Texture2D white, Texture2D? glow,
            Vector2 a, Vector2 b, in BeamParams p, float timeSeconds)
        {
            ArgumentNullException.ThrowIfNull(batch);
            ArgumentNullException.ThrowIfNull(white);

            var (length, angle) = Axis(a, b);
            if (length <= 0f) return;

            Vector2 dir = (b - a) / length;
            Vector2 perp = new(-dir.Y, dir.X);   // unit perpendicular (screen y-down)

            // Brightness/width pulse in [1-amount, 1+amount].
            float pulse = p.PulseAmount > 0f && p.PulseSpeed > 0f
                ? 1f + p.PulseAmount * MathF.Sin(timeSeconds * p.PulseSpeed)
                : 1f;

            BlendMode prev = batch.BlendMode;
            batch.BlendMode = BlendMode.Additive;

            int segs = Math.Max(1, p.Segments);
            float segLen = length / segs;

            // Soft outer glow band (solid, continuous) drawn first so the core sits on top.
            if (p.GlowWidth > 0f && p.GlowColor.A > 0f)
                DrawBand(batch, white, a, dir, perp, length, p.GlowWidth * pulse, p.GlowColor,
                    segs, segLen, timeSeconds, p, dashed: false);

            // Bright inner core (optionally dashed/jittered).
            if (p.CoreWidth > 0f && p.CoreColor.A > 0f)
                DrawBand(batch, white, a, dir, perp, length, p.CoreWidth * pulse, p.CoreColor,
                    segs, segLen, timeSeconds, p, dashed: p.DashLength > 0f);

            // Endpoint flares.
            if (glow != null && p.FlareRadius > 0f)
            {
                Color flare = p.CoreColor * pulse;
                DrawFlare(batch, glow, a, p.FlareRadius * pulse, flare);
                DrawFlare(batch, glow, b, p.FlareRadius * pulse, flare);
            }

            batch.BlendMode = prev;
        }

        static void DrawBand(SpriteBatch batch, Texture2D white, Vector2 a, Vector2 dir, Vector2 perp,
            float length, float width, Color color, int segs, float segLen, float timeSeconds,
            in BeamParams p, bool dashed)
        {
            for (int i = 0; i < segs; i++)
            {
                float along = (i + 0.5f) * segLen;
                float alpha = dashed ? DashAlpha(along, timeSeconds, p.DashLength, p.DashGap, p.DashSpeed) : 1f;
                if (alpha <= 0f) continue;

                Vector2 centre = a + dir * along;
                if (p.JitterAmount > 0f && p.JitterSpeed > 0f)
                {
                    float wobble = MathF.Sin(timeSeconds * p.JitterSpeed + along * 0.05f) * p.JitterAmount;
                    centre += perp * wobble;
                }
                // Rotated quad centred on the segment: pivot (0.5,0.5) lands at centre.
                batch.Draw(white, centre, new Vector2(segLen, width), new Vector2(0.5f, 0.5f),
                    MathF.Atan2(dir.Y, dir.X), PrimitiveRenderer.FullUV, color * alpha);
            }
        }

        static void DrawFlare(SpriteBatch batch, Texture2D glow, Vector2 centre, float radius, Color color)
        {
            float d = radius * 2f;
            batch.Draw(glow, centre, new Vector2(d, d), new Vector2(0.5f, 0.5f), 0f, PrimitiveRenderer.FullUV, color);
        }

        /// <summary>Length and screen-space angle (radians, atan2 of B-A) of the beam axis. Pure.</summary>
        internal static (float Length, float Angle) Axis(Vector2 a, Vector2 b)
        {
            Vector2 d = b - a;
            return (d.Length(), MathF.Atan2(d.Y, d.X));
        }

        /// <summary>Unit vector perpendicular to A-&gt;B (screen y-down); zero for a degenerate beam. Pure.</summary>
        internal static Vector2 Perpendicular(Vector2 a, Vector2 b)
        {
            Vector2 d = b - a;
            float len = d.Length();
            if (len <= 0f) return Vector2.Zero;
            Vector2 dir = d / len;
            return new Vector2(-dir.Y, dir.X);
        }

        /// <summary>
        /// Dash mask in {0,1} at <paramref name="distance"/> pixels along the beam: 1 inside a lit dash, 0 in a
        /// gap. The pattern scrolls by <paramref name="dashSpeed"/>*<paramref name="timeSeconds"/>. A
        /// non-positive <paramref name="dashLength"/> (or period) is solid (always 1). Pure.
        /// </summary>
        internal static float DashAlpha(float distance, float timeSeconds, float dashLength, float dashGap, float dashSpeed)
        {
            if (dashLength <= 0f) return 1f;
            float period = dashLength + dashGap;
            if (period <= 0f) return 1f;
            float phase = (distance - timeSeconds * dashSpeed) % period;
            if (phase < 0f) phase += period;
            return phase < dashLength ? 1f : 0f;
        }
    }
}
