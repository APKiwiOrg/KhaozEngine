using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// GPU proof that a MASK caster's shadow respects its alpha cutout (issue #15). Pixel-presence, NOT golden: the
    /// scene of <see cref="ShadowCasterPolicyGpuTests"/> (a floor, a box caster, a shallow key light throwing a long
    /// shadow toward +z), with the caster textured so that one half of every face is transparent. The shadowed ground
    /// behind each half is compared against open lit ground, so the thresholds are backend-agnostic and nothing is
    /// baked.
    /// <para>
    /// The light has no x component, so the ground at x = -0.5 is shadowed only by the caster's x &lt; 0 half and the
    /// ground at x = +0.5 only by its x &gt; 0 half. The texture is transparent on one side of u = 0.5, and the box
    /// maps u across x on the faces that cast, so a cutout that reaches the depth pass lights exactly one of the two
    /// probe strips. Before the fix the depth pass never sampled the albedo, so both strips stayed dark.
    /// </para>
    /// Skipped unless KE_GPU_TESTS=1.
    /// </summary>
    public sealed class AlphaCutoutShadowGpuTests
    {
        readonly Xunit.Abstractions.ITestOutputHelper _out;
        public AlphaCutoutShadowGpuTests(Xunit.Abstractions.ITestOutputHelper o) => _out = o;

        const int W = 480, H = 320;
        const int TexN = 8;

        static readonly Vector3 Light = Vector3.Normalize(new Vector3(0f, -0.5f, 0.87f));   // shallow: long +z shadow
        static readonly Matrix4x4 CasterXform = Matrix4x4.CreateScale(1.4f, 2.2f, 1.4f) * Matrix4x4.CreateTranslation(0f, 1.5f, 0f);
        static readonly Vector3 CamTarget = new(0f, 0f, 5f);
        static readonly Vector3 CamExtent = new(14f, 6f, 14f);
        const float CamAz = 0.6f, CamEl = 0.95f;

        // Two probe strips inside the caster's long shadow, one under each half of it, and open lit ground.
        static readonly Vector3[] LeftProbes = { new(-0.5f, 0f, 2.0f), new(-0.5f, 0f, 2.4f), new(-0.5f, 0f, 2.8f) };
        static readonly Vector3[] RightProbes = { new(0.5f, 0f, 2.0f), new(0.5f, 0f, 2.4f), new(0.5f, 0f, 2.8f) };
        static readonly Vector3 LitRef = new(-6f, 0f, -2f);

        // An 8x8 green albedo whose alpha is 0 in the left half of the columns (u < 0.5) when halfCut is set, and
        // opaque everywhere otherwise.
        static byte[] Albedo(bool halfCut)
        {
            var px = new byte[TexN * TexN * 4];
            for (int y = 0; y < TexN; y++)
                for (int x = 0; x < TexN; x++)
                {
                    int i = (y * TexN + x) * 4;
                    px[i] = 50; px[i + 1] = 190; px[i + 2] = 60;
                    px[i + 3] = (byte)(halfCut && x < TexN / 2 ? 0 : 255);
                }
            return px;
        }

        static byte[] Render(bool halfCut, float alphaCutoff)
        {
            MeshHandle floor = default, caster = default;
            return Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(60f, 0.1f));
                    Scene3D.TextureHandle tex = scene.LoadTexture(Albedo(halfCut), TexN, TexN);
                    caster = scene.LoadMesh(MeshPrimitives.Box(1f), new Scene3D.SurfaceMaps(tex, alphaCutoff: alphaCutoff));
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
                    scene.Draw(caster, CasterXform);
                },
                frames: 2);
        }

        // Mean luminance over a probe strip as a fraction of the open lit ground: 1 is "as bright as lit" (no shadow)
        // and lower is more shadow.
        static float ShadowRatio(byte[] rgba, Vector3[] probes)
        {
            var cam = new IsoCamera3D { Azimuth = CamAz, Elevation = CamEl };
            cam.Frame(CamTarget, CamExtent);
            cam.AspectRatio = (float)W / H;
            float lit = GroundLum(rgba, cam, LitRef);
            if (lit <= 1e-3f) return 1f;
            float sum = 0f;
            foreach (Vector3 p in probes) sum += GroundLum(rgba, cam, p);
            return sum / probes.Length / lit;
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

        [GpuFact]
        public void A_cutout_hole_leaves_the_receiver_under_it_lit_while_the_rest_still_casts()
        {
            byte[] opaque = Render(halfCut: true, alphaCutoff: 0f);     // the same texture, loaded OPAQUE: no clip
            byte[] mask = Render(halfCut: true, alphaCutoff: 0.5f);     // loaded MASK: the left half is a hole

            float opaqueLeft = ShadowRatio(opaque, LeftProbes), opaqueRight = ShadowRatio(opaque, RightProbes);
            float maskLeft = ShadowRatio(mask, LeftProbes), maskRight = ShadowRatio(mask, RightProbes);
            _out.WriteLine($"opaque: left {opaqueLeft:0.####} right {opaqueRight:0.####}");
            _out.WriteLine($"mask:   left {maskLeft:0.####} right {maskRight:0.####}");

            // Framing guard: the opaque load shadows both strips, or everything below is meaningless.
            Assert.True(opaqueLeft < 0.8f && opaqueRight < 0.8f,
                $"the probes are not in shadow (left {opaqueLeft:0.###}, right {opaqueRight:0.###}), so the scene or camera moved");

            // The hole is lit and the solid half still casts. Which strip is the hole is fixed by the box's UV
            // layout on its casting faces, so it is asserted by value rather than assumed.
            float hole = MathF.Max(maskLeft, maskRight), solid = MathF.Min(maskLeft, maskRight);
            Assert.True(hole > 0.97f,
                $"the cutout hole still casts a shadow (brighter strip reads {hole:0.###} of lit ground): the depth pass " +
                "is not alpha-testing the MASK caster");
            Assert.True(solid < 0.8f,
                $"the opaque half of the MASK caster stopped casting (darker strip reads {solid:0.###})");
            Assert.InRange(solid, MathF.Min(opaqueLeft, opaqueRight) - 0.05f, MathF.Max(opaqueLeft, opaqueRight) + 0.05f);
        }

        [GpuFact]
        public void A_mask_caster_with_an_opaque_texture_casts_the_same_shadow_as_an_opaque_load()
        {
            // The cutout pipeline records the same depth wherever the texture keeps the texel, so a MASK caster
            // whose albedo is opaque everywhere must shadow exactly like the OPAQUE load of the same mesh.
            byte[] opaque = Render(halfCut: false, alphaCutoff: 0f);
            byte[] mask = Render(halfCut: false, alphaCutoff: 0.5f);

            float opaqueLeft = ShadowRatio(opaque, LeftProbes), opaqueRight = ShadowRatio(opaque, RightProbes);
            float maskLeft = ShadowRatio(mask, LeftProbes), maskRight = ShadowRatio(mask, RightProbes);
            _out.WriteLine($"opaque: left {opaqueLeft:0.####} right {opaqueRight:0.####}");
            _out.WriteLine($"mask:   left {maskLeft:0.####} right {maskRight:0.####}");

            Assert.True(opaqueLeft < 0.8f && opaqueRight < 0.8f, "the probes are not in shadow, so the scene or camera moved");
            Assert.InRange(maskLeft, opaqueLeft - 0.02f, opaqueLeft + 0.02f);
            Assert.InRange(maskRight, opaqueRight - 0.02f, opaqueRight + 0.02f);
        }
    }
}
