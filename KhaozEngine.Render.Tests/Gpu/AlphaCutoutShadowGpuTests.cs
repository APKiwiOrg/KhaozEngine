using System;
using System.Numerics;
using KhaozEngine.Imaging;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// GPU proof that a MASK caster's shadow respects its alpha cutout (issue #15). Pixel-presence, NOT golden: the
    /// scene of <see cref="ShadowCasterPolicyGpuTests"/> (a floor, a caster, a shallow key light throwing a long
    /// shadow toward +z), with the caster textured so that part of it is transparent. The shadowed ground behind each
    /// part is compared against open lit ground, so the thresholds are backend-agnostic and nothing is baked.
    /// <para>
    /// The light has no x component, so a probe strip on the ground at a given x is shadowed only by the caster at
    /// that x. The rays reaching the probes cross the box's two z faces and nothing else, and the box maps u across x
    /// on both, mirrored between them. The box's hole is therefore a CENTRE band of u, which is the same x band on
    /// both faces, so the centre strip lights while the outer strips stay dark. The cutout pipelines cull nothing, so
    /// both faces record: a hole on one side of u = 0.5 would be covered by the other face's opaque half. The box is
    /// 3 wide so its outer strips sit clear of both the band's and the box's own shadow penumbra. Before the fix the
    /// depth pass never sampled the albedo, so every strip stayed dark.
    /// </para>
    /// <para>
    /// A single-plane card, the shape a leaf card actually has, is set perpendicular to the light with either face
    /// toward it and cut on one side of u = 0.5, so exactly one outer strip lights. With the depth pass's front
    /// culling the card wrote nothing when turned with its glTF back face to the sun.
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
        static readonly Matrix4x4 BoxXform = Matrix4x4.CreateScale(3.0f, 2.2f, 1.4f) * Matrix4x4.CreateTranslation(0f, 1.5f, 0f);
        static readonly Vector3 CamTarget = new(0f, 0f, 5f);
        static readonly Vector3 CamExtent = new(14f, 6f, 14f);
        const float CamAz = 0.6f, CamEl = 0.95f;

        // Probe strips inside the caster's long shadow, and open lit ground. On the 1.4 wide card x = +/-0.5 is u = 0.14
        // or 0.86. On the 3 wide box the centre band is |x| < 0.75 and x = +/-1.05 is u = 0.15 or 0.85, 0.3 clear of it.
        static readonly Vector3[] LeftProbes = { new(-0.5f, 0f, 2.0f), new(-0.5f, 0f, 2.4f), new(-0.5f, 0f, 2.8f) };
        static readonly Vector3[] RightProbes = { new(0.5f, 0f, 2.0f), new(0.5f, 0f, 2.4f), new(0.5f, 0f, 2.8f) };
        static readonly Vector3[] BoxLeftProbes = { new(-1.05f, 0f, 2.0f), new(-1.05f, 0f, 2.4f), new(-1.05f, 0f, 2.8f) };
        static readonly Vector3[] CentreProbes = { new(0f, 0f, 2.0f), new(0f, 0f, 2.4f), new(0f, 0f, 2.8f) };
        static readonly Vector3[] BoxRightProbes = { new(1.05f, 0f, 2.0f), new(1.05f, 0f, 2.4f), new(1.05f, 0f, 2.8f) };
        static readonly Vector3 LitRef = new(-6f, 0f, -2f);

        /// <summary>Where the albedo is transparent: nowhere, on the u &lt; 0.5 half, or in the centre band of u.</summary>
        enum Hole { None, LeftHalf, CentreBand }

        // An 8x8 green albedo, alpha 0 in the columns the hole names and opaque everywhere else.
        static byte[] Albedo(Hole hole)
        {
            var px = new byte[TexN * TexN * 4];
            for (int y = 0; y < TexN; y++)
                for (int x = 0; x < TexN; x++)
                {
                    bool clear = hole switch
                    {
                        Hole.LeftHalf => x < TexN / 2,
                        Hole.CentreBand => x >= TexN / 4 && x < 3 * TexN / 4,
                        _ => false,
                    };
                    int i = (y * TexN + x) * 4;
                    px[i] = 50; px[i + 1] = 190; px[i + 2] = 60;
                    px[i + 3] = (byte)(clear ? 0 : 255);
                }
            return px;
        }

        // A 1.4 x 2.2 single-plane card at the box's centre, turned about x so its normal is +/- the light direction:
        // the XZ plane's +Y face points at the sun when facesLight, away from it otherwise. The plane's u runs along x
        // either way.
        static Matrix4x4 CardXform(bool facesLight)
            => Matrix4x4.CreateRotationX(facesLight ? -MathF.PI / 3f : 2f * MathF.PI / 3f)
             * Matrix4x4.CreateTranslation(0f, 1.5f, 0f);

        static byte[] RenderBox(Hole hole, float alphaCutoff) => Render(MeshPrimitives.Box(1f), BoxXform, hole, alphaCutoff);

        static byte[] Render(GltfMesh casterMesh, Matrix4x4 casterXform, Hole hole, float alphaCutoff)
        {
            MeshHandle floor = default, caster = default;
            return Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(60f, 0.1f));
                    Scene3D.TextureHandle tex = scene.LoadTexture(Albedo(hole), TexN, TexN);
                    caster = scene.LoadMesh(casterMesh, new Scene3D.SurfaceMaps(tex, alphaCutoff: alphaCutoff));
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
                    scene.Draw(caster, casterXform);
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
            byte[] opaque = RenderBox(Hole.CentreBand, alphaCutoff: 0f);     // the same texture, loaded OPAQUE: no clip
            byte[] mask = RenderBox(Hole.CentreBand, alphaCutoff: 0.5f);     // loaded MASK: the centre band is a hole

            float opaqueLeft = ShadowRatio(opaque, BoxLeftProbes), opaqueCentre = ShadowRatio(opaque, CentreProbes);
            float opaqueRight = ShadowRatio(opaque, BoxRightProbes);
            float maskLeft = ShadowRatio(mask, BoxLeftProbes), maskCentre = ShadowRatio(mask, CentreProbes);
            float maskRight = ShadowRatio(mask, BoxRightProbes);
            _out.WriteLine($"opaque: left {opaqueLeft:0.####} centre {opaqueCentre:0.####} right {opaqueRight:0.####}");
            _out.WriteLine($"mask:   left {maskLeft:0.####} centre {maskCentre:0.####} right {maskRight:0.####}");

            // Framing guard: the opaque load shadows every strip, or everything below is meaningless.
            Assert.True(opaqueLeft < 0.8f && opaqueCentre < 0.8f && opaqueRight < 0.8f,
                $"the probes are not in shadow (left {opaqueLeft:0.###}, centre {opaqueCentre:0.###}, right " +
                $"{opaqueRight:0.###}), so the scene or camera moved");

            Assert.True(maskCentre > 0.97f,
                $"the cutout hole still casts a shadow (centre strip reads {maskCentre:0.###} of lit ground): the depth " +
                "pass is not alpha-testing the MASK caster");
            Assert.True(maskLeft < 0.8f && maskRight < 0.8f,
                $"the opaque sides of the MASK caster stopped casting (left {maskLeft:0.###}, right {maskRight:0.###})");
            Assert.InRange(maskLeft, opaqueLeft - 0.05f, opaqueLeft + 0.05f);
            Assert.InRange(maskRight, opaqueRight - 0.05f, opaqueRight + 0.05f);
        }

        [GpuFact]
        public void A_mask_caster_with_an_opaque_texture_casts_the_same_shadow_as_an_opaque_load()
        {
            // The cutout pipeline records the same depth wherever the texture keeps the texel, so a MASK caster
            // whose albedo is opaque everywhere must shadow exactly like the OPAQUE load of the same mesh.
            byte[] opaque = RenderBox(Hole.None, alphaCutoff: 0f);
            byte[] mask = RenderBox(Hole.None, alphaCutoff: 0.5f);

            float opaqueLeft = ShadowRatio(opaque, LeftProbes), opaqueRight = ShadowRatio(opaque, RightProbes);
            float maskLeft = ShadowRatio(mask, LeftProbes), maskRight = ShadowRatio(mask, RightProbes);
            _out.WriteLine($"opaque: left {opaqueLeft:0.####} right {opaqueRight:0.####}");
            _out.WriteLine($"mask:   left {maskLeft:0.####} right {maskRight:0.####}");

            Assert.True(opaqueLeft < 0.8f && opaqueRight < 0.8f, "the probes are not in shadow, so the scene or camera moved");
            Assert.InRange(maskLeft, opaqueLeft - 0.02f, opaqueLeft + 0.02f);
            Assert.InRange(maskRight, opaqueRight - 0.02f, opaqueRight + 0.02f);

            // The whole frame too, the caster's own lit faces included. The cutout pipeline culls nothing, so a closed
            // MASK caster records its NEAR side where the opaque pipeline records the far one, and self-shadow acne on
            // the lit faces would show here first.
            var frame = GoldenGrid.Compare(GoldenGrid.Downsample(mask, W, H), GoldenGrid.Downsample(opaque, W, H));
            _out.WriteLine($"whole frame: worst cell {frame.WorstDiff:0.####}");
            Assert.True(frame.Passed, $"the MASK box's frame drifted from the OPAQUE load's (worst cell {frame.WorstDiff:0.###})");
        }

        [GpuTheory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_single_plane_card_casts_its_cutout_silhouette_whichever_face_points_at_the_light(bool facesLight)
        {
            // No OPAQUE reference here: the opaque depth pipeline culls front faces, so an opaque single-sided card
            // casts for one orientation only. The MASK card with a fully opaque texture is the reference instead.
            GltfMesh card = MeshPrimitives.Plane(1.4f, 2.2f);
            byte[] solid = Render(card, CardXform(facesLight), Hole.None, alphaCutoff: 0.5f);
            byte[] cut = Render(card, CardXform(facesLight), Hole.LeftHalf, alphaCutoff: 0.5f);

            float solidLeft = ShadowRatio(solid, LeftProbes), solidRight = ShadowRatio(solid, RightProbes);
            float cutLeft = ShadowRatio(cut, LeftProbes), cutRight = ShadowRatio(cut, RightProbes);
            _out.WriteLine($"card {(facesLight ? "facing" : "backing")} the light: solid left {solidLeft:0.####} right " +
                           $"{solidRight:0.####}, cut left {cutLeft:0.####} right {cutRight:0.####}");

            Assert.True(solidLeft < 0.8f && solidRight < 0.8f,
                $"the MASK card cast no shadow (left {solidLeft:0.###}, right {solidRight:0.###}): the cutout depth " +
                "pipeline culled the side turned toward the light, or the scene moved");
            // Which outer strip is the hole follows the plane's u direction under the turn, so it is read by value.
            float hole = MathF.Max(cutLeft, cutRight), kept = MathF.Min(cutLeft, cutRight);
            Assert.True(hole > 0.97f, $"the card's transparent half still casts (brighter strip reads {hole:0.###})");
            Assert.True(kept < 0.8f, $"the card's opaque half stopped casting (darker strip reads {kept:0.###})");
            Assert.InRange(kept, MathF.Min(solidLeft, solidRight) - 0.05f, MathF.Max(solidLeft, solidRight) + 0.05f);
        }
    }
}
