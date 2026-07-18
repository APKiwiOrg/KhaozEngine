using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Imaging;
using Xunit;
using SnapshotTool;

namespace KhaozEngine.Tests.Snapshot
{
    /// <summary>
    /// Headless tests for the SnapshotTool <c>diff</c>/<c>score</c> command layer: synthetic PNGs are encoded with
    /// <see cref="PngWriter"/> into a temp dir, and the exit-code decisions plus offender output are asserted
    /// directly against <see cref="DiffCommands"/> without spawning the tool process.
    /// </summary>
    public class DiffCommandsTests : IDisposable
    {
        readonly string _dir;

        public DiffCommandsTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ke-diffcmd-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }

        string Path_(string name) => Path.Combine(_dir, name);

        // Encode a solid w x h RGBA PNG of one colour.
        string WriteSolidPng(string name, byte r, byte g, byte b, int w = 64, int h = 48)
        {
            var px = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                px[i * 4] = r; px[i * 4 + 1] = g; px[i * 4 + 2] = b; px[i * 4 + 3] = 255;
            }
            string p = Path_(name);
            PngWriter.Save(p, px, w, h);
            return p;
        }

        static (int code, List<string> log) RunDiff(params string[] args)
        {
            var log = new List<string>();
            int code = DiffCommands.Diff(args, log.Add);
            return (code, log);
        }

        static (int code, List<string> log) RunScore(params string[] args)
        {
            var log = new List<string>();
            int code = DiffCommands.Score(args, log.Add);
            return (code, log);
        }

        // ---- diff ----

        [Fact]
        public void Diff_identical_images_passes_exit0()
        {
            string a = WriteSolidPng("a.png", 100, 120, 140);
            string b = WriteSolidPng("b.png", 100, 120, 140);
            var (code, log) = RunDiff(a, b);
            Assert.Equal(DiffCommands.ExitPass, code);
            Assert.Contains(log, l => l.Contains("PASS"));
        }

        [Fact]
        public void Diff_large_difference_fails_exit1_with_offenders()
        {
            string a = WriteSolidPng("a.png", 20, 20, 20);
            string b = WriteSolidPng("b.png", 220, 220, 220);
            var (code, log) = RunDiff(a, b);
            Assert.Equal(DiffCommands.ExitFail, code);
            Assert.Contains(log, l => l.Contains("FAIL") && l.Contains("channel(s) over tol"));
            // Solid images differ in every cell channel: 32*18*3 offenders, but only the top 8 cells print.
            Assert.Equal(8, log.FindAll(l => l.TrimStart().StartsWith("(")).Count);
        }

        [Fact]
        public void Diff_within_custom_tolerance_passes()
        {
            string a = WriteSolidPng("a.png", 100, 100, 100);
            string b = WriteSolidPng("b.png", 130, 130, 130);  // ~0.118 diff per channel
            // Default tolerance 0.06 fails; a loose 0.2 passes.
            Assert.Equal(DiffCommands.ExitFail, RunDiff(a, b).code);
            Assert.Equal(DiffCommands.ExitPass, RunDiff(a, b, "--tolerance", "0.2").code);
        }

        [Fact]
        public void Diff_dimension_mismatch_is_usage_error_exit2()
        {
            string a = WriteSolidPng("a.png", 10, 10, 10, w: 64, h: 48);
            string b = WriteSolidPng("b.png", 10, 10, 10, w: 32, h: 48);
            var (code, log) = RunDiff(a, b);
            Assert.Equal(DiffCommands.ExitUsage, code);
            Assert.Contains(log, l => l.Contains("dimension mismatch"));
        }

        [Fact]
        public void Diff_missing_file_is_usage_error_exit2()
        {
            string a = WriteSolidPng("a.png", 10, 10, 10);
            var (code, log) = RunDiff(a, Path_("nope.png"));
            Assert.Equal(DiffCommands.ExitUsage, code);
            Assert.Contains(log, l => l.Contains("cannot decode"));
        }

        [Fact]
        public void Diff_wrong_arg_count_is_usage_error_exit2()
        {
            var (code, log) = RunDiff(WriteSolidPng("a.png", 1, 2, 3));
            Assert.Equal(DiffCommands.ExitUsage, code);
            Assert.Contains(log, l => l.Contains("usage: diff"));
        }

        [Fact]
        public void Diff_out_writes_heatmap_png()
        {
            string a = WriteSolidPng("a.png", 20, 20, 20);
            string b = WriteSolidPng("b.png", 220, 220, 220);
            string outp = Path_("heat.png");
            var (code, log) = RunDiff(a, b, "--out", outp);
            Assert.Equal(DiffCommands.ExitFail, code);
            Assert.True(File.Exists(outp));
            Assert.Contains(log, l => l.Contains("heat map ->"));
        }

        [Fact]
        public void Diff_custom_grid_dims_are_honoured()
        {
            string a = WriteSolidPng("a.png", 100, 120, 140);
            string b = WriteSolidPng("b.png", 100, 120, 140);
            var (code, _) = RunDiff(a, b, "--grid", "8x8");
            Assert.Equal(DiffCommands.ExitPass, code);
        }

        [Fact]
        public void Diff_bad_grid_arg_is_usage_error_exit2()
        {
            string a = WriteSolidPng("a.png", 1, 2, 3);
            string b = WriteSolidPng("b.png", 1, 2, 3);
            var (code, log) = RunDiff(a, b, "--grid", "nonsense");
            Assert.Equal(DiffCommands.ExitUsage, code);
            Assert.Contains(log, l => l.Contains("--grid"));
        }

        // ---- score ----

        [Fact]
        public void Score_matching_golden_passes_exit0()
        {
            string img = WriteSolidPng("img.png", 100, 120, 140);
            string golden = WriteGoldenFor(img, "g.txt");
            var (code, log) = RunScore(img, golden);
            Assert.Equal(DiffCommands.ExitPass, code);
            Assert.Contains(log, l => l.Contains("PASS"));
        }

        [Fact]
        public void Score_regressed_golden_fails_exit1()
        {
            // Golden captured from a dark image, scored against a bright one.
            string dark = WriteSolidPng("dark.png", 20, 20, 20);
            string golden = WriteGoldenFor(dark, "g.txt");
            string bright = WriteSolidPng("bright.png", 220, 220, 220);
            var (code, log) = RunScore(bright, golden);
            Assert.Equal(DiffCommands.ExitFail, code);
            Assert.Contains(log, l => l.Contains("FAIL"));
        }

        [Fact]
        public void Score_missing_golden_is_usage_error_exit2()
        {
            string img = WriteSolidPng("img.png", 1, 2, 3);
            var (code, log) = RunScore(img, Path_("missing.txt"));
            Assert.Equal(DiffCommands.ExitUsage, code);
            Assert.Contains(log, l => l.Contains("cannot read golden"));
        }

        [Fact]
        public void Score_reads_grid_dims_from_committed_engine_golden_header()
        {
            // The committed engine goldens carry a "# ... 32x18 ..." header; a non-default 12x9 must round-trip.
            var grid = new float[12 * 9 * 3];
            for (int i = 0; i < grid.Length; i++) grid[i] = 0.4f;
            string golden = Path_("g12x9.txt");
            File.WriteAllText(golden, GoldenGrid.Serialize(grid, 12, 9));

            Assert.True(DiffCommands.TryParseGridDims(File.ReadAllText(golden), out int gw, out int gh));
            Assert.Equal(12, gw);
            Assert.Equal(9, gh);

            // A uniform ~0.4 grey image scores against the uniform 0.4 golden within tolerance.
            byte v = (byte)Math.Round(0.4f * 255f);
            string img = WriteSolidPng("grey.png", v, v, v);
            Assert.Equal(DiffCommands.ExitPass, RunScore(img, golden).code);
        }

        [Fact]
        public void Score_wrong_arg_count_is_usage_error_exit2()
        {
            var (code, log) = RunScore(WriteSolidPng("img.png", 1, 2, 3));
            Assert.Equal(DiffCommands.ExitUsage, code);
            Assert.Contains(log, l => l.Contains("usage: score"));
        }

        // Build a golden txt from a solid image, matching how the tool downsamples (default 32x18).
        string WriteGoldenFor(string imgPath, string goldenName)
        {
            var img = KhaozEngine.Render2D.ImageRgba.Load(imgPath);
            float[] grid = GoldenGrid.Downsample(img.Pixels, img.Width, img.Height);
            string p = Path_(goldenName);
            File.WriteAllText(p, GoldenGrid.Serialize(grid));
            return p;
        }
    }
}
