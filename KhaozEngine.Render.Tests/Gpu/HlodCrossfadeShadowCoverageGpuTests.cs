using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// GPU coverage of the HLOD crossfade's two SHADOW halves (issue #391), measured on the key light's depth atlas
    /// rather than on screen. The atlas IS the quantity that was wrong: how much of a caster the two halves record
    /// between them. A ground-luminance probe cannot isolate it, because the halves are different geometry and their
    /// COLOUR dithers also move across the band, so several things change at once.
    /// <para>
    /// The scene is the crossfade reduced to its essentials: ONE flat caster panel drawn TWICE at the same transform,
    /// once as the fading props half (dissolve t, plain dither) and once as the arriving merged half (dissolve 1 - t,
    /// inverted dither), which is exactly what <c>Scene3DChunkSink</c> queues across the band once the two
    /// representations have converged on the same geometry. Drawing them at the same transform is what makes the test
    /// decisive: any positional difference decorrelates the two masks and hides the defect behind chance coverage.
    /// </para>
    /// <para>
    /// Before the fix both halves discarded <c>mask &lt; threshold</c>, at t and 1 - t. Those keep-sets NEST (for
    /// t &lt; 0.5 one contains the other), so their union is the larger of the two and bottoms out at half the mask at
    /// band centre, while both ends of the band record the caster whole. The merged half now inverts its test, so the
    /// union is the whole mask at every t.
    /// </para>
    /// Skipped unless KE_GPU_TESTS=1.
    /// </summary>
    public sealed class HlodCrossfadeShadowCoverageGpuTests
    {
        readonly ITestOutputHelper _out;
        public HlodCrossfadeShadowCoverageGpuTests(ITestOutputHelper o) => _out = o;

        const int W = 256, H = 256;
        const int Cascades = 4;

        // A flat panel, so the depth pass records exactly ONE fragment per shadow texel. A closed solid would put
        // several back faces on the same texel, each rolling the dither independently, which hides a coverage loss
        // behind the survival of any one of them.
        static readonly Matrix4x4 CasterXform = Matrix4x4.CreateTranslation(0f, 6f, 0f);

        static ShadowSettings Settings() => new()
        {
            Mode = ShadowMode.ShadowMap,
            ShadowCascadeCount = Cascades,
            ShadowNearDistance = 16f,
            ShadowMaxDistance = 250f,
        };

        [GpuFact]
        public void Crossfade_halves_cover_the_caster_between_them_across_the_band()
        {
            float[] ts = { 0f, 0.25f, 0.5f, 0.75f, 1f };
            var coverage = new int[ts.Length];

            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H, Settings());
            Scene3D scene = preview.Scene;

            MeshHandle panel = scene.LoadMesh(MeshPrimitives.Tile(24f, 0.05f));
            scene.Post.Starfield = false;
            scene.Post.Outline = false;
            scene.Post.LightDirection = Vector3.Normalize(new Vector3(-0.15f, -0.97f, 0.2f));
            scene.Camera.Frame(new Vector3(0f, 0f, 0f), new Vector3(120f, 40f, 120f));

            // Control: the panel with no dissolve at all, the full-coverage reference both ends must reach.
            preview.Capture(s => s.Draw(panel, CasterXform, Color.White));
            int solid = CasterTexels(scene.DebugReadShadowMap(out int aw, out int ah), aw, ah);
            _out.WriteLine($"solid caster texels = {solid} (atlas {aw}x{ah})");
            Assert.True(solid > 5000, $"the caster records only {solid} atlas texels; re-frame the scene");

            for (int i = 0; i < ts.Length; i++)
            {
                float t = ts[i];
                preview.Capture(s =>
                {
                    // The props half: fades OUT on t, plain dither (keeps mask >= t).
                    if (t < 1f) s.Draw(panel, CasterXform, Color.White, Material.None, t, 0f, default);
                    // The merged half: fades IN on 1 - t, INVERTED dither (keeps mask < t, the exact complement).
                    if (t > 0f) s.Draw(panel, CasterXform, Color.White, Material.None, 1f - t, 0f, default,
                        castsShadows: true, invertShadowDissolve: true);
                });
                coverage[i] = CasterTexels(scene.DebugReadShadowMap(out _, out _), aw, ah);
                _out.WriteLine($"t={t:0.00} union caster texels = {coverage[i]} ({coverage[i] / (float)solid:P0} of solid)");
            }

            // Both ends are a single un-dissolved half, so they must reach the solid reference. That is the guard
            // that makes the middle meaningful.
            Assert.True(coverage[0] >= solid * 0.98f, $"t=0 recorded {coverage[0]} of {solid}");
            Assert.True(coverage[ts.Length - 1] >= solid * 0.98f, $"t=1 recorded {coverage[ts.Length - 1]} of {solid}");

            for (int i = 1; i < ts.Length - 1; i++)
                Assert.True(coverage[i] >= solid * 0.95f,
                    $"at t={ts[i]:0.00} the two crossfade halves recorded {coverage[i]} of the caster's {solid} atlas " +
                    $"texels ({coverage[i] / (float)solid:P0}). Their dithers are not complementary, so the union " +
                    "thins in the middle of the band and the shadow visibly lightens there.");
        }

        // Count texels holding a caster depth: the atlas clears to 1.0 (far plane = no caster).
        static int CasterTexels(float[] depth, int width, int height)
        {
            int n = 0;
            for (int i = 0; i < width * height; i++) if (depth[i] < 0.999f) n++;
            return n;
        }
    }
}
