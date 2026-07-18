using System;
using System.IO;
using System.Linq;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Headless ([Fact], no GPU device) tests for the evidence-PNG side of <see cref="GoldenCompare"/>. They drive
    /// the internal <c>AssertOrUpdate</c> overload with throwaway temp directories and an explicit backend name, so
    /// they never touch the committed goldens dir and never mutate process-wide env vars.
    /// </summary>
    public sealed class GoldenEvidenceTests : IDisposable
    {
        const string Backend = "testbackend";
        readonly string _root;
        readonly string _goldenDir;
        readonly string _evidenceDir;

        public GoldenEvidenceTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ke-golden-evidence-" + Guid.NewGuid().ToString("N"));
            _goldenDir = Path.Combine(_root, "goldens");
            _evidenceDir = Path.Combine(_root, "evidence");
            Directory.CreateDirectory(_goldenDir);
            Directory.CreateDirectory(_evidenceDir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }

        // A solid w x h RGBA buffer of one color.
        static byte[] Solid(int w, int h, byte r, byte g, byte b)
        {
            var px = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                px[i * 4] = r; px[i * 4 + 1] = g; px[i * 4 + 2] = b; px[i * 4 + 3] = 255;
            }
            return px;
        }

        // Bake a golden text for a solid color by running the internal overload in bake mode.
        void BakeSolidGolden(string name, int w, int h, byte r, byte g, byte b)
        {
            GoldenCompare.AssertOrUpdate(name, Solid(w, h, r, g, b), w, h,
                _goldenDir, _evidenceDir, Backend, updateGoldens: true);
        }

        [Fact]
        public void CompareFailure_WritesGotWantDiff_AllDecodable_AndPathsInMessage()
        {
            const int w = 64, h = 36;
            const string name = "cmpfail";
            // Golden is a solid red scene; the captured frame is solid green: every cell fails.
            BakeSolidGolden(name, w, h, 220, 0, 0);
            // Bake also wrote evidence; clear it so we only see the compare-failure output.
            foreach (var f in Directory.GetFiles(_evidenceDir)) File.Delete(f);

            var ex = Assert.Throws<Xunit.Sdk.FailException>(() =>
                GoldenCompare.AssertOrUpdate(name, Solid(w, h, 0, 220, 0), w, h,
                    _goldenDir, _evidenceDir, Backend, updateGoldens: false));

            string got = Path.Combine(_evidenceDir, $"{name}.{Backend}.got.png");
            string want = Path.Combine(_evidenceDir, $"{name}.{Backend}.want.png");
            string diff = Path.Combine(_evidenceDir, $"{name}.{Backend}.diff.png");

            // Exactly these three PNGs.
            var written = Directory.GetFiles(_evidenceDir).Select(Path.GetFileName).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { $"{name}.{Backend}.diff.png", $"{name}.{Backend}.got.png", $"{name}.{Backend}.want.png" }, written);

            foreach (var p in new[] { got, want, diff })
            {
                var img = ImageRgba.Load(p);
                Assert.Equal(w, img.Width);
                Assert.Equal(h, img.Height);
            }

            Assert.Contains(got, ex.Message);
            Assert.Contains(want, ex.Message);
            Assert.Contains(diff, ex.Message);
        }

        [Fact]
        public void MissingGolden_WritesGotPng_AndMentionsIt()
        {
            const int w = 64, h = 36;
            const string name = "missing";
            var ex = Assert.Throws<Xunit.Sdk.FailException>(() =>
                GoldenCompare.AssertOrUpdate(name, Solid(w, h, 10, 20, 30), w, h,
                    _goldenDir, _evidenceDir, Backend, updateGoldens: false));

            string got = Path.Combine(_evidenceDir, $"{name}.{Backend}.got.png");
            Assert.True(File.Exists(got), "missing-golden path should still write got.png");
            var img = ImageRgba.Load(got);
            Assert.Equal(w, img.Width);
            Assert.Equal(h, img.Height);
            Assert.Contains(got, ex.Message);
        }

        [Fact]
        public void BakeMode_WritesGoldenText_AndBakePng()
        {
            const int w = 64, h = 36;
            const string name = "bakes";
            GoldenCompare.AssertOrUpdate(name, Solid(w, h, 100, 150, 200), w, h,
                _goldenDir, _evidenceDir, Backend, updateGoldens: true);

            string goldenTxt = Path.Combine(_goldenDir, $"{name}.{Backend}.txt");
            string bakePng = Path.Combine(_evidenceDir, $"{name}.{Backend}.bake.png");
            Assert.True(File.Exists(goldenTxt), "bake should write the golden text");
            Assert.True(File.Exists(bakePng), "bake should write bake.png evidence");
            var img = ImageRgba.Load(bakePng);
            Assert.Equal(w, img.Width);
            Assert.Equal(h, img.Height);
        }

        [Fact]
        public void PassingCompare_WritesNothingToEvidenceDir()
        {
            const int w = 64, h = 36;
            const string name = "passes";
            BakeSolidGolden(name, w, h, 40, 80, 120);
            foreach (var f in Directory.GetFiles(_evidenceDir)) File.Delete(f);

            // Same solid color -> passes.
            GoldenCompare.AssertOrUpdate(name, Solid(w, h, 40, 80, 120), w, h,
                _goldenDir, _evidenceDir, Backend, updateGoldens: false);

            Assert.Empty(Directory.GetFiles(_evidenceDir));
        }

        [Fact]
        public void DiffHeatMap_DifferingCellIsRed_MatchingCellIsBlack()
        {
            // Build a scene where exactly one grid cell differs. Grid is 32x18; make the image big enough
            // that each cell maps to a clean block. Use w = GridW*2, h = GridH*2 so each cell is a 2x2 block.
            int w = GoldenCompare.GridW * 4;   // 128
            int h = GoldenCompare.GridH * 4;   // 72
            const string name = "onediff";

            // Golden: solid black everywhere.
            BakeSolidGolden(name, w, h, 0, 0, 0);
            foreach (var f in Directory.GetFiles(_evidenceDir)) File.Delete(f);

            // Captured: black everywhere EXCEPT one known cell (cx=5, cy=3) painted bright white.
            int diffCx = 5, diffCy = 3;
            var px = Solid(w, h, 0, 0, 0);
            int x0 = diffCx * w / GoldenCompare.GridW, x1 = (diffCx + 1) * w / GoldenCompare.GridW;
            int y0 = diffCy * h / GoldenCompare.GridH, y1 = (diffCy + 1) * h / GoldenCompare.GridH;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    int i = (y * w + x) * 4;
                    px[i] = 255; px[i + 1] = 255; px[i + 2] = 255;
                }

            Assert.Throws<Xunit.Sdk.FailException>(() =>
                GoldenCompare.AssertOrUpdate(name, px, w, h,
                    _goldenDir, _evidenceDir, Backend, updateGoldens: false));

            string diff = Path.Combine(_evidenceDir, $"{name}.{Backend}.diff.png");
            var img = ImageRgba.Load(diff);

            // Sample the center of the differing cell: must be red-dominant.
            int dcx = (x0 + x1) / 2, dcy = (y0 + y1) / 2;
            int di = (dcy * img.Width + dcx) * 4;
            Assert.True(img.Pixels[di] > 128, "differing cell should be strongly red");
            Assert.True(img.Pixels[di] > img.Pixels[di + 1] && img.Pixels[di] > img.Pixels[di + 2],
                "differing cell should be red-dominant");

            // Sample a matching cell (cx=0, cy=0) center: must be black.
            int mcx = (0 * w / GoldenCompare.GridW + 1 * w / GoldenCompare.GridW) / 2;
            int mcy = (0 * h / GoldenCompare.GridH + 1 * h / GoldenCompare.GridH) / 2;
            int mi = (mcy * img.Width + mcx) * 4;
            Assert.True(img.Pixels[mi] < 16 && img.Pixels[mi + 1] < 16 && img.Pixels[mi + 2] < 16,
                "matching cell should stay black");
        }
    }
}
