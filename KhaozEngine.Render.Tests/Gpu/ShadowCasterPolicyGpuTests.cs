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
    /// fixes was a fading prop casting a fully solid shadow right up to its cull radius</item>
    /// <item>a zero dissolve through the new overload matches the plain draw (the gated path is inert)</item>
    /// </list>
    /// Skipped unless KE_GPU_TESTS=1.
    /// </summary>
    public sealed class ShadowCasterPolicyGpuTests
    {
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

        [GpuFact]
        public void A_dissolving_caster_casts_a_partial_shadow()
        {
            byte[] solid = Render((scene, _, caster) => scene.Draw(caster, CasterXform, CasterColor));
            byte[] fading = Render((scene, _, caster) =>
                scene.Draw(caster, CasterXform, CasterColor, Material.None, dissolve: 0.5f, edgeWidth: 0f, edgeColor: default));
            byte[] none = Render((scene, _, caster) =>
                scene.Draw(caster, CasterXform, CasterColor, Material.None, castsShadows: false));

            float solidRatio = ShadowRatio(solid);
            float fadingRatio = ShadowRatio(fading);
            float noneRatio = ShadowRatio(none);

            Assert.True(solidRatio < 0.8f, $"the probes are not in shadow (ratio {solidRatio:0.###}), so the scene or camera moved");
            // Half the noise is above the 0.5 threshold, so roughly half the caster's depth survives: the shadow must
            // land clearly between "solid" and "none". Generous margins - the point is the ordering, not a bake.
            Assert.True(fadingRatio > solidRatio + 0.05f,
                $"a half-dissolved caster still cast an (almost) solid shadow: solid {solidRatio:0.###}, fading {fadingRatio:0.###}");
            Assert.True(fadingRatio < noneRatio - 0.05f,
                $"a half-dissolved caster cast (almost) no shadow: fading {fadingRatio:0.###}, none {noneRatio:0.###}");
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
