using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// GPU proof of the skinned half of the shadow-caster policy (issue #387), on the real cascaded depth pass and on
    /// BOTH skinning paths. Pixel-presence, NOT golden: the scene of <see cref="ShadowCasterPolicyGpuTests"/> (a
    /// floor and a shallow key light throwing a long shadow toward +z) with the box caster swapped for a skinned
    /// upright tube in its rest pose, rendered solid, half dissolved and opted out.
    /// <list type="bullet">
    /// <item>a half-dissolved skinned caster shadows PARTIALLY: before the fix the GPU-skinned depth vertex had no
    /// dissolve input and the CPU-skinned instance carried none, so a character mid-CharDissolve kept a fully solid
    /// shadow under an almost invisible body</item>
    /// <item><c>castsShadows: false</c> removes the shadow while the character still RENDERS</item>
    /// </list>
    /// Skipped unless KE_GPU_TESTS=1.
    /// </summary>
    public sealed class SkinnedShadowCasterPolicyGpuTests
    {
        readonly Xunit.Abstractions.ITestOutputHelper _out;
        public SkinnedShadowCasterPolicyGpuTests(Xunit.Abstractions.ITestOutputHelper o) => _out = o;

        const int W = 480, H = 320;

        static readonly Vector3 Light = Vector3.Normalize(new Vector3(0f, -0.5f, 0.87f));   // shallow: long +z shadow
        // An upright tube spanning y 0.4..2.6 and x, z -0.7..0.7, the footprint of the box caster it stands in for.
        static readonly Matrix4x4 TubeXform = Matrix4x4.CreateTranslation(0f, 0.4f, 0f);
        static readonly Vector3 CamTarget = new(0f, 0f, 5f);
        static readonly Vector3 CamExtent = new(14f, 6f, 14f);
        const float CamAz = 0.6f, CamEl = 0.95f;
        static readonly Color TubeTint = new(0.25f, 1f, 0.3f, 1f);

        static readonly Vector3[] Probes =
        {
            new(-0.5f, 0f, 2.0f), new(0f, 0f, 2.0f), new(0.5f, 0f, 2.0f),
            new(-0.5f, 0f, 2.4f), new(0f, 0f, 2.4f), new(0.5f, 0f, 2.4f),
            new(-0.5f, 0f, 2.8f), new(0f, 0f, 2.8f), new(0.5f, 0f, 2.8f),
        };
        static readonly Vector3 LitRef = new(-6f, 0f, -2f);

        static byte[] Render(bool gpuSkinning, Action<Scene3D, SkinnedMeshHandle, Matrix4x4[]> drawTube)
        {
            MeshHandle floor = default;
            SkinnedMeshHandle tube = default;
            SkinnedGltfMesh mesh = SkinnedMeshBuilder.BuildTube(0.7f, 2.2f, 8, 16, 4, Axis.Y);
            return Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    scene.UseGpuSkinning = gpuSkinning;
                    floor = scene.LoadMesh(MeshPrimitives.Tile(60f, 0.1f));
                    tube = scene.LoadSkinnedMesh(mesh);
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
                    drawTube(scene, tube, mesh.RestPose);
                },
                frames: 2);
        }

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
            return 0.299f * r / (255f * n) + 0.587f * g / (255f * n) + 0.114f * b / (255f * n);
        }

        // The tube is the only green thing in the scene, so its own pixels are countable: that is what proves an
        // opted-out caster still DRAWS rather than having been culled.
        static int TubePixels(byte[] rgba)
        {
            int n = 0;
            for (int i = 0; i < rgba.Length; i += 4)
                if (rgba[i + 1] > 90 && rgba[i + 1] > rgba[i] + 40 && rgba[i + 1] > rgba[i + 2] + 40) n++;
            return n;
        }

        [GpuTheory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_dissolving_skinned_caster_casts_a_partial_shadow_and_an_opted_out_one_casts_none(bool gpuSkinning)
        {
            byte[] solid = Render(gpuSkinning, (s, h, pose) => s.DrawSkinned(h, pose, TubeXform, TubeTint));
            byte[] fading = Render(gpuSkinning, (s, h, pose) =>
                s.DrawSkinned(h, pose, TubeXform, TubeTint, Material.None, dissolve: 0.5f, edgeWidth: 0f, edgeColor: default));
            byte[] none = Render(gpuSkinning, (s, h, pose) =>
                s.DrawSkinned(h, pose, TubeXform, TubeTint, Material.None, castsShadows: false));

            float solidRatio = ShadowRatio(solid);
            float fadingRatio = ShadowRatio(fading);
            float noneRatio = ShadowRatio(none);
            _out.WriteLine($"{(gpuSkinning ? "GPU" : "CPU")} skinning: solid {solidRatio:0.####} fading {fadingRatio:0.####} " +
                           $"none {noneRatio:0.####}, tube pixels solid {TubePixels(solid)} none {TubePixels(none)}");

            Assert.True(solidRatio < 0.8f,
                $"the probes are not in shadow (ratio {solidRatio:0.###}), so the scene or camera moved");

            // Opted out: the probed ground reads as lit ground, and the tube still draws.
            Assert.True(noneRatio > 0.97f, $"an opted-out skinned caster still darkened the ground (ratio {noneRatio:0.###})");
            int drawn = TubePixels(none);
            Assert.True(drawn > 0, "the opted-out skinned caster stopped rendering entirely");
            Assert.InRange(drawn, (int)(TubePixels(solid) * 0.9f), (int)(TubePixels(solid) * 1.1f));

            // Half dissolved: a real fraction of the solid caster's darkening survives, and a real fraction goes.
            // The same band ShadowCasterPolicyGpuTests holds the rigid dissolve to.
            float solidDarkening = noneRatio - solidRatio;
            float kept = solidDarkening > 1e-4f ? (noneRatio - fadingRatio) / solidDarkening : 0f;
            Assert.True(kept > 0.2f,
                $"a half-dissolved skinned caster kept only {kept:P0} of the solid caster's darkening " +
                $"(solid {solidRatio:0.###}, fading {fadingRatio:0.###}, none {noneRatio:0.###})");
            Assert.True(kept < 0.9f,
                $"a half-dissolved skinned caster kept {kept:P0} of the solid caster's darkening, so its shadow did not " +
                $"thin with its body (solid {solidRatio:0.###}, fading {fadingRatio:0.###}, none {noneRatio:0.###})");
        }

        [GpuFact]
        public void CastsShadows_true_and_dissolve_zero_match_the_plain_skinned_draw()
        {
            // The new overloads are inert at their defaults: same pipeline selection, same depth, same pixels.
            byte[] plain = Render(gpuSkinning: true, (s, h, pose) => s.DrawSkinned(h, pose, TubeXform, TubeTint));
            byte[] viaOverload = Render(gpuSkinning: true, (s, h, pose) =>
                s.DrawSkinned(h, pose, TubeXform, TubeTint, Material.None, 0f, 0f, default, castsShadows: true));
            Assert.Equal(plain, viaOverload);
        }
    }
}
