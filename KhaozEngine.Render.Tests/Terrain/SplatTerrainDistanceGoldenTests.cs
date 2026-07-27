using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Terrain
{
    // Distance/grazing coverage for the splat terrain pass, and the per-backend home of the "ground fuzzes at
    // distance when the camera moves" investigation (reported on Windows/D3D11, not macOS/Metal).
    //
    // The pre-existing SplatTerrainGoldenTests frames the ground with the ORTHOGRAPHIC iso camera nearly top-down,
    // so it has no perspective minification and structurally cannot exhibit distance shimmer. This renders the SAME
    // splat pipeline through a PERSPECTIVE follow camera at a low grazing pitch over a high-frequency checkerboard
    // albedo (tiled ~0.2/m, matching a real overworld), where the distant ground is minified to many texels per
    // pixel and MUST be mip/aniso filtered. If the LOD selection is broken (mip 0 wins at distance) the far checker
    // aliases at near-full contrast; if it is filtered it converges to a flat mid grey.
    //
    // Named "...Golden..." on purpose: cross-platform-gpu CI runs `--filter FullyQualifiedName~Golden` on each
    // backend (Metal / D3D11-WARP / Vulkan-lavapipe), so this is the terrain path's first per-backend DISTANCE
    // check. The hard assertions are deliberately backend-robust (the distant ground must be textured + lit, not
    // white / black / background) so a correctly-filtering backend passes with margin and this never false-reds on
    // WARP. The near/far high-frequency energy and the checker contrast are LOGGED (embedded in the assertion
    // message, visible per backend in the CI log without extra verbosity) so the exact D3D11 aliasing behaviour is
    // observable for the first time. A tight aliasing gate is intentionally NOT asserted here: it can only be
    // calibrated from real per-backend numbers, which this test is what surfaces.
    public sealed class SplatTerrainDistanceGoldenTests
    {
        readonly ITestOutputHelper _out;
        public SplatTerrainDistanceGoldenTests(ITestOutputHelper o) => _out = o;

        [GpuFact]
        public void SplatTerrainDistanceGoldenIsTexturedAndLitAtGrazingDistance()
        {
            const int W = 160, H = 120;

            var field = new TerrainField(TerrainPresets.Clearing());
            var material = Checkerboard(size: 64, cell: 4, tilesPerMetre: 0.2f);

            // A strip of chunks receding along +Z so the ground fills from the foreground to the horizon.
            const float size = TerrainChunkRegion.DefaultSize;   // 60 m
            var regions = new List<TerrainChunkRegion>();
            for (int cz = 0; cz < 5; cz++)          // z origins 0..240 -> ground out to ~300 m
                for (int cx = -1; cx <= 1; cx++)    // x origins -60,0,60 -> fills the frame width at grazing
                    regions.Add(new TerrainChunkRegion { OriginX = cx * size, OriginZ = cz * size, Size = size });

            var handles = new List<MeshHandle>();
            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    var mat = scene.LoadTerrainMaterial(material);
                    foreach (var r in regions)
                    {
                        var chunk = TerrainChunkBuilder.Build(field, r, lod: 1);
                        handles.Add(scene.LoadTerrainChunk(chunk, mat));
                    }

                    // Perspective grazing camera: low eye, shallow pitch, looking down +Z across the receding strip.
                    // Yaw = PI points DirToEye toward -Z, so the eye sits behind the target and looks into +Z.
                    var cam = new FollowCamera3D
                    {
                        Target = new Vector3(0f, 0.5f, 10f),
                        Yaw = MathF.PI,
                        AspectRatio = (float)W / H,
                        FarPlane = 800f,
                        MaxDistance = 30f,
                        HeightOffset = 1.2f,
                    };
                    cam.Distance = 28f;
                    cam.Pitch = cam.MinPitch;   // ~6 deg above horizontal: a grazing look across the ground
                    scene.CameraOverride = cam;
                },
                // Handles are appended in region order, so index i places chunk i (vertices are chunk-local).
                drawFrame: scene => { for (int i = 0; i < handles.Count; i++) scene.DrawTerrainChunk(handles[i], regions[i]); });

            // ---- Analyse the frame in two horizontal bands: a NEAR band (magnified foreground, big smooth checker
            //      cells -> low local energy) and a FAR band (minified distant ground just below the horizon). A
            //      working mip/aniso chain blurs the far band toward a flat grey (low energy); a broken/mip-0
            //      selection leaves it aliasing near full contrast (high energy).
            (int a, int b, int c) At(int x, int y) { int i = (y * W + x) * 4; return (rgba[i], rgba[i + 1], rgba[i + 2]); }
            static bool Background(int r, int g, int b) => r < 30 && g < 30 && b < 30;
            static bool NearWhite(int r, int g, int b) => r >= 235 && g >= 235 && b >= 235;
            static float Luma(int r, int g, int b) => 0.299f * r + 0.587f * g + 0.114f * b;

            int GroundInRow(int y) { int n = 0; for (int x = 0; x < W; x++) { var (r, g, b) = At(x, y); if (!Background(r, g, b)) n++; } return n; }

            // Locate the ground region and the horizon boundary WITHOUT assuming a vertical orientation (the readback
            // may be top-up or bottom-up). The ground occupies a contiguous run of substantially-ground rows; one end
            // borders the void beyond the horizon (the DISTANT ground), the other is the magnified foreground.
            int[] rowGround = new int[H];
            for (int y = 0; y < H; y++) rowGround[y] = GroundInRow(y);
            int gLo = -1, gHi = -1;
            for (int y = 0; y < H; y++) if (rowGround[y] >= W / 3) { if (gLo < 0) gLo = y; gHi = y; }
            Assert.True(gLo >= 0, "no substantially-ground row found: the grazing camera did not frame the terrain strip.");

            // The far (horizon) end is the ground-region end whose neighbour row has LESS ground (borders the void).
            // A frame edge counts as fully ground (W), so it is never mistaken for the horizon.
            int neighbourBelow = gHi + 1 < H ? rowGround[gHi + 1] : W;
            int neighbourAbove = gLo - 1 >= 0 ? rowGround[gLo - 1] : W;
            bool farIsHigh = neighbourBelow < neighbourAbove;   // horizon void is just past gHi

            int farTop, farBot, nearTop, nearBot;
            if (farIsHigh) { farBot = gHi + 1; farTop = Math.Max(gLo, gHi - 15); nearTop = gLo; nearBot = Math.Min(gHi + 1, gLo + 22); }
            else { farTop = gLo; farBot = Math.Min(gHi + 1, gLo + 16); nearBot = gHi + 1; nearTop = Math.Max(gLo, gHi - 21); }

            (float energy, float luma, int count, int nearWhite) Band(int y0, int y1)
            {
                double eSum = 0, lSum = 0; int pairs = 0, count = 0, white = 0;
                for (int y = y0; y < y1; y++)
                    for (int x = 0; x < W; x++)
                    {
                        var (r, g, b) = At(x, y);
                        if (Background(r, g, b)) continue;
                        count++;
                        lSum += Luma(r, g, b);
                        if (NearWhite(r, g, b)) white++;
                        // Local high-frequency energy vs the right + down neighbours (when they are also ground).
                        if (x + 1 < W) { var (r2, g2, b2) = At(x + 1, y); if (!Background(r2, g2, b2)) { eSum += Math.Abs(Luma(r, g, b) - Luma(r2, g2, b2)); pairs++; } }
                        if (y + 1 < y1) { var (r3, g3, b3) = At(x, y + 1); if (!Background(r3, g3, b3)) { eSum += Math.Abs(Luma(r, g, b) - Luma(r3, g3, b3)); pairs++; } }
                    }
                return (pairs > 0 ? (float)(eSum / pairs) : 0f, count > 0 ? (float)(lSum / count) : 0f, count, white);
            }

            var far = Band(farTop, farBot);
            var near = Band(nearTop, nearBot);

            // Full contrast between the two checker tones (the ceiling the far band would approach if it aliased raw).
            var (ca, cb) = CheckerLumas();
            float contrast = Math.Abs(ca - cb);

            string msg =
                $"ground rows[{gLo},{gHi}] farIsHigh={farIsHigh} | FAR band rows[{farTop},{farBot}) ground={far.count} luma={far.luma:F1} energy={far.energy:F1} nearWhite={far.nearWhite} | " +
                $"NEAR band rows[{nearTop},{nearBot}) ground={near.count} luma={near.luma:F1} energy={near.energy:F1} | checkerContrast={contrast:F1} farEnergy/contrast={(contrast > 0 ? far.energy / contrast : 0):F2}";
            _out.WriteLine(msg);

            // Backend-robust hard assertions (the coverage-gap closure): the DISTANT ground must actually render as a
            // lit texture, not vanish, not blow out to white, not stay background. These catch a gross distance
            // regression on any backend without depending on a calibrated aliasing threshold.
            Assert.True(far.count >= 300, $"far band shows too little ground ({far.count}px): camera framing or terrain load is wrong. {msg}");
            // A genuine white-out (the D3D11 interpolant-gap class of bug) blows the whole band near-white; a few
            // specular texels are fine, so gate on a fraction rather than zero.
            Assert.True(far.nearWhite < far.count / 20, $"distant terrain is mostly near-white (splat material not rendered at distance). {msg}");
            Assert.InRange(far.luma, 25f, 225f);   // lit mid-tone, neither black nor blown out
            Assert.True(near.count >= 300, $"near band shows too little ground ({near.count}px). {msg}");
        }

        // Two contrasting mid-tones for the checker: both clearly non-background and non-white, so the robust
        // textured/lit assertions have headroom while the pattern is maximally high-frequency for the energy log.
        static readonly Color CheckerA = new Color(60 / 255f, 90 / 255f, 40 / 255f);
        static readonly Color CheckerB = new Color(180 / 255f, 170 / 255f, 120 / 255f);

        static (float a, float b) CheckerLumas()
        {
            static float L(Color c) => 0.299f * (c.R * 255f) + 0.587f * (c.G * 255f) + 0.114f * (c.B * 255f);
            return (L(CheckerA), L(CheckerB));
        }

        // A five-layer material whose every layer is the same high-frequency checkerboard albedo, so the rendered
        // ground is the checker regardless of the per-vertex splat blend. Flat tangent-space normal per texel.
        static TerrainLayeredMaterial Checkerboard(int size, int cell, float tilesPerMetre)
        {
            byte[] albedo = new byte[size * size * 4];
            byte[] normal = new byte[size * size * 4];
            static byte U(float f) => (byte)Math.Clamp((int)(f * 255f + 0.5f), 0, 255);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int i = (y * size + x) * 4;
                    bool onA = (((x / cell) + (y / cell)) & 1) == 0;
                    Color c = onA ? CheckerA : CheckerB;
                    albedo[i + 0] = U(c.R); albedo[i + 1] = U(c.G); albedo[i + 2] = U(c.B); albedo[i + 3] = 255;
                    normal[i + 0] = 128; normal[i + 1] = 128; normal[i + 2] = 255; normal[i + 3] = 255;   // tangent-space up
                }

            TerrainMaterialLayer Layer() => new()
            {
                AlbedoRgba = (byte[])albedo.Clone(),
                NormalRgba = (byte[])normal.Clone(),
                Tint = Color.White,
                TilesPerMetre = tilesPerMetre,
                Roughness = 0.9f,
            };

            return new TerrainLayeredMaterial
            {
                Width = size, Height = size,
                Grass = Layer(), Dirt = Layer(), Rock = Layer(), Sand = Layer(), Snow = Layer(),
            };
        }
    }
}
