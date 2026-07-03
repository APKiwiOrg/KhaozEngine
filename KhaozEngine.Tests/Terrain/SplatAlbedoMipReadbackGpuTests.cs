using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Terrain
{
    // Validates Scene3D.DebugReadSplatAlbedoMip (the mip-chain readback used to diagnose the Windows/D3D11 ground
    // fuzz): on a correct backend, a high mip of the splat albedo array is a real blurred downsample - its average
    // colour matches mip 0 (a box filter preserves the mean) and it has much less local detail. A broken GPU mip
    // generation shows up as a high mip that is empty (near-black, mean way off) or a copy of mip 0 (still detailed).
    // Not "Golden"-named on purpose: runs on the local Metal device only, not the CI backend legs (the real
    // Windows/D3D11 answer comes from the game's in-app mip self-test on the tester's actual GPU).
    public sealed class SplatAlbedoMipReadbackGpuTests
    {
        readonly ITestOutputHelper _out;
        public SplatAlbedoMipReadbackGpuTests(ITestOutputHelper o) => _out = o;

        [GpuFact]
        public void HighMipOfSplatAlbedoIsARealBlurredDownsample()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            using var preview = new Render3DPreview(ctx.GpuDevice, 64, 64);

            var mat = preview.Scene.LoadTerrainMaterial(TerrainMaterialPresets.Procedural(256)); // mips 0..8
            const int highMip = 4;   // 256 >> 4 = 16 px

            byte[] mip0 = preview.Scene.DebugReadSplatAlbedoMip(mat, 0, arrayLayer: 0, out int w0, out int h0);
            byte[] mipH = preview.Scene.DebugReadSplatAlbedoMip(mat, highMip, arrayLayer: 0, out int wH, out int hH);

            Assert.Equal(256, w0); Assert.Equal(256, h0);
            Assert.Equal(16, wH); Assert.Equal(16, hH);

            var (mr0, mg0, mb0, det0) = Stats(mip0, w0, h0);
            var (mrH, mgH, mbH, detH) = Stats(mipH, wH, hH);
            int meanDelta = Math.Abs(mr0 - mrH) + Math.Abs(mg0 - mgH) + Math.Abs(mb0 - mbH);
            string msg = $"mip0 mean=({mr0},{mg0},{mb0}) detail={det0:F1} | mip{highMip} mean=({mrH},{mgH},{mbH}) detail={detH:F1} | meanDelta={meanDelta}";
            _out.WriteLine(msg);

            // Real downsample: mean colour is preserved (generous bound catches an empty/garbage high mip) and the
            // high mip is markedly smoother than mip 0 (catches a mip that is just a copy of mip 0).
            Assert.True(meanDelta < 45, $"high mip average colour drifted from mip 0 (empty/garbage mip?). {msg}");
            Assert.True(detH < det0 * 0.6f, $"high mip is not smoother than mip 0 (mips not downsampling?). {msg}");
        }

        // Mean R/G/B (0..255) + mean absolute adjacent-pixel luma difference (a "local detail" measure).
        static (int mr, int mg, int mb, float detail) Stats(byte[] rgba, int w, int h)
        {
            long sr = 0, sg = 0, sb = 0;
            int n = w * h;
            for (int i = 0; i < n; i++) { sr += rgba[i * 4]; sg += rgba[i * 4 + 1]; sb += rgba[i * 4 + 2]; }
            static float Luma(byte[] p, int i) => 0.299f * p[i * 4] + 0.587f * p[i * 4 + 1] + 0.114f * p[i * 4 + 2];
            double e = 0; int pairs = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (x + 1 < w) { e += Math.Abs(Luma(rgba, i) - Luma(rgba, i + 1)); pairs++; }
                    if (y + 1 < h) { e += Math.Abs(Luma(rgba, i) - Luma(rgba, i + w)); pairs++; }
                }
            return ((int)(sr / n), (int)(sg / n), (int)(sb / n), pairs > 0 ? (float)(e / pairs) : 0f);
        }
    }
}
