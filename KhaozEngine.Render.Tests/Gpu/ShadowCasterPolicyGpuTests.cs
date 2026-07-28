using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// GPU coverage of the two shadow-caster policies (issue #287), on the real cascaded depth pass. Pixel-presence,
    /// NOT golden: one fixed scene (a floor, a box caster, a shallow key light throwing a long shadow toward +z) is
    /// rendered several ways and the shadowed ground is compared against open lit ground, so the thresholds are
    /// backend-agnostic and nothing is baked.
    /// <list type="bullet">
    /// <item>a plain caster shadows the ground (the control, and the framing guard for everything below)</item>
    /// <item><c>castsShadows: false</c> removes the shadow while the caster still RENDERS (the per-layer opt-out)</item>
    /// <item>a half-dissolved caster shadows PARTIALLY: lighter than solid, darker than nothing - the defect this
    /// fixes was a fading prop casting a fully solid shadow right up to its cull radius. Run as a THEORY across
    /// three self-similar framings (cascade 0, 2 and 3), because the dithered depth used to survive only in
    /// cascade 0 (issue #391)</item>
    /// <item>a zero dissolve through the new overload matches the plain draw (the gated path is inert)</item>
    /// </list>
    /// Skipped unless KE_GPU_TESTS=1.
    /// </summary>
    public sealed class ShadowCasterPolicyGpuTests
    {
        readonly Xunit.Abstractions.ITestOutputHelper _out;
        public ShadowCasterPolicyGpuTests(Xunit.Abstractions.ITestOutputHelper o) => _out = o;

        const int W = 480, H = 320;

        static readonly Vector3 Light = Vector3.Normalize(new Vector3(0f, -0.5f, 0.87f));  // shallow: long +z shadow
        static readonly Matrix4x4 CasterXform = Matrix4x4.CreateScale(1.4f, 2.2f, 1.4f) * Matrix4x4.CreateTranslation(0f, 1.5f, 0f);
        static readonly Vector3 CamTarget = new(0f, 0f, 5f);
        static readonly Vector3 CamExtent = new(14f, 6f, 14f);
        const float CamAz = 0.6f, CamEl = 0.95f;

        // Ground points solidly inside the caster's long shadow, and one well off it.
        static readonly Vector3[] Probes =
        {
            new(-0.5f, 0f, 2.0f), new(0f, 0f, 2.0f), new(0.5f, 0f, 2.0f),
            new(-0.5f, 0f, 2.4f), new(0f, 0f, 2.4f), new(0.5f, 0f, 2.4f),
            new(-0.5f, 0f, 2.8f), new(0f, 0f, 2.8f), new(0.5f, 0f, 2.8f),
        };
        static readonly Vector3 LitRef = new(-6f, 0f, -2f);

        static byte[] Render(Action<Scene3D, MeshHandle, MeshHandle> drawFrame)
        {
            MeshHandle floor = default, caster = default;
            return Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(60f, 0.1f));
                    caster = scene.LoadMesh(MeshPrimitives.Box(1f));
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.Post.BackgroundColor = new Color(0.02f, 0.03f, 0.05f, 1f);
                    scene.Post.LightDirection = Light;
                    scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
                    scene.Camera.Azimuth = CamAz;
                    scene.Camera.Elevation = CamEl;
                    scene.Camera.Frame(CamTarget, CamExtent);
                },
                drawFrame: scene =>
                {
                    scene.Draw(floor, Matrix4x4.Identity, new Color(0.60f, 0.61f, 0.63f, 1f));
                    drawFrame(scene, floor, caster);
                },
                frames: 2);
        }

        // Mean luminance over the shadow probes as a fraction of the open lit ground: 1 means "as bright as lit"
        // (no shadow at all), and lower means more shadow.
        static float ShadowRatio(byte[] rgba)
        {
            var cam = new IsoCamera3D { Azimuth = CamAz, Elevation = CamEl };
            cam.Frame(CamTarget, CamExtent);
            cam.AspectRatio = (float)W / H;
            float lit = GroundLum(rgba, cam, LitRef);
            if (lit <= 1e-3f) return 1f;
            float sum = 0f;
            foreach (Vector3 p in Probes) sum += GroundLum(rgba, cam, p);
            return sum / Probes.Length / lit;
        }

        static float GroundLum(byte[] rgba, IsoCamera3D cam, Vector3 world)
        {
            if (!cam.WorldToScreen(world, W, H, out Vector2 p)) return 0f;
            return LumAt(rgba, p);
        }

        // Mean luminance of a 5x5 pixel patch around a projected point, clipped to the frame.
        static float LumAt(byte[] rgba, Vector2 p)
        {
            int px = (int)(p.X + 0.5f), py = (int)(p.Y + 0.5f);
            long r = 0, g = 0, b = 0; int n = 0;
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
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

        // The caster is the only green thing in the scene (floor is neutral grey), so its own pixels are countable -
        // that is what proves an opted-out caster still DRAWS rather than having been culled.
        static int CasterPixels(byte[] rgba)
        {
            int n = 0;
            for (int i = 0; i < rgba.Length; i += 4)
                if (rgba[i + 1] > 90 && rgba[i + 1] > rgba[i] + 40 && rgba[i + 1] > rgba[i + 2] + 40) n++;
            return n;
        }

        static readonly Color CasterColor = new(0.2f, 0.75f, 0.25f, 1f);

        [GpuFact]
        public void CastsShadows_false_removes_the_shadow_but_keeps_the_caster()
        {
            byte[] casting = Render((scene, _, caster) => scene.Draw(caster, CasterXform, CasterColor));
            byte[] notCasting = Render((scene, _, caster) =>
                scene.Draw(caster, CasterXform, CasterColor, Material.None, castsShadows: false));

            float castingRatio = ShadowRatio(casting);
            float notCastingRatio = ShadowRatio(notCasting);

            // Framing guard first: if the probes stopped landing in shadow, everything below is meaningless.
            Assert.True(castingRatio < 0.8f,
                $"the probes are not in shadow (ratio {castingRatio:0.###}), so the scene or camera moved");
            // With the caster opted out the probed ground reads as lit ground.
            Assert.True(notCastingRatio > 0.97f,
                $"an opted-out caster still darkened the ground (ratio {notCastingRatio:0.###})");
            // And it is still drawn: the opt-out is a shadow policy, not a cull.
            int drawn = CasterPixels(notCasting);
            Assert.True(drawn > 0, "the opted-out caster stopped rendering entirely");
            Assert.InRange(drawn, (int)(CasterPixels(casting) * 0.9f), (int)(CasterPixels(casting) * 1.1f));
        }

        // ---- The dissolving-caster contract, across the cascades (issue #391) -----------------------------------
        //
        // The original single-framing version of this test put the caster 14 m from the camera, entirely inside
        // cascade 0. Every claim about how a dithered caster behaves further out was therefore untested, which is
        // how the #391 field report reached a shipped build. This theory re-frames the SAME scene at three
        // distances so the caster lands in cascade 0, 2 and 3, and pins the contract in each.
        //
        // The world scene is IDENTICAL in every leg (same caster, same probes, same floor, same light). Only the
        // camera moves back, with its field of view narrowed to keep the framing on screen, so the caster stays the
        // same physical size and the same size in pixels while the cascade covering it grows. That matters: the
        // filter-vs-compare flip threshold is h / (2 * cascadeRadius) for a caster h above its receiver, so a scene
        // that scaled WITH the distance would hold that ratio constant and could not distinguish the cascades at
        // all. Here it falls from ~9 percent in cascade 0 to ~1.5 percent in cascade 3, and the noise cell goes
        // from ~12 shadow texels to ~2.
        //
        // Measured on Metal, these legs pass both before and after the #391 sampler change: the erasure the issue
        // predicted for the outer cascades did not reproduce here at any distance, resolution or dissolve level
        // tried, and point sampling moves the half-dissolved reading by a few percent (toward the true dither
        // coverage), not from "gone" to "there". They are kept as the coverage this suite was missing, not as a
        // repro. The measured crossfade defect has its own test in HlodCrossfadeShadowCoverageGpuTests.
        [GpuTheory]
        [InlineData(14f, 0)]     // cascade 0: the original framing, the only one that used to pass
        [InlineData(140f, 2)]    // cascade 2: noise cell ~3.1 shadow texels, flip threshold ~2.4 percent
        [InlineData(300f, 3)]    // cascade 3: noise cell ~2.0 shadow texels, flip threshold ~1.5 percent
        public void A_dissolving_caster_casts_a_partial_shadow(float distance, int expectedCascade)
        {
            // Framing guard: re-fit the cascades on the CPU exactly as Scene3D does and require the probe ground to
            // land in the cascade this leg claims to cover. Without it a camera/settings tweak could silently slide
            // every leg back into cascade 0 and the theory would go green while covering nothing new.
            int cascade = SelectedCascade(distance);
            Assert.True(cascade == expectedCascade,
                $"framing drift: at {distance} m the probe ground now selects cascade {cascade}, not {expectedCascade}. " +
                "Re-tune the distance or the shadow settings so this leg still exercises the cascade it claims.");

            byte[] solid = RenderAt(distance, (scene, caster, xform) => scene.Draw(caster, xform, CasterColor));
            byte[] fading = RenderAt(distance, (scene, caster, xform) =>
                scene.Draw(caster, xform, CasterColor, Material.None, dissolve: 0.5f, edgeWidth: 0f, edgeColor: default));
            byte[] none = RenderAt(distance, (scene, caster, xform) =>
                scene.Draw(caster, xform, CasterColor, Material.None, castsShadows: false));

            float solidRatio = ShadowRatioAt(solid, distance);
            float fadingRatio = ShadowRatioAt(fading, distance);
            float noneRatio = ShadowRatioAt(none, distance);
            _out.WriteLine($"cascade {expectedCascade} @ {distance} m: solid {solidRatio:0.####} " +
                           $"fading {fadingRatio:0.####} none {noneRatio:0.####}");

            Assert.True(solidRatio < 0.8f,
                $"cascade {expectedCascade}: the probes are not in shadow (ratio {solidRatio:0.###}), so the scene or camera moved");
            Assert.True(fadingRatio > solidRatio + 0.05f,
                $"cascade {expectedCascade}: a half-dissolved caster still cast an (almost) solid shadow: " +
                $"solid {solidRatio:0.###}, fading {fadingRatio:0.###}");
            Assert.True(fadingRatio < noneRatio - 0.05f,
                $"cascade {expectedCascade}: a half-dissolved caster cast (almost) no shadow: " +
                $"fading {fadingRatio:0.###}, none {noneRatio:0.###}");

            // Ordering alone is too weak: a sliver of surviving shadow still orders correctly while reading as no
            // shadow at all on screen. So pin the STRENGTH too. Half the noise sits above the 0.5 threshold, so
            // roughly half the caster's depth survives and the darkening must be a real fraction of the solid
            // caster's, not a rounding error. Wide band (0.2..0.9) because the point is "clearly partial" rather
            // than a bake, and because the far legs sit around 0.3 with a coarser per-cascade dither and want
            // headroom for a backend that filters or rasterizes the mask slightly differently. Measured on Metal:
            // 0.50 in cascade 0, 0.41 in cascade 2, 0.31 in cascade 3.
            float solidDarkening = noneRatio - solidRatio;
            float fadingDarkening = noneRatio - fadingRatio;
            float kept = solidDarkening > 1e-4f ? fadingDarkening / solidDarkening : 0f;
            Assert.True(kept > 0.2f,
                $"cascade {expectedCascade}: a half-dissolved caster kept only {kept:P0} of the solid caster's " +
                $"darkening (solid {solidRatio:0.###}, fading {fadingRatio:0.###}, none {noneRatio:0.###}); " +
                "the dithered depth is being erased, not thinned");
            Assert.True(kept < 0.9f,
                $"cascade {expectedCascade}: a half-dissolved caster kept {kept:P0} of the solid caster's darkening, " +
                "so the dissolve barely thinned the shadow at all");
        }

        // ---- The self-similar cascade-framing harness -----------------------------------------------------------

        // Four cascades reaching 400 m, so a framing exists that lands the caster in each of 0, 2 and 3. Both the
        // rendered scene and the CPU framing guard read these, so they cannot disagree.
        const int CascadeCount = 4;
        const float ShadowNear = 16f, ShadowMax = 400f;
        // Vertical world extent the frustum spans at the look point, held constant across the legs (it matches the
        // iso legs' CamExtent), so the field of view narrows as the camera pulls back and the framing is unchanged.
        const float FrameHeight = 14f;

        static ShadowSettings CascadeSettings() => new()
        {
            Mode = ShadowMode.ShadowMap,
            ShadowCascadeCount = CascadeCount,
            ShadowNearDistance = ShadowNear,
            ShadowMaxDistance = ShadowMax,
        };

        // A telephoto perspective camera <paramref name="distance"/> from the probe ground, on the same
        // azimuth/elevation the iso legs use. Near/far stay world-fixed so the cascade splits land in the same
        // world places at every distance and only WHICH cascade holds the caster changes.
        static FlyCamera3D FramingCamera(float distance)
        {
            Vector3 look = new(0f, 0f, 2.4f);
            Vector3 dirToEye = Vector3.Normalize(new Vector3(
                MathF.Sin(CamAz) * MathF.Cos(CamEl), MathF.Sin(CamEl), MathF.Cos(CamAz) * MathF.Cos(CamEl)));
            Vector3 fwd = -dirToEye;
            return new FlyCamera3D
            {
                Position = look + dirToEye * distance,
                Yaw = MathF.Atan2(fwd.X, fwd.Z),
                Pitch = MathF.Asin(fwd.Y),
                FieldOfView = 2f * MathF.Atan(FrameHeight * 0.5f / distance),
                AspectRatio = (float)W / H,
                NearPlane = 0.5f,
                FarPlane = distance + 60f,
            };
        }

        static byte[] RenderAt(float distance, Action<Scene3D, MeshHandle, Matrix4x4> drawCaster)
        {
            MeshHandle floor = default, caster = default;
            return Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(60f, 0.1f));
                    caster = scene.LoadMesh(MeshPrimitives.Box(1f));
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.Post.BackgroundColor = new Color(0.02f, 0.03f, 0.05f, 1f);
                    scene.Post.LightDirection = Light;
                    scene.CameraOverride = FramingCamera(distance);
                },
                drawFrame: scene =>
                {
                    scene.Draw(floor, Matrix4x4.Identity, new Color(0.60f, 0.61f, 0.63f, 1f));
                    drawCaster(scene, caster, CasterXform);
                },
                frames: 2, shadows: CascadeSettings());
        }

        static float ShadowRatioAt(byte[] rgba, float distance)
        {
            FlyCamera3D cam = FramingCamera(distance);
            float lit = GroundLum(rgba, cam, LitRef);
            if (lit <= 1e-3f) return 1f;
            float sum = 0f;
            foreach (Vector3 p in Probes) sum += GroundLum(rgba, cam, p);
            return sum / Probes.Length / lit;
        }

        // Mirror Scene3D.ComputeShadowCascades for this framing (practical split, slice-sphere fit, texel snap at the
        // default resolution) and report which cascade the probe ground falls in, using the same selection rule as
        // the receiver shader (ShadowMapMath.SelectCascade).
        static int SelectedCascade(float distance)
        {
            FlyCamera3D cam = FramingCamera(distance);
            Span<Vector3> corners = stackalloc Vector3[8];
            if (!KhaozEngine.Render3D.Internal.ShadowMapMath.FrustumCornersWorld(cam.ViewProjection, corners)) return -1;
            Vector3 eye = cam.Eye, fwd = cam.Forward;
            Vector3 nearC = (corners[0] + corners[1] + corners[2] + corners[3]) * 0.25f;
            Vector3 farC = (corners[4] + corners[5] + corners[6] + corners[7]) * 0.25f;
            float camNear = Vector3.Dot(nearC - eye, fwd);
            float camFar = Vector3.Dot(farC - eye, fwd);
            float range = MathF.Max(camFar - camNear, 1e-3f);

            int res = new ShadowSettings().ShadowMapResolution;
            Span<float> splits = stackalloc float[ShadowSettings.MaxCascades];
            Span<Matrix4x4> mats = stackalloc Matrix4x4[ShadowSettings.MaxCascades];
            KhaozEngine.Render3D.Internal.ShadowMapMath.FillCascadeSplits(splits, CascadeCount, ShadowNear, ShadowMax);
            float prev = camNear;
            for (int i = 0; i < CascadeCount; i++)
            {
                float d = Math.Clamp(splits[i], camNear, camFar);
                KhaozEngine.Render3D.Internal.ShadowMapMath.SliceBoundingSphere(corners,
                    (prev - camNear) / range, (d - camNear) / range, out Vector3 c, out float r);
                mats[i] = KhaozEngine.Render3D.Internal.ShadowMapMath.BuildLightViewProj(Light, c, r, res);
                prev = MathF.Max(d, prev);
            }
            return KhaozEngine.Render3D.Internal.ShadowMapMath.SelectCascade(mats, CascadeCount, Probes[4], 2f / res);
        }

        static float GroundLum(byte[] rgba, FlyCamera3D cam, Vector3 world)
        {
            if (!cam.WorldToScreen(world, W, H, out Vector2 p)) return 0f;
            return LumAt(rgba, p);
        }

        [GpuFact]
        public void Dissolve_zero_and_castsShadows_true_match_the_plain_draw()
        {
            // The policy overloads are inert at their defaults: same pipeline selection, same depth, same pixels.
            byte[] plain = Render((scene, _, caster) => scene.Draw(caster, CasterXform, CasterColor));
            byte[] viaOverload = Render((scene, _, caster) =>
                scene.Draw(caster, CasterXform, CasterColor, Material.None, dissolve: 0f, edgeWidth: 0f, edgeColor: default, castsShadows: true));
            Assert.Equal(plain, viaOverload);
        }
    }
}
