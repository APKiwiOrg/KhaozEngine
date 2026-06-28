using System.Numerics;
using System.Text;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Terrain
{
    // Regression guard for the D3D11/WARP "white terrain" bug (fixed by making SplatFrag's pixel-input interpolants
    // contiguous - a gap at TEXCOORD4 from the declared-but-unused vUv corrupted the highest live interpolant
    // vEmissive on FXC/WARP and blew the whole terrain to flat white, while Metal/Vulkan tolerated it). The class is
    // named "...Golden..." on purpose: cross-platform-gpu CI runs `--filter FullyQualifiedName~Golden` on each
    // backend (Metal / D3D11-WARP / Vulkan-lavapipe), so this executes - and would have caught - the regression per
    // backend. (The pre-existing splat tests had no "Golden" in their name, which is why D3D11 never exercised them.)
    //
    // The procedural Clearing terrain is grass/dirt dominant, so a correctly textured ground reads OLIVE (a tinted
    // colour with channel spread ~55 on Metal). The bug rendered it pure WHITE on D3D11. This samples a small grid,
    // embeds every sample in the assertion message (visible in the CI log on failure without detailed verbosity),
    // and asserts the ground is textured (clear channel spread) and NOT near-white.
    public sealed class SplatTerrainGoldenTests
    {
        readonly ITestOutputHelper _out;
        public SplatTerrainGoldenTests(ITestOutputHelper o) => _out = o;

        [GpuFact]
        public void SplatTerrainGoldenIsTexturedNotWhite()
        {
            const int W = 96, H = 96;
            var field = new TerrainField(TerrainPresets.Clearing());
            var chunk = TerrainChunkBuilder.Build(field, new TerrainChunkRegion { OriginX = 0f, OriginZ = 0f, Size = 32f }, lod: 0);

            MeshHandle h = default;
            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    var mat = scene.LoadTerrainMaterial(TerrainMaterialPresets.Procedural(32));
                    h = scene.LoadTerrainChunk(chunk, mat);
                    scene.Camera.Frame(new Vector3(16f, 1f, 16f), new Vector3(16f, 26f, 16.4f));
                },
                drawFrame: scene => scene.DrawTerrainChunk(h));

            int Idx(int x, int y) => (y * W + x) * 4;
            (byte r, byte g, byte b) At(int x, int y) { int i = Idx(x, y); return (rgba[i], rgba[i + 1], rgba[i + 2]); }

            // Sample a 5x5 grid over the central terrain region. The corner/edge cells may be background.
            var grid = new StringBuilder("splat grid (r,g,b): ");
            int nearWhite = 0, lit = 0, samples = 0;
            for (int gy = 0; gy < 5; gy++)
                for (int gx = 0; gx < 5; gx++)
                {
                    int px = W / 4 + gx * (W / 2) / 4;
                    int py = H / 4 + gy * (H / 2) / 4;
                    var (r, g, b) = At(px, py);
                    grid.Append($"{r},{g},{b}|");
                    samples++;
                    bool background = r < 30 && g < 30 && b < 30;
                    if (!background) lit++;
                    if (r >= 235 && g >= 235 && b >= 235) nearWhite++;
                }

            var (cr, cg, cb) = At(W / 2, H / 2);
            int spread = System.Math.Max(cr, System.Math.Max(cg, cb)) - System.Math.Min(cr, System.Math.Min(cg, cb));
            string msg = $"centre=({cr},{cg},{cb}) spread={spread} nearWhite={nearWhite}/{samples} lit={lit}/{samples}. {grid}";
            _out.WriteLine(msg);

            // White ground means the splat material is not being sampled/lit correctly (the D3D11 interpolant-gap bug);
            // white/grey has a near-zero channel spread. Olive grass/dirt has a clear spread, well below 235 per channel.
            Assert.True(nearWhite == 0, $"terrain is near-white (splat material not rendered). {msg}");
            Assert.True(spread >= 15, $"terrain centre is flat grey/white, not a tinted texture. {msg}");
        }
    }
}
