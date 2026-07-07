using System;
using System.Globalization;
using System.IO;
using System.Text;
using KhaozEngine.Imaging;
using KhaozEngine.Render2D;

namespace SnapshotTool
{
    /// <summary>
    /// The <c>diff</c> and <c>score</c> subcommands of the snapshot tool, factored as pure argument-to-decision
    /// logic so a plain unit test can drive them against synthetic PNGs without spawning a process. Both build on
    /// <see cref="KhaozEngine.Imaging.GoldenGrid"/>: <c>diff</c> compares two rendered PNGs, <c>score</c> compares a
    /// rendered PNG against a committed golden grid txt (the same files the engine's GPU golden tests use).
    /// <para>
    /// Exit-code contract (shared with the whole tool): <c>0</c> within tolerance, <c>1</c> over tolerance,
    /// <c>2</c> on usage or IO error. Output goes through an injected <see cref="Action{String}"/> so tests can
    /// capture it and the CLI routes it to the console.
    /// </para>
    /// </summary>
    public static class DiffCommands
    {
        /// <summary>Exit code when the compared images are within tolerance.</summary>
        public const int ExitPass = 0;
        /// <summary>Exit code when at least one cell channel is over tolerance.</summary>
        public const int ExitFail = 1;
        /// <summary>Exit code for a usage or IO error (bad args, missing file, dimension mismatch).</summary>
        public const int ExitUsage = 2;

        /// <summary>
        /// Run <c>diff &lt;a.png&gt; &lt;b.png&gt; [--tolerance t] [--grid WxH] [--out heatmap.png]</c>: decode both
        /// PNGs, require equal dimensions, downsample both to the grid, compare, print a summary, and return the
        /// exit code. <c>--out</c> writes a per-cell heat map PNG (same visual language as the golden diff PNG).
        /// </summary>
        /// <param name="args">Arguments AFTER the <c>diff</c> verb.</param>
        /// <param name="log">Sink for summary/error lines (stdout in the CLI, a capture list in tests).</param>
        public static int Diff(string[] args, Action<string> log)
        {
            ArgumentNullException.ThrowIfNull(args);
            ArgumentNullException.ThrowIfNull(log);

            string? aPath = null, bPath = null;
            float tolerance = GoldenGrid.DefaultTolerance;
            int gridW = GoldenGrid.DefaultGridW, gridH = GoldenGrid.DefaultGridH;
            string? outPath = null;

            var positional = new System.Collections.Generic.List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--tolerance":
                        if (!TryTakeValue(args, ref i, out string tolStr) || !TryParseTolerance(tolStr, out tolerance))
                            return Usage(log, "diff: --tolerance needs a number in [0,1].");
                        break;
                    case "--grid":
                        if (!TryTakeValue(args, ref i, out string gridStr) || !TryParseGrid(gridStr, out gridW, out gridH))
                            return Usage(log, "diff: --grid needs WxH (e.g. 32x18).");
                        break;
                    case "--out":
                        if (!TryTakeValue(args, ref i, out outPath!))
                            return Usage(log, "diff: --out needs a file path.");
                        break;
                    default:
                        positional.Add(args[i]);
                        break;
                }
            }
            if (positional.Count != 2)
                return Usage(log, "usage: diff <a.png> <b.png> [--tolerance t] [--grid WxH] [--out heatmap.png]");
            aPath = positional[0];
            bPath = positional[1];

            if (!TryLoad(aPath, log, out ImageRgba a)) return ExitUsage;
            if (!TryLoad(bPath, log, out ImageRgba b)) return ExitUsage;
            if (a.Width != b.Width || a.Height != b.Height)
            {
                log($"error: dimension mismatch: {aPath} is {a.Width}x{a.Height}, {bPath} is {b.Width}x{b.Height}.");
                return ExitUsage;
            }

            float[] gotGrid = GoldenGrid.Downsample(a.Pixels, a.Width, a.Height, gridW, gridH);
            float[] wantGrid = GoldenGrid.Downsample(b.Pixels, b.Width, b.Height, gridW, gridH);
            GoldenGridComparison cmp = GoldenGrid.Compare(gotGrid, wantGrid, tolerance);

            PrintSummary(log, $"diff {aPath} vs {bPath}", cmp, gridW);

            if (outPath is not null)
            {
                if (!TryWriteHeatMap(outPath, gotGrid, wantGrid, a.Width, a.Height, gridW, gridH, tolerance, log))
                    return ExitUsage;
                log($"heat map -> {outPath}");
            }

