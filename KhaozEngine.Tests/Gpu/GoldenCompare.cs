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
        /// committed reference and fails listing the worst-offending cells. On failure / missing golden / bake it
        /// also writes viewable PNG evidence (see the internal overload) so the outcome can be eyeballed without
        /// re-running: the got frame, the reconstructed want image, and a per-cell diff heat map.
        /// </summary>
        public static void AssertOrUpdate(string name, byte[] rgba, int w, int h)
        {
            string backend = KhaozEngine.Gpu.GpuBackendSelector.Select().ToString().ToLowerInvariant();
            AssertOrUpdate(name, rgba, w, h, GoldenDir(), EvidenceDir(), backend,
                Environment.GetEnvironmentVariable("KE_UPDATE_GOLDENS") == "1");
        }

        /// <summary>
        /// Core compare/bake logic, parameterized on directories + backend so it is testable against throwaway
        /// temp dirs (never the committed goldens dir, never a process-wide env-var mutation). The public
        /// <see cref="AssertOrUpdate(string,byte[],int,int)"/> resolves those from the environment and delegates
        /// here. Golden text lives at <c>&lt;goldenDir&gt;/&lt;name&gt;.&lt;backend&gt;.txt</c>; evidence PNGs at
        /// <c>&lt;evidenceDir&gt;/&lt;name&gt;.&lt;backend&gt;.{got,want,diff,bake}.png</c>.
        /// </summary>
        internal static void AssertOrUpdate(string name, byte[] rgba, int w, int h,
            string goldenDir, string evidenceDir, string backend, bool updateGoldens)
        {
            float[] grid = Downsample(rgba, w, h);
            string path = Path.Combine(goldenDir, name + "." + backend + ".txt");
            if (updateGoldens)
            {
                Directory.CreateDirectory(goldenDir);
                File.WriteAllText(path, Serialize(grid));
                // Evidence: the full-res capture, so CI bake artifacts are viewable.
                WriteEvidence(evidenceDir, name, backend, "bake", rgba, w, h);
                return;
            }

            if (!File.Exists(path))
            {
                // Write the captured frame so a brand-new scene can be eyeballed before its first bake.
                string gotPath = WriteEvidence(evidenceDir, name, backend, "got", rgba, w, h);
                Assert.Fail(
                    $"golden '{name}' missing at {path}. Run with KE_GPU_TESTS=1 KE_UPDATE_GOLDENS=1 to generate it. " +
                    $"Captured frame written to: {gotPath}");
            }
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

                // Viewable evidence: the captured frame, the golden reconstructed as an image, and a diff heat map.
                string gotPath = WriteEvidence(evidenceDir, name, backend, "got", rgba, w, h);
                string wantPath = WriteEvidence(evidenceDir, name, backend, "want", GridToImage(golden, w, h), w, h);
                string diffPath = WriteEvidence(evidenceDir, name, backend, "diff", DiffHeatMap(grid, golden, w, h), w, h);
                sb.Append("Evidence PNGs:\n")
                  .Append("  got:  ").Append(gotPath).Append('\n')
                  .Append("  want: ").Append(wantPath).Append('\n')
                  .Append("  diff: ").Append(diffPath).Append('\n');
                sb.Append("Re-bake intentionally with KE_GPU_TESTS=1 KE_UPDATE_GOLDENS=1 if the change is expected.");
                Assert.Fail(sb.ToString());
            }
        }

        /// <summary>Encode <paramref name="rgba"/> (w x h RGBA8) to <c>&lt;dir&gt;/&lt;name&gt;.&lt;backend&gt;.&lt;kind&gt;.png</c> and return the path.</summary>
        static string WriteEvidence(string dir, string name, string backend, string kind, byte[] rgba, int w, int h)
        {
            Directory.CreateDirectory(dir);
            string p = Path.Combine(dir, $"{name}.{backend}.{kind}.png");
            KhaozEngine.Imaging.PngWriter.Save(p, rgba, w, h);
            return p;
        }

        /// <summary>
        /// Reconstruct a <see cref="GridW"/>x<see cref="GridH"/> golden grid (3 floats/cell, 0..1) into a
        /// <paramref name="w"/>x<paramref name="h"/> RGBA8 image, each cell painted as a flat nearest-neighbour
        /// block, so it lines up dimensionally with the captured frame.
        /// </summary>
        static byte[] GridToImage(float[] grid, int w, int h)
        {
            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                int cy = Math.Min(y * GridH / h, GridH - 1);
                for (int x = 0; x < w; x++)
                {
                    int cx = Math.Min(x * GridW / w, GridW - 1);
                    int gi = (cy * GridW + cx) * 3;
                    int i = (y * w + x) * 4;
                    px[i] = ToByte(grid[gi]); px[i + 1] = ToByte(grid[gi + 1]); px[i + 2] = ToByte(grid[gi + 2]);
                    px[i + 3] = 255;
                }
            }
            return px;
        }

        /// <summary>
        /// Build a <paramref name="w"/>x<paramref name="h"/> per-cell diff heat map: black for zero diff, scaling
        /// to full red at/above 2x <see cref="Tolerance"/> (max channel abs diff of the cell). Cells over
        /// tolerance are painted full-saturation red with a black inner border so they are unmistakable.
        /// </summary>
        static byte[] DiffHeatMap(float[] got, float[] golden, int w, int h)
        {
            // Per-cell max channel abs diff.
            var cellDiff = new float[GridW * GridH];
            for (int c = 0; c < GridW * GridH; c++)
            {
                float d = 0f;
                for (int ch = 0; ch < 3; ch++)
                    d = Math.Max(d, Math.Abs(got[c * 3 + ch] - golden[c * 3 + ch]));
                cellDiff[c] = d;
            }

            var px = new byte[w * h * 4];
            float scaleMax = 2f * Tolerance;
            for (int y = 0; y < h; y++)
            {
                int cy = Math.Min(y * GridH / h, GridH - 1);
                for (int x = 0; x < w; x++)
                {
                    int cx = Math.Min(x * GridW / w, GridW - 1);
                    int c = cy * GridW + cx;
                    float d = cellDiff[c];
                    byte red = ToByte(scaleMax <= 0f ? 0f : Math.Min(1f, d / scaleMax));
                    byte g = 0, b = 0;
                    if (d > Tolerance)
                    {
                        // Over-tolerance cells: unmistakable full-saturation red, with a black inner border.
                        red = 255;
                        int cx0 = cx * w / GridW, cx1 = (cx + 1) * w / GridW;
                        int cy0 = cy * h / GridH, cy1 = (cy + 1) * h / GridH;
                        bool border = x == cx0 || x == cx1 - 1 || y == cy0 || y == cy1 - 1;
                        if (border) { red = 0; }
                    }
                    int i = (y * w + x) * 4;
                    px[i] = red; px[i + 1] = g; px[i + 2] = b; px[i + 3] = 255;
                }
            }
            return px;
        }

        static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

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
        /// Resolve <c>Gpu/goldens/&lt;name&gt;.&lt;backend&gt;.txt</c> next to this source file, where
        /// <c>&lt;backend&gt;</c> is the active <see cref="KhaozEngine.Gpu.GpuBackendSelector.Select()"/> result
        /// lower-cased (metal / vulkan / direct3d11 / opengl). Each backend gets its own reference grid because a
        /// software rasterizer (lavapipe, WARP) won't match Metal pixel-for-pixel. Using
        /// <see cref="CallerFilePathAttribute"/> makes the path independent of <c>dotnet test</c>'s working
        /// directory and the build output layout, so generated references and checks always hit the committed
        /// source tree.
        /// </summary>
        public static string GoldenPath(string name, [CallerFilePath] string thisFile = "")
        {
            string backend = KhaozEngine.Gpu.GpuBackendSelector.Select().ToString().ToLowerInvariant();
            return Path.Combine(GoldenDir(thisFile), name + "." + backend + ".txt");
        }

        /// <summary>The committed goldens directory next to this source file (<c>Gpu/goldens/</c>).</summary>
        static string GoldenDir([CallerFilePath] string thisFile = "")
            => Path.Combine(Path.GetDirectoryName(thisFile)!, "goldens");

        /// <summary>
        /// Where failure-evidence PNGs are written: the <c>KE_GOLDEN_EVIDENCE_DIR</c> env var if set, else
        /// <c>Gpu/goldens-evidence/</c> next to this source file (via <see cref="CallerFilePathAttribute"/>, the
        /// same working-dir-independent technique as <see cref="GoldenPath"/>). The default dir is gitignored.
        /// </summary>
        static string EvidenceDir([CallerFilePath] string thisFile = "")
        {
            string? env = Environment.GetEnvironmentVariable("KE_GOLDEN_EVIDENCE_DIR");
            if (!string.IsNullOrEmpty(env)) return env;
            return Path.Combine(Path.GetDirectoryName(thisFile)!, "goldens-evidence");
        }
    }
}
