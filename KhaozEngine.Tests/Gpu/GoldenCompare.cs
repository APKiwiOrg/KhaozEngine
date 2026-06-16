using System;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Tolerance-based image regression: downsamples a raw RGBA buffer to a small grid of average RGB per cell
    /// and either WRITES a committed reference grid (when <c>KE_UPDATE_GOLDENS=1</c>) or COMPARES against it with
    /// a per-channel tolerance. Robust to minor driver noise; a real shader/UBO/blend/winding regression moves a
    /// cell well past the tolerance.
    /// </summary>
    internal static class GoldenCompare
    {
        /// <summary>Downsample grid width in cells.</summary>
        public const int GridW = 32;
        /// <summary>Downsample grid height in cells.</summary>
        public const int GridH = 18;
        /// <summary>Per-channel absolute-difference tolerance (channels are 0..1).</summary>
        public const float Tolerance = 0.06f;

        /// <summary>
        /// Downsample <paramref name="rgba"/> (raw RGBA8, <paramref name="w"/>×<paramref name="h"/>) to a
        /// <see cref="GridW"/>×<see cref="GridH"/> grid of average RGB per cell as floats 0..1, row-major,
        /// 3 floats per cell.
        /// </summary>
        public static float[] Downsample(byte[] rgba, int w, int h)
        {
            var grid = new float[GridW * GridH * 3];
            for (int cy = 0; cy < GridH; cy++)
            {
                int y0 = cy * h / GridH, y1 = (cy + 1) * h / GridH;
                if (y1 <= y0) y1 = y0 + 1;
                for (int cx = 0; cx < GridW; cx++)
                {
                    int x0 = cx * w / GridW, x1 = (cx + 1) * w / GridW;
                    if (x1 <= x0) x1 = x0 + 1;
                    double sr = 0, sg = 0, sb = 0;
                    long n = 0;
                    for (int y = y0; y < y1 && y < h; y++)
                        for (int x = x0; x < x1 && x < w; x++)
                        {
                            int i = (y * w + x) * 4;
                            sr += rgba[i]; sg += rgba[i + 1]; sb += rgba[i + 2];
                            n++;
                        }
                    if (n == 0) n = 1;
                    int gi = (cy * GridW + cx) * 3;
                    grid[gi] = (float)(sr / n / 255.0);
                    grid[gi + 1] = (float)(sg / n / 255.0);
                    grid[gi + 2] = (float)(sb / n / 255.0);
                }
            }
            return grid;
        }

        /// <summary>
        /// Capture-and-check entry point. Downsamples <paramref name="rgba"/>; when <c>KE_UPDATE_GOLDENS=1</c>
        /// writes the reference for <paramref name="name"/> and skips the assert, otherwise compares against the
        /// committed reference and fails listing the worst-offending cells.
        /// </summary>
        public static void AssertOrUpdate(string name, byte[] rgba, int w, int h)
        {
            float[] grid = Downsample(rgba, w, h);
            string path = GoldenPath(name);
            if (Environment.GetEnvironmentVariable("KE_UPDATE_GOLDENS") == "1")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, Serialize(grid));
                return;
            }

            Assert.True(File.Exists(path),
                $"golden '{name}' missing at {path}. Run with KE_GPU_TESTS=1 KE_UPDATE_GOLDENS=1 to generate it.");
            float[] golden = Deserialize(File.ReadAllText(path));
            Assert.True(golden.Length == grid.Length,
                $"golden '{name}' has {golden.Length / 3} cells, expected {grid.Length / 3}. Re-bake with KE_UPDATE_GOLDENS=1.");

            // Collect the worst offenders for a useful failure message.
            int cells = GridW * GridH;
            var offenders = new System.Collections.Generic.List<(int cell, float diff, int ch)>();
            float worst = 0f;
            for (int c = 0; c < cells; c++)
                for (int ch = 0; ch < 3; ch++)
                {
                    int idx = c * 3 + ch;
                    float d = Math.Abs(grid[idx] - golden[idx]);
                    if (d > worst) worst = d;
                    if (d > Tolerance) offenders.Add((c, d, ch));
                }

            if (offenders.Count > 0)
            {
                offenders.Sort((a, b) => b.diff.CompareTo(a.diff));
                var sb = new StringBuilder();
                sb.Append($"golden '{name}' regressed: {offenders.Count} channel(s) over tol {Tolerance:0.###} ")
                  .Append($"(worst abs diff {worst:0.###}). Top cells (cx,cy ch got/want):\n");
                int show = Math.Min(8, offenders.Count);
                for (int k = 0; k < show; k++)
                {
                    var (cell, diff, ch) = offenders[k];
                    int cx = cell % GridW, cy = cell / GridW;
                    int idx = cell * 3 + ch;
                    string chN = ch == 0 ? "R" : ch == 1 ? "G" : "B";
                    sb.Append($"  ({cx},{cy}) {chN} got {grid[idx]:0.###} want {golden[idx]:0.###} (diff {diff:0.###})\n");
                }
                sb.Append("Re-bake intentionally with KE_GPU_TESTS=1 KE_UPDATE_GOLDENS=1 if the change is expected.");
                Assert.Fail(sb.ToString());
            }
        }

        static string Serialize(float[] grid)
        {
            var sb = new StringBuilder();
            sb.Append("# KhaozEngine golden grid ").Append(GridW).Append('x').Append(GridH)
              .Append(" (one line per cell: r g b, row-major)\n");
            int cells = GridW * GridH;
            for (int c = 0; c < cells; c++)
            {
                int i = c * 3;
                sb.Append(grid[i].ToString("0.0000", CultureInfo.InvariantCulture)).Append(' ')
                  .Append(grid[i + 1].ToString("0.0000", CultureInfo.InvariantCulture)).Append(' ')
                  .Append(grid[i + 2].ToString("0.0000", CultureInfo.InvariantCulture)).Append('\n');
            }
            return sb.ToString();
        }

        static float[] Deserialize(string text)
        {
            var vals = new System.Collections.Generic.List<float>(GridW * GridH * 3);
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3) continue;
                vals.Add(float.Parse(parts[0], CultureInfo.InvariantCulture));
                vals.Add(float.Parse(parts[1], CultureInfo.InvariantCulture));
                vals.Add(float.Parse(parts[2], CultureInfo.InvariantCulture));
            }
            return vals.ToArray();
        }

        /// <summary>
        /// Resolve <c>Gpu/goldens/&lt;name&gt;.txt</c> next to this source file. Using <see cref="CallerFilePathAttribute"/>
        /// makes the path independent of <c>dotnet test</c>'s working directory and the build output layout, so
        /// generated references and checks always hit the committed source tree.
        /// </summary>
        public static string GoldenPath(string name, [CallerFilePath] string thisFile = "")
        {
            string dir = Path.GetDirectoryName(thisFile)!;
            return Path.Combine(dir, "goldens", name + ".txt");
        }
    }
}