            return cmp.Passed ? ExitPass : ExitFail;
        }

        /// <summary>
        /// Run <c>score &lt;image.png&gt; &lt;golden.txt&gt; [--tolerance t]</c>: decode the PNG, deserialize the
        /// golden grid, downsample the image to the golden's dimensions, compare, print a summary, and return the
        /// exit code. Works directly against the committed <c>KhaozEngine.Tests/Gpu/goldens/*.txt</c> files.
        /// </summary>
        /// <param name="args">Arguments AFTER the <c>score</c> verb.</param>
        /// <param name="log">Sink for summary/error lines (stdout in the CLI, a capture list in tests).</param>
        public static int Score(string[] args, Action<string> log)
        {
            ArgumentNullException.ThrowIfNull(args);
            ArgumentNullException.ThrowIfNull(log);

            float tolerance = GoldenGrid.DefaultTolerance;
            var positional = new System.Collections.Generic.List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--tolerance":
                        if (!TryTakeValue(args, ref i, out string tolStr) || !TryParseTolerance(tolStr, out tolerance))
                            return Usage(log, "score: --tolerance needs a number in [0,1].");
                        break;
                    default:
                        positional.Add(args[i]);
                        break;
                }
            }
            if (positional.Count != 2)
                return Usage(log, "usage: score <image.png> <golden.txt> [--tolerance t]");
            string imgPath = positional[0], goldenPath = positional[1];

            if (!TryLoad(imgPath, log, out ImageRgba img)) return ExitUsage;

            string goldenText;
            try { goldenText = File.ReadAllText(goldenPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                log($"error: cannot read golden '{goldenPath}': {ex.Message}");
                return ExitUsage;
            }

            if (!TryParseGridDims(goldenText, out int gridW, out int gridH))
            {
                log($"error: golden '{goldenPath}' has no readable '# ... WxH ...' header.");
                return ExitUsage;
            }

            float[] want = GoldenGrid.Deserialize(goldenText);
            if (want.Length != gridW * gridH * 3)
            {
                log($"error: golden '{goldenPath}' header says {gridW}x{gridH} ({gridW * gridH} cells) but has {want.Length / 3} cells.");
                return ExitUsage;
            }

            float[] got = GoldenGrid.Downsample(img.Pixels, img.Width, img.Height, gridW, gridH);
            GoldenGridComparison cmp = GoldenGrid.Compare(got, want, tolerance);
            PrintSummary(log, $"score {imgPath} vs {goldenPath}", cmp, gridW);
            return cmp.Passed ? ExitPass : ExitFail;
        }

        /// <summary>
        /// Parse the <c>WxH</c> pair out of a golden grid's header line (e.g. <c># KhaozEngine golden grid 32x18
        /// ...</c>). Returns false when no <c>#</c> line carries a <c>&lt;int&gt;x&lt;int&gt;</c> token.
        /// </summary>
        public static bool TryParseGridDims(string goldenText, out int gridW, out int gridH)
        {
            gridW = 0; gridH = 0;
            if (goldenText is null) return false;
            foreach (string raw in goldenText.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] != '#') continue;
                foreach (string tok in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (TryParseGrid(tok, out gridW, out gridH))
                        return true;
            }
            return false;
        }

        static void PrintSummary(Action<string> log, string title, GoldenGridComparison cmp, int gridW)
        {
            var sb = new StringBuilder();
            if (cmp.Passed)
            {
                sb.Append(title).Append(": PASS (worst abs diff ")
                  .Append(cmp.WorstDiff.ToString("0.###", CultureInfo.InvariantCulture))
                  .Append(" <= tol ").Append(cmp.Tolerance.ToString("0.###", CultureInfo.InvariantCulture)).Append(").");
                log(sb.ToString());
                return;
            }

            sb.Append(title).Append(": FAIL - ").Append(cmp.Offenders.Count)
              .Append(" channel(s) over tol ").Append(cmp.Tolerance.ToString("0.###", CultureInfo.InvariantCulture))
              .Append(" (worst abs diff ").Append(cmp.WorstDiff.ToString("0.###", CultureInfo.InvariantCulture))
              .Append("). Top cells (cx,cy ch got/want):");
            log(sb.ToString());
            int show = Math.Min(8, cmp.Offenders.Count);
            for (int k = 0; k < show; k++)
            {
                var off = cmp.Offenders[k];
                int cx = off.Cell % gridW, cy = off.Cell / gridW;
                string chN = off.Channel == 0 ? "R" : off.Channel == 1 ? "G" : "B";
                log($"  ({cx},{cy}) {chN} got {off.Got.ToString("0.###", CultureInfo.InvariantCulture)} " +
                    $"want {off.Want.ToString("0.###", CultureInfo.InvariantCulture)} " +
                    $"(diff {off.Diff.ToString("0.###", CultureInfo.InvariantCulture)})");
            }
        }

        static bool TryWriteHeatMap(string outPath, float[] got, float[] want, int w, int h,
            int gridW, int gridH, float tolerance, Action<string> log)
        {
            try
            {
                byte[] heat = GoldenGrid.DiffHeatMap(got, want, w, h, gridW, gridH, tolerance);
                PngWriter.Save(outPath, heat, w, h);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                log($"error: cannot write heat map '{outPath}': {ex.Message}");
                return false;
            }
        }

        static bool TryLoad(string path, Action<string> log, out ImageRgba img)
        {
            img = default;
            try { img = ImageRgba.Load(path); return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                log($"error: cannot decode '{path}': {ex.Message}");
                return false;
            }
        }

        static int Usage(Action<string> log, string message)
        {
            log(message);
            return ExitUsage;
        }

        static bool TryTakeValue(string[] args, ref int i, out string value)
        {
            if (i + 1 >= args.Length) { value = string.Empty; return false; }
            value = args[++i];
            return true;
        }

        static bool TryParseTolerance(string s, out float tolerance)
        {
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out tolerance)
                && tolerance >= 0f && tolerance <= 1f)
                return true;
            tolerance = GoldenGrid.DefaultTolerance;
            return false;
        }

        static bool TryParseGrid(string s, out int gridW, out int gridH)
        {
            gridW = 0; gridH = 0;
            int xi = s.IndexOf('x');
            if (xi <= 0 || xi >= s.Length - 1) return false;
            return int.TryParse(s.AsSpan(0, xi), NumberStyles.Integer, CultureInfo.InvariantCulture, out gridW)
                && int.TryParse(s.AsSpan(xi + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out gridH)
                && gridW > 0 && gridH > 0;
        }
    }
}
