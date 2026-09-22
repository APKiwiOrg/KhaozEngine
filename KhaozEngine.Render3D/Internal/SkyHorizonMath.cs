using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>The ground band a <see cref="SkyHorizon.World"/> sky paints below elevation zero. The default value
    /// is "not live", which leaves every evaluation on the historical groundless sky.</summary>
    internal readonly struct SkyGround
    {
        public SkyGround(Vector3 color, float softness)
        {
            Live = true;
            Color = color;
            Softness = softness;
        }

        /// <summary>False for <see cref="SkyHorizon.Screen"/> (and for the fallbacks onto it).</summary>
        public bool Live { get; }

        /// <summary>Ground band colour RGB.</summary>
        public Vector3 Color { get; }

        /// <summary>Depth of the blend below the horizon, in sin-elevation units.</summary>
        public float Softness { get; }

        /// <summary>How much of the ground covers a ray at <paramref name="sinElevation"/>: 0 at and above the
        /// horizon, 1 once the ray is <see cref="Softness"/> below it. The ground goes OVER the sun, which is what
        /// lets the disc set through the line. Mirrored by <c>SkyFrag</c> and the water's
        /// <c>skyAlongDirection</c>.</summary>
        public float Weight(float sinElevation) =>
            Live ? SkyMath.Smoothstep(0f, MathF.Max(Softness, SkyHorizonMath.MinSoftness), -sinElevation) : 0f;

        /// <summary>Lay the ground band over an already shaded sky colour.</summary>
        public Vector3 Over(Vector3 sky, float sinElevation) =>
            Live ? Vector3.Lerp(sky, Color, Weight(sinElevation)) : sky;
    }

    /// <summary>What the sky pass needs to turn a screen pixel into the elevation of its view ray: the view ray
    /// through NDC <c>(x, y)</c> is <c>(x * Ray.X + Ray.Z, y * Ray.Y + Ray.W, -1)</c> in view space, and
    /// <see cref="Up"/> carries the world-Y component of the camera's right, up and back axes. The default value is
    /// "not live" (the screen-space sky).</summary>
    internal readonly struct SkyHorizonFrame
    {
        public SkyHorizonFrame(Vector4 ray, Vector3 up, SkyGround ground)
        {
            Ray = ray;
            Up = up;
            Ground = ground;
        }

        public Vector4 Ray { get; }
        public Vector3 Up { get; }
        public SkyGround Ground { get; }
        public bool Live => Ground.Live;

        /// <summary>Sine of the elevation of the view ray through <paramref name="ndc"/> (x,y in [-1,1], y up):
        /// 0 on the world horizon, 1 straight up, negative below. Camera roll and an off-centre projection are
        /// both honoured.</summary>
        public float SinElevation(Vector2 ndc)
        {
            float vx = ndc.X * Ray.X + Ray.Z;
            float vy = ndc.Y * Ray.Y + Ray.W;
            return (vx * Up.X + vy * Up.Y - Up.Z) / MathF.Sqrt(vx * vx + vy * vy + 1f);
        }
    }

    /// <summary>
    /// The world-horizon half of the procedural sky (<see cref="SkyHorizon.World"/>): which frames get one, and the
    /// camera terms the pass needs. Pure, headless-tested, and mirrored by <c>SkyFrag</c>.
    /// </summary>
    internal static class SkyHorizonMath
    {
        /// <summary>Floor on the blend depth, so a zero softness is a one-pixel-ish hard line and never a divide by
        /// zero. The GLSL mirrors carry the same literal.</summary>
        public const float MinSoftness = 1e-5f;

        /// <summary>True for a perspective projection, or a view-projection built from one (row-vector convention:
        /// the fourth column carries the perspective divide, and an orthographic matrix leaves it at 0,0,0,1).</summary>
        public static bool IsPerspective(Matrix4x4 m) =>
            MathF.Abs(m.M14) + MathF.Abs(m.M24) + MathF.Abs(m.M34) > 1e-6f;

        /// <summary>The ground band for these settings, live only when the world horizon can really be drawn:
        /// <see cref="SkyHorizon.World"/>, a world-anchored sun and a perspective camera. Anything else is the
        /// historical groundless sky.</summary>
        public static SkyGround ResolveGround(SkySettings sky, Matrix4x4 projectionOrViewProjection)
        {
            if (sky.Horizon != SkyHorizon.World || sky.Anchor != SunAnchor.World) return default;
            if (!IsPerspective(projectionOrViewProjection)) return default;
            Vector4 g = sky.GroundColor;
            return new SkyGround(new Vector3(g.X, g.Y, g.Z), MathF.Max(0f, sky.HorizonSoftness));
        }

        /// <summary>The per-frame camera terms for the sky pass, or the not-live default when the world horizon
        /// does not apply (see <see cref="ResolveGround"/>) or the projection cannot be inverted.</summary>
        public static SkyHorizonFrame ResolveFrame(SkySettings sky, Matrix4x4 view, Matrix4x4 projection)
        {
            SkyGround ground = ResolveGround(sky, projection);
            if (!ground.Live) return default;
            if (MathF.Abs(projection.M11) < 1e-8f || MathF.Abs(projection.M22) < 1e-8f) return default;

            // clip = viewRow * projection and w = -z, so a ray at view z = -1 lands on ndc.x = vx * M11 - M31.
            var ray = new Vector4(
                1f / projection.M11, 1f / projection.M22,
                projection.M31 / projection.M11, projection.M32 / projection.M22);
            // Row-vector view matrix: the camera's right, up and back axes are its first three COLUMNS, so their
            // world-Y components are the second row.
            var up = new Vector3(view.M21, view.M22, view.M23);
            return new SkyHorizonFrame(ray, up, ground);
        }
    }
}
