using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// The evidence behind NOT adding per-cascade re-render skipping to the shadow depth pass.
    /// <para>
    /// The idea is obvious and keeps getting re-proposed: the atlas already skips entirely when nothing moved
    /// (<c>Scene3D.ShadowDepthPassDirty</c>), so why not skip PER CASCADE, re-rendering only the cascades whose
    /// matrix and caster set actually changed. It would pay if cascade matrices held still frame to frame. They do
    /// not. A cascade's matrix is <see cref="ShadowMapMath.BuildLightViewProj"/> of the light DIRECTION plus the
    /// fitted slice sphere, and Ruinborne's sun sweeps a 30 minute day, about 0.2 degrees per second. At 60 fps
    /// that is 0.0033 degrees per frame: invisible to look at, and tens of times float32 epsilon, so every entry of
    /// every cascade's view rotation changes every single frame. The texel snap does not help - it quantizes the
    /// focus TRANSLATION in light-view space, and the light-view ROTATION is what the sun moves. Nor does a static
    /// sun save it: a camera step moves the fitted slice sphere's RADIUS, which sets the unsnapped ortho extents.
    /// </para>
    /// <para>
    /// These tests measure that rather than asserting it from theory, and they print the numbers. If a future
    /// change ever does make cascade matrices stable under a moving sun, they turn red and the idea is worth
    /// revisiting. What WOULD enable per-cascade skipping is quantizing the light direction so the fit only moves
    /// in steps: that was tried in the 13.x line and un-adopted after playtest (the shadow visibly stepped), so it
    /// is a deliberate non-option, not an oversight.
    /// </para>
    /// </summary>
    public sealed class ShadowCascadeStabilityTests
    {
        const int Resolution = 2048;
        const int Frames = 120;                        // two seconds at 60 fps
        const float SunDegreesPerFrame = 0.2f / 60f;   // Ruinborne: a 30 minute day
        const float WalkSpeedPerFrame = 5f / 60f;      // a player walking at 5 m/s

        readonly ITestOutputHelper _out;
        public ShadowCascadeStabilityTests(ITestOutputHelper o) => _out = o;

        static FlyCamera3D Camera(Vector3 position) => new()
        {
            Position = position,
            Yaw = 0.4f,
            Pitch = -0.25f,
            FieldOfView = 0.9f,
            AspectRatio = 16f / 9f,
            NearPlane = 0.5f,
            FarPlane = 200f,
        };

        static Vector3 Sun(float degrees)
        {
            const float elevation = 35f * MathF.PI / 180f;
            float a = degrees * MathF.PI / 180f;
            return Vector3.Normalize(new Vector3(MathF.Cos(a) * MathF.Cos(elevation), -MathF.Sin(elevation),
                MathF.Sin(a) * MathF.Cos(elevation)));
        }

        // Mirror Scene3D.ComputeShadowCascades for one camera + light: frustum corners, the practical split over the
        // default shadow distances, a slice-sphere fit per cascade, texel-snapped at the harness resolution.
        static int FitCascades(FlyCamera3D cam, Vector3 light, Span<Matrix4x4> mats)
        {
            Span<Vector3> corners = stackalloc Vector3[8];
            if (!ShadowMapMath.FrustumCornersWorld(cam.ViewProjection, corners)) return 0;
            Vector3 eye = cam.Eye, fwd = cam.Forward;
            Vector3 nearC = (corners[0] + corners[1] + corners[2] + corners[3]) * 0.25f;
            Vector3 farC = (corners[4] + corners[5] + corners[6] + corners[7]) * 0.25f;
            float camNear = Vector3.Dot(nearC - eye, fwd);
            float camFar = Vector3.Dot(farC - eye, fwd);
            float range = MathF.Max(camFar - camNear, 1e-3f);

            var defaults = new ShadowSettings { ShadowCascadeCount = 4 };
            int count = defaults.ResolvedCascadeCount;
            Span<float> splits = stackalloc float[ShadowSettings.MaxCascades];
            ShadowMapMath.FillCascadeSplits(splits, count, defaults.ShadowNearDistance, defaults.ResolvedMaxDistance);
            float prev = camNear;
            for (int i = 0; i < count; i++)
            {
                float d = Math.Clamp(splits[i], camNear, camFar);
                ShadowMapMath.SliceBoundingSphere(corners, (prev - camNear) / range, (d - camNear) / range,
                    out Vector3 centre, out float radius);
                mats[i] = ShadowMapMath.BuildLightViewProj(light, centre, radius, Resolution);
                prev = MathF.Max(d, prev);
            }
            return count;
        }

        // Walk the scenario for Frames frames and count, per cascade, how many frame-to-frame steps left that
        // cascade's fitted matrix BIT-IDENTICAL. That is exactly the condition a per-cascade skip would need.
        int[] StableFrames(string label, bool sunMoves, bool cameraMoves)
        {
            Span<Matrix4x4> prev = stackalloc Matrix4x4[ShadowSettings.MaxCascades];
            Span<Matrix4x4> cur = stackalloc Matrix4x4[ShadowSettings.MaxCascades];
            var stable = new int[ShadowSettings.MaxCascades];
            int count = 0;
            for (int f = 0; f < Frames; f++)
            {
                var cam = Camera(new Vector3(0f, 3f, cameraMoves ? f * WalkSpeedPerFrame : 0f));
                count = FitCascades(cam, Sun(sunMoves ? f * SunDegreesPerFrame : 0f), cur);
                if (f > 0)
                    for (int c = 0; c < count; c++)
                        if (cur[c] == prev[c]) stable[c]++;
                cur.CopyTo(prev);
            }
            var pct = new int[count];
            for (int c = 0; c < count; c++) pct[c] = stable[c] * 100 / (Frames - 1);
            _out.WriteLine($"{label,-34} stable frames per cascade: [{string.Join(", ", pct)}] percent " +
                           $"(of {Frames - 1} steps)");
            return pct;
        }

        [Fact]
        public void A_moving_sun_re_fits_every_cascade_every_frame()
        {
            // (a) the static-camera, moving-sun case: the one a per-cascade cache would have to survive, and the
            // normal state of a Ruinborne session, since the world clock never stops.
            int[] pct = StableFrames("static camera, moving sun", sunMoves: true, cameraMoves: false);
            foreach (int p in pct)
                Assert.Equal(0, p);
        }

        [Fact]
        public void A_moving_camera_re_fits_the_cascades_it_moves_past_the_texel_snap_of()
        {
            // (b) the moving-camera case with the sun HELD STILL, which never actually happens and is therefore
            // the most generous case available. It is still 0 percent: the texel snap quantizes the focus, but a
            // camera step also moves the fitted slice sphere's RADIUS, and the radius sets the ortho extents, which
            // are not snapped to anything. So even the easy case gives a per-cascade cache nothing to hold onto.
            int[] cameraOnly = StableFrames("moving camera, static sun", sunMoves: false, cameraMoves: true);
            foreach (int p in cameraOnly)
                Assert.Equal(0, p);

            // (c) both moving, which is what actually runs. Nothing is ever stable.
            int[] both = StableFrames("moving camera, moving sun", sunMoves: true, cameraMoves: true);
            foreach (int p in both)
                Assert.Equal(0, p);
        }

        [Fact]
        public void One_frame_of_sun_travel_moves_the_fit_above_float_epsilon()
        {
            // Why the numbers above are 0 rather than "nearly 0": a single frame of sun travel moves the rotation
            // basis by a few times 1e-6, tens of times float32 epsilon. There is no comparison tolerance to widen
            // here that would not simply be a light-direction quantization by another name, and that was tried in
            // the 13.x line and un-adopted after playtest.
            Span<Matrix4x4> a = stackalloc Matrix4x4[ShadowSettings.MaxCascades];
            Span<Matrix4x4> b = stackalloc Matrix4x4[ShadowSettings.MaxCascades];
            var cam = Camera(new Vector3(0f, 3f, 0f));
            int count = FitCascades(cam, Sun(0f), a);
            FitCascades(cam, Sun(SunDegreesPerFrame), b);

            float worst = 0f;
            for (int c = 0; c < count; c++)
            {
                Matrix4x4 d = a[c] - b[c];
                foreach (float v in new[] { d.M11, d.M12, d.M13, d.M21, d.M22, d.M23, d.M31, d.M32, d.M33 })
                    worst = MathF.Max(worst, MathF.Abs(v));
            }
            _out.WriteLine($"one frame of sun travel ({SunDegreesPerFrame:0.#####} deg) moves the rotation basis by up to {worst:0.#######}");
            Assert.True(worst > 1e-6f,
                $"the per-frame fit delta collapsed to {worst}, so per-cascade caching may now be worth re-evaluating");
        }
    }
}
