using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    // Regression net for the splat-terrain RENDER path (not just "does it throw"). The 7.64.0 splat material put a
    // second uniform buffer (per-material params) in the pipeline; Veldrid/SPIRV-Cross on Metal mis-binds a second
    // UBO so the per-layer tint read garbage and the terrain rendered ~black / flat primary colours. The fix folds
    // the params into the single frame UBO. This test renders a procedurally-textured chunk top-down and asserts the
    // ground is lit and looks textured, so a regression to the black/primary output fails here.
    public sealed class TexturedTerrainRenderGpuTests
    {
        [GpuFact]
        public void TexturedTerrainRendersLitAndTextured()
        {
            const int W = 64, H = 64;
            var field = new TerrainField(TerrainPresets.Clearing());
            var region = new TerrainChunkRegion { OriginX = 0f, OriginZ = 0f, Size = 32f };
            var chunk = TerrainChunkBuilder.Build(field, region, lod: 0);

            MeshHandle h = default;
            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    var mat = scene.LoadTerrainMaterial(TerrainMaterialPresets.Procedural(32));
                    h = scene.LoadTerrainChunk(chunk, mat);
                    scene.Camera.Frame(new Vector3(16f, 1f, 16f), new Vector3(16f, 26f, 16.4f));
                },
                drawFrame: scene => scene.DrawTerrainChunk(h, region));

            int Idx(int x, int y) => (y * W + x) * 4;
            // Centre pixels are terrain (top-down at the chunk centre). The clearing is grass/dirt dominant, so the
            // ground reads olive: lit (not the black-albedo bug) and multi-channel (not a raw single-weight primary).
            byte r = rgba[Idx(W / 2, H / 2)], g = rgba[Idx(W / 2, H / 2) + 1], b = rgba[Idx(W / 2, H / 2) + 2];
            Assert.True(g > 40, $"terrain centre too dark (G={g}); splat albedo/tint not sampled (the second-UBO bug)");
            Assert.True(r > 20 && b > 20, $"terrain centre is a raw weight primary (r={r} g={g} b={b}); not textured");
        }
    }
}
