using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Grazing-sun cascade coverage (issue #394): a tall caster standing well UP-SUN of the camera must still shadow
    /// the ground it reaches, at a sun elevation low enough that its top sits in front of the near cascade's ortho
    /// near plane.
    /// <para>
    /// The geometry. Each cascade places its light eye <c>2r</c> up-light of the slice-sphere centre with the ortho
    /// near plane AT the eye, so its depth range spans <c>4r</c> and a world point's stored depth is
    /// <c>z = (dot(p - c, lightDir) + 2r) / 4r</c>. A receiver <c>s</c> and the caster point that shades it lie on
    /// ONE light ray separated by <c>h / sin(e)</c>, so the caster's depth is <c>z(s) - h / (4 r sin e)</c>: it goes
    /// NEGATIVE (in front of the near plane) once <c>h &gt; 4 r sin(e) z(s)</c>, which for a receiver near the
    /// cascade centre is <c>h &gt; 2 r sin(e)</c>. At 15 degrees that budget is only about half a cascade radius, and
    /// the caster that spends it stands roughly <c>2r</c> up-sun of the ground it shades, which is normally BEHIND
    /// the camera and therefore invisible - which is why the defect reads as ground that simply refuses to be
    /// shadowed rather than as a missing caster.
    /// </para>
    /// <para>
    /// Before the depth-pass pancake, that caster was CLIPPED out of the near cascade instead of clamped, and since
    /// the receiver takes the first cascade whose UV accepts it with no fall-through, the ground read the atlas clear
    /// value and rendered fully lit. Both tests here pin that: the headless one pins the geometry (the caster really
    /// is in front of the near plane, and the pancake contract recovers it), the GPU one pins the rendered outcome.
    /// </para>
    /// <para>
    /// This is a property/invariant golden (no committed per-backend grid), so it runs on every backend leg of the
    /// cross-platform matrix, which selects on "Golden" in the fully-qualified name. Do not rename the class to drop
    /// it. See docs/CROSS-PLATFORM.md.
    /// </para>
    /// </summary>
    public sealed class LowSunShadowGoldenTests
    {
        const int W = 480, H = 320;

        /// <summary>Sun elevation above the horizon. Low enough that a tall caster overruns the near cascade's
        /// <c>2 r sin(e)</c> height budget by a wide margin.</summary>
        const float SunElevationDegrees = 15f;

        // A tall thin caster standing UP-SUN of the camera (behind it, so it never draws in the main pass) whose
        // shadow stripe runs forward across the visible ground. Height is set so the stripe reaches well past the
        // probe: the shadow tip sits at CasterZ + CasterHeight / tan(e) = 27.7.
        const float CasterZ = -32f, CasterHeight = 16f, CasterWidth = 2f;

        /// <summary>Visible ground inside the caster's shadow stripe.</summary>
        static readonly Vector3 ProbeGround = new(0f, 0f, 8f);
        /// <summary>Visible ground beside the stripe, the lit reference the probe is measured against.</summary>
        static readonly Vector3 LitGround = new(-10f, 0f, 8f);

        /// <summary>The key-light travel direction: due +Z, <see cref="SunElevationDegrees"/> above the horizon.</summary>
        static Vector3 SunDirection
        {
            get
            {
                float e = SunElevationDegrees * MathF.PI / 180f;
                return Vector3.Normalize(new Vector3(0f, -MathF.Sin(e), MathF.Cos(e)));
            }
        }

        static FlyCamera3D MakeCamera() => new()
        {
            Position = new Vector3(0f, 5f, -8f),
            Yaw = 0f,
            Pitch = -0.30f,
            FieldOfView = 0.9f,
            AspectRatio = (float)W / H,
            NearPlane = 0.5f,
            FarPlane = 160f,
        };

        // The caster point that shades ProbeGround: same light ray, so the same shadow-map texel, at the height whose
        // shadow lands exactly on the probe.
        static Vector3 CasterPointShadingProbe()
        {
            float e = SunElevationDegrees * MathF.PI / 180f;
            return new Vector3(0f, (ProbeGround.Z - CasterZ) * MathF.Tan(e), CasterZ);
        }

        [Fact]
        public void LowSun_CasterShadingTheProbe_SitsInFrontOfTheNearCascadeNearPlane()
        {
            Span<Matrix4x4> mats = stackalloc Matrix4x4[ShadowSettings.MaxCascades];
            int count = FitCascades(MakeCamera(), SunDirection, mats);

            // The probe takes the tightest cascade that accepts it, exactly as the receiver shader does.
            int sel = ShadowMapMath.SelectCascade(mats, count, ProbeGround);
            Assert.True(sel >= 0, "the probe fell outside every cascade; re-frame the scene");

            float receiverZ = ClipZ(mats[sel], ProbeGround);
            float casterZ = ClipZ(mats[sel], CasterPointShadingProbe());
            Assert.InRange(receiverZ, 0f, 1f);

            // The defect: the caster that shades a receiver the cascade DOES cover is itself in front of that
            // cascade's near plane, so a clipping depth pass drops it and the probe reads the atlas clear = lit.
            Assert.True(casterZ < 0f,
                $"cascade {sel} no longer clips the caster (caster z {casterZ:0.###}, receiver z {receiverZ:0.###}); " +
                "the scene stopped exercising issue #394, so re-tune the sun elevation or the caster distance");

            // The contract that recovers it: the depth pass clamps instead of clipping, so the caster records depth
            // at the near plane with its silhouette intact, and the receiver in front of it reads shadowed.
            float recorded = ShadowMapMath.PancakeDepth(casterZ);
            Assert.InRange(recorded, 0f, 1f);
            Assert.True(receiverZ > recorded,
                $"the pancaked caster must sit in front of the receiver (recorded {recorded:0.###}, receiver {receiverZ:0.###})");
        }

        // A tall caster standing up-sun of the camera must shadow the ground its stripe reaches. RED before the
        // depth-pass pancake: cascade 0 clipped the caster, the probe read the atlas clear value and rendered as
        // brightly as the open ground beside it.
        [GpuFact]
        public void Golden3D_LowSunTallCaster_ShadowsTheGround()
        {
            MeshHandle floor = default, caster = default;
            var fly = MakeCamera();
            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(160f, 0.1f));
                    caster = scene.LoadMesh(MeshPrimitives.Box(1f));
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.Post.BackgroundColor = new Color(0.10f, 0.12f, 0.16f, 1f);
                    scene.CameraOverride = fly;
                    scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
                    scene.Post.LightDirection = SunDirection;
                },
                drawFrame: scene =>
                {
                    scene.Draw(floor, Matrix4x4.Identity, new Color(0.60f, 0.61f, 0.63f, 1f));
                    scene.Draw(caster,
                        Matrix4x4.CreateScale(CasterWidth, CasterHeight, CasterWidth)
                        * Matrix4x4.CreateTranslation(0f, CasterHeight * 0.5f, CasterZ),
                        new Color(0.2f, 0.75f, 0.25f, 1f));
                },
                frames: 2);

            float lit = GroundLuminance(rgba, fly, LitGround);
            float shaded = GroundLuminance(rgba, fly, ProbeGround);
            Assert.True(lit > 1e-3f, "the lit reference ground is not visible; re-frame the scene");
            float ratio = shaded / lit;
            Assert.True(ratio < 0.8f,
                $"the ground under a tall up-sun caster rendered lit (ratio {ratio:0.###}) at a {SunElevationDegrees} " +
                "degree sun: the near cascade dropped the caster instead of pancaking it (issue #394)");
        }

        // Mirror Scene3D.ComputeShadowCascades for one camera: frustum corners, the practical split over the default
        // shadow distances, a slice-sphere fit per cascade, texel-snapped at the default resolution.
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

            var defaults = new ShadowSettings();
            int count = defaults.ResolvedCascadeCount;
            Span<float> splits = stackalloc float[ShadowSettings.MaxCascades];
            ShadowMapMath.FillCascadeSplits(splits, count, defaults.ShadowNearDistance, defaults.ResolvedMaxDistance);
            float prev = camNear;
            for (int i = 0; i < count; i++)
            {
                float d = Math.Clamp(splits[i], camNear, camFar);
                ShadowMapMath.SliceBoundingSphere(corners, (prev - camNear) / range, (d - camNear) / range,
                    out Vector3 center, out float radius);
                mats[i] = ShadowMapMath.BuildLightViewProj(light, center, radius, defaults.ShadowMapResolution);
                prev = MathF.Max(d, prev);
            }
            return count;
        }

        static float ClipZ(in Matrix4x4 mat, Vector3 p)
        {
            Vector4 lc = Vector4.Transform(new Vector4(p, 1f), mat);
            return lc.Z / lc.W;
        }

        // Average luminance of the 3x3 pixel block at a ground point's screen position.
        static float GroundLuminance(byte[] rgba, FlyCamera3D cam, Vector3 world)
        {
            if (!cam.WorldToScreen(world, W, H, out Vector2 p)) return 0f;
            int px = (int)(p.X + 0.5f), py = (int)(p.Y + 0.5f);
            long r = 0, g = 0, b = 0; int n = 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = px + dx, y = py + dy;
                    if (x < 0 || y < 0 || x >= W || y >= H) continue;
                    int i = (y * W + x) * 4;
                    r += rgba[i]; g += rgba[i + 1]; b += rgba[i + 2]; n++;
                }
            if (n == 0) return 0f;
            float rf = r / (255f * n), gf = g / (255f * n), bf = b / (255f * n);
            return 0.299f * rf + 0.587f * gf + 0.114f * bf;
        }
    }
}
