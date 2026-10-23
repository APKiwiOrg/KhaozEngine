using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>GPU colour and shadow coverage of the real cluster renderer with identical individual and merged
    /// panel geometry. Every interior pixel and caster texel must survive throughout the HLOD band (#391).
    /// Skipped unless KE_GPU_TESTS=1.</summary>
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

            GltfMesh mesh = MeshPrimitives.Tile(24f, 0.05f);
            MeshHandle panel = scene.LoadMesh(mesh);
            var placements = new[] { new PropPlacement("panel", 0f, 6f, 0f, 1f, 0f, 0) };
            PropLayer layer = PropLayer.PlacementLayer(placements,
                    new Dictionary<string, MeshHandle> { ["panel"] = panel }, 400f)
                .WithHlod(new Dictionary<string, GltfMesh> { ["panel"] = mesh }, 100f, 0f, 40f);
            using var renderer = new PropClusterRenderer(scene);
            PropClusterKey key = new("panel", 0, 0, 0);
            renderer.Apply(key, renderer.BuildCpu(new PropClusterBuildRequest(key, 1,
                new RectArea(-12f, -12f, 12f, 12f), layer, placements)));
            scene.Post.BackgroundColor = Color.Black;
            scene.Post.AmbientColor = new Color(.9f, .9f, .9f, 1f);
            scene.Post.Starfield = false;
            scene.Post.Outline = false;
            scene.Post.LightDirection = Vector3.Normalize(new Vector3(-0.15f, -0.97f, 0.2f));
            scene.Camera.Frame(new Vector3(0f, 0f, 0f), new Vector3(120f, 40f, 120f));

            // Control: the panel with no dissolve at all, the full-coverage reference both ends must reach.
            preview.Capture(s => s.Draw(panel, CasterXform, Color.White));
            byte[] solidColour = preview.ReadbackRgba();
            int solid = CasterTexels(scene.DebugReadShadowMap(out int aw, out int ah), aw, ah);
            _out.WriteLine($"solid caster texels = {solid} (atlas {aw}x{ah})");
            Assert.True(solid > 5000, $"the caster records only {solid} atlas texels; re-frame the scene");

            for (int i = 0; i < ts.Length; i++)
            {
                float t = ts[i];
                preview.Capture(_ => renderer.Draw(new Vector3(80f + t * 40f, 0f, 0f)));
                AssertNoInteriorBackground(solidColour, preview.ReadbackRgba(), t);
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

        static void AssertNoInteriorBackground(byte[] solid, byte[] crossfade, float t)
        {
            int interior = 0, missing = 0;
            for (int y = 1; y < H - 1; y++)
                for (int x = 1; x < W - 1; x++)
                {
                    bool inside = true;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                            inside &= Covered(solid, ((y + dy) * W + x + dx) * 4);
                    if (!inside) continue;
                    interior++;
                    if (!Covered(crossfade, (y * W + x) * 4)) missing++;
                }
            Assert.True(interior > 100, $"solid panel has only {interior} interior pixels");
            Assert.True(missing == 0, $"t={t}: {missing} background pixels inside the solid panel");
        }

        static bool Covered(byte[] rgba, int i) => rgba[i] > 30 || rgba[i + 1] > 30 || rgba[i + 2] > 30;

        // Count texels holding a caster depth: the atlas clears to 1.0 (far plane = no caster).
        static int CasterTexels(float[] depth, int width, int height)
        {
            int n = 0;
            for (int i = 0; i < width * height; i++) if (depth[i] < 0.999f) n++;
            return n;
        }
    }
}
