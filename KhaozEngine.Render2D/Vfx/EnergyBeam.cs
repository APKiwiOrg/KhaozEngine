using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render2D.Vfx
{
    /// <summary>
    /// Draws an animated additive energy beam from A to B: a soft glow band under a bright (optionally dashed,
    /// pulsing, jittering) core, with optional round end-caps and glow flares at the endpoints. Time-driven and
    /// stateless - the caller passes the elapsed time in seconds, so there is no hidden mutable state and the same
    /// time always renders the same frame. The beam is composited additively regardless of the batch's current
    /// <see cref="BlendMode"/> (it is set and restored around the draw).
    /// </summary>
    public static class EnergyBeam
    {
        /// <summary>
        /// Draws the beam between <paramref name="a"/> and <paramref name="b"/> (screen-space points) on
        /// <paramref name="batch"/>. <paramref name="white"/> is a 1x1 (or solid) white texture for the band/core;
        /// <paramref name="glow"/> is an optional radial-glow texture used for the endpoint flares and the round
        /// end-caps (<see cref="BeamParams.Caps"/>); neither is drawn when it is null. <paramref name="timeSeconds"/>
        /// drives the dash flow, pulse, and jitter.
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
            {
                DrawBand(batch, white, a, dir, perp, length, p.GlowWidth * pulse, p.GlowColor,
                    segs, segLen, timeSeconds, p, dashed: false);
                // Round glow caps go under the core, matching the band draw order.
                DrawCaps(batch, glow, RoundCaps(a, b, p.Caps, p.GlowWidth, pulse), p.GlowColor);
            }

            // Bright inner core (optionally dashed/jittered).
            if (p.CoreWidth > 0f && p.CoreColor.A > 0f)
            {
                DrawBand(batch, white, a, dir, perp, length, p.CoreWidth * pulse, p.CoreColor,
                    segs, segLen, timeSeconds, p, dashed: p.DashLength > 0f);
                // Round core caps sit over the glow caps.
                DrawCaps(batch, glow, RoundCaps(a, b, p.Caps, p.CoreWidth, pulse), p.CoreColor);
            }

            // Endpoint flares (independent of and larger than the round caps).
            if (glow != null && p.FlareRadius > 0f)
            {
                Color flare = p.CoreColor * pulse;
                DrawDisc(batch, glow, a, p.FlareRadius * pulse, flare);
                DrawDisc(batch, glow, b, p.FlareRadius * pulse, flare);
            }

            batch.BlendMode = prev;
        }

        /// <summary>Draws both round end-caps of a band (a no-op when there is no glow texture or caps are off).</summary>
        static void DrawCaps(SpriteBatch batch, Texture2D? glow, in BeamCaps caps, Color color)
        {
            if (glow == null || !caps.Enabled) return;
            DrawDisc(batch, glow, caps.A, caps.Radius, color);
            DrawDisc(batch, glow, caps.B, caps.Radius, color);
        }

        static void DrawBand(SpriteBatch batch, Texture2D white, Vector2 a, Vector2 dir, Vector2 perp,
            float length, float width, Color color, int segs, float segLen, float timeSeconds,
            in BeamParams p, bool dashed)
        {
            // Jagged needs no speed: 0 holds one still bolt. Wave needs one, or there is nothing to oscillate.
            bool jagged = p.JitterShape == BeamJitter.Jagged && p.JitterAmount > 0f;
            int roll = jagged ? RollIndex(timeSeconds, p.JitterSpeed) : 0;
            float axisAngle = MathF.Atan2(dir.Y, dir.X);

            for (int i = 0; i < segs; i++)
            {
                // Dashing measures along the AXIS in both modes, so a jagged bolt's dashes stay evenly spaced
                // rather than bunching wherever the bolt happens to zigzag.
                float along = (i + 0.5f) * segLen;
                float alpha = dashed ? DashAlpha(along, timeSeconds, p.DashLength, p.DashGap, p.DashSpeed) : 1f;
                if (alpha <= 0f) continue;

                Vector2 centre;
                float quadLength, quadAngle;
                if (jagged)
                {
                    // Each quad spans its two displaced boundaries, so it TILTS with the bolt instead of staying
                    // axis-aligned. Axis-aligned quads at zigzagging centres would leave the band visibly broken.
                    Vector2 p0 = a + dir * (i * segLen) + perp * BoltOffset(p.JitterSeed, roll, i, segs, p.JitterAmount);
                    Vector2 p1 = a + dir * ((i + 1) * segLen) + perp * BoltOffset(p.JitterSeed, roll, i + 1, segs, p.JitterAmount);
                    Vector2 span = p1 - p0;
                    float spanLength = span.Length();
                    if (spanLength <= 0f) continue;
                    centre = (p0 + p1) * 0.5f;
                    quadAngle = MathF.Atan2(span.Y, span.X);
                    // Overlap neighbours by the band width so the wedge gap on the outside of each bend closes.
                    // Two rectangles butted at an angle do not meet, and on an additive core that gap reads as a
                    // dashed bolt. The cost is half a width of overhang at each endpoint, under the flare.
                    quadLength = spanLength + width;
                }
                else
                {
                    centre = a + dir * along;
                    if (p.JitterAmount > 0f && p.JitterSpeed > 0f)
                    {
                        float wobble = MathF.Sin(timeSeconds * p.JitterSpeed + along * 0.05f) * p.JitterAmount;
                        centre += perp * wobble;
                    }
                    quadLength = segLen;
                    quadAngle = axisAngle;
                }

                // Rotated quad centred on the segment: pivot (0.5,0.5) lands at centre.
                batch.Draw(white, centre, new Vector2(quadLength, width), new Vector2(0.5f, 0.5f),
                    quadAngle, PrimitiveRenderer.FullUV, color * alpha);
            }
        }

        /// <summary>
        /// Which bolt <see cref="BeamJitter.Jagged"/> is drawing at <paramref name="timeSeconds"/>: the whole
        /// number of re-rolls elapsed at <paramref name="rollsPerSecond"/>. A non-positive rate holds roll 0, one
        /// still bolt. Monotone non-decreasing in time, and pure, which is what keeps the beam stateless and every
        /// client on the same bolt.
        /// </summary>
        internal static int RollIndex(float timeSeconds, float rollsPerSecond) =>
            rollsPerSecond > 0f ? (int)MathF.Floor(timeSeconds * rollsPerSecond) : 0;

        /// <summary>
        /// The mid-span envelope a jagged bolt's displacement is scaled by: 0 at both endpoints (so the bolt
        /// starts and ends exactly on the two points it connects), 1 halfway along. A degenerate
        /// <paramref name="segs"/> is 0. Pure.
        /// </summary>
        internal static float BoltEnvelope(int index, int segs) =>
            segs <= 0 ? 0f : MathF.Sin(index / (float)segs * MathF.PI);

        /// <summary>
        /// Signed perpendicular displacement, in pixels, of segment boundary <paramref name="index"/> of a jagged
        /// bolt: its own noise draw times <paramref name="amount"/> times <see cref="BoltEnvelope"/>. Keyed on
        /// (<paramref name="seed"/>, <paramref name="roll"/>, <paramref name="index"/>) through
        /// <see cref="StableHash"/>, so it is a pure key-to-value map with no RNG stream to thread anywhere: the
        /// same bolt redraws identically, a new roll is a wholly new bolt, and neighbouring segments are
        /// independent (which is the whole difference from the coherent sine wobble). Pure.
        /// </summary>
        internal static float BoltOffset(int seed, int roll, int index, int segs, float amount)
        {
            float envelope = BoltEnvelope(index, segs);
            if (envelope <= 0f) return 0f;
            float noise = StableHash.ToUnitFloat(StableHash.Mix((uint)seed, (uint)roll, (uint)index)) * 2f - 1f;
            return noise * amount * envelope;
        }

        /// <summary>Draws a soft radial disc of <paramref name="radius"/> pixels (shared by endpoint flares and round caps).</summary>
        static void DrawDisc(SpriteBatch batch, Texture2D glow, Vector2 centre, float radius, Color color)
        {
            float d = radius * 2f;
            batch.Draw(glow, centre, new Vector2(d, d), new Vector2(0.5f, 0.5f), 0f, PrimitiveRenderer.FullUV, color);
        }

        /// <summary>
        /// The round end-caps of one beam band (see <see cref="RoundCaps"/>): a soft disc of <see cref="Radius"/>
        /// pixels centred on each endpoint (<see cref="A"/>, <see cref="B"/>), or <see cref="Enabled"/> = false when
        /// no cap should be drawn. Pure value.
        /// </summary>
        internal readonly record struct BeamCaps(bool Enabled, Vector2 A, Vector2 B, float Radius);

        /// <summary>
        /// Round end-cap geometry for one band: a disc of radius half the pulse-adjusted band width centred on each
        /// endpoint, so the cap sits flush with the band and rounds the otherwise-square end.
        /// <see cref="BeamCaps.Enabled"/> is false (a no-op) when <paramref name="cap"/> is not
        /// <see cref="BeamCap.Round"/>, the band is invisible (<paramref name="bandWidth"/> &lt;= 0), or the beam is
        /// degenerate (A == B). Pure.
        /// </summary>
        internal static BeamCaps RoundCaps(Vector2 a, Vector2 b, BeamCap cap, float bandWidth, float pulse)
        {
            if (cap != BeamCap.Round || bandWidth <= 0f || Axis(a, b).Length <= 0f)
                return default;
            return new BeamCaps(true, a, b, bandWidth * pulse * 0.5f);
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
