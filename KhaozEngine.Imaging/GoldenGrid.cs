using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KhaozEngine.Imaging
{
    /// <summary>
    /// Tolerance-based image-regression core: downsample a raw RGBA8 buffer to a small grid of average RGB per
    /// cell, compare two grids per-channel with an absolute tolerance, and serialize/deserialize a grid to the
    /// committed golden text format. This is the reusable engine primitive behind the test project's golden
    /// harness and the snapshot-diff tool, so games can golden-test their own scenes without pulling in xUnit.
    /// Dependency-free (BCL only): it carries no notion of files, backends, or test frameworks.
    /// <para>
    /// A grid is a flat <c>float[]</c>, row-major, 3 floats per cell (R, G, B in 0..1), length
    /// <c>gridW * gridH * 3</c>. The default grid is <see cref="DefaultGridW"/>x<see cref="DefaultGridH"/> at
    /// tolerance <see cref="DefaultTolerance"/>, matching the committed engine goldens.
    /// </para>
    /// </summary>
    public static class GoldenGrid
    {
        /// <summary>Default downsample grid width in cells (matches the committed engine goldens).</summary>
        public const int DefaultGridW = 32;
        /// <summary>Default downsample grid height in cells (matches the committed engine goldens).</summary>
        public const int DefaultGridH = 18;
        /// <summary>Default per-channel absolute-difference tolerance (channels are 0..1).</summary>
        public const float DefaultTolerance = 0.06f;

        /// <summary>
        /// Downsample <paramref name="rgba"/> (raw RGBA8, <paramref name="w"/>x<paramref name="h"/>, row-major
        /// top-to-bottom) to a <paramref name="gridW"/>x<paramref name="gridH"/> grid of average RGB per cell as
        /// floats 0..1, row-major, 3 floats per cell (alpha is ignored). Each cell averages the source pixels
        /// falling in its rectangle; an empty cell (grid coarser than the source in that axis is not empty, but a
        /// zero-area cell edge case) averages at least one pixel.
        /// </summary>
        public static float[] Downsample(byte[] rgba, int w, int h, int gridW = DefaultGridW, int gridH = DefaultGridH)
        {
            ArgumentNullException.ThrowIfNull(rgba);
            if (w <= 0 || h <= 0) throw new ArgumentOutOfRangeException(nameof(w), "w/h must be positive.");
            if (gridW <= 0 || gridH <= 0) throw new ArgumentOutOfRangeException(nameof(gridW), "gridW/gridH must be positive.");
            int expected = w * h * 4;
            if (rgba.Length != expected)
                throw new ArgumentException($"rgba length {rgba.Length} != w*h*4 ({expected}).", nameof(rgba));

            var grid = new float[gridW * gridH * 3];
            for (int cy = 0; cy < gridH; cy++)
            {
                int y0 = cy * h / gridH, y1 = (cy + 1) * h / gridH;
                if (y1 <= y0) y1 = y0 + 1;
                for (int cx = 0; cx < gridW; cx++)
                {
                    int x0 = cx * w / gridW, x1 = (cx + 1) * w / gridW;
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
                    int gi = (cy * gridW + cx) * 3;
                    grid[gi] = (float)(sr / n / 255.0);
                    grid[gi + 1] = (float)(sg / n / 255.0);
                    grid[gi + 2] = (float)(sb / n / 255.0);
                }
            }
            return grid;
        }

        /// <summary>
        /// Compare grid <paramref name="got"/> against <paramref name="want"/> per channel. Every channel whose
        /// absolute difference exceeds <paramref name="tolerance"/> becomes an offender; the worst absolute diff
        /// across all channels is always reported (even when nothing exceeds tolerance). Both grids must be the
        /// same length; callers format their own failure message from the result.
        /// </summary>
        public static GoldenGridComparison Compare(float[] got, float[] want, float tolerance = DefaultTolerance)
        {
            ArgumentNullException.ThrowIfNull(got);
            ArgumentNullException.ThrowIfNull(want);
            if (got.Length != want.Length)
                throw new ArgumentException($"grid length mismatch: got {got.Length}, want {want.Length}.", nameof(want));
            if (got.Length % 3 != 0)
                throw new ArgumentException($"grid length {got.Length} is not a multiple of 3.", nameof(got));

            int cells = got.Length / 3;
            var offenders = new List<GoldenGridOffender>();
            float worst = 0f;
            for (int c = 0; c < cells; c++)
                for (int ch = 0; ch < 3; ch++)
                {
                    int idx = c * 3 + ch;
                    float d = Math.Abs(got[idx] - want[idx]);
                    if (d > worst) worst = d;
                    if (d > tolerance) offenders.Add(new GoldenGridOffender(c, ch, got[idx], want[idx], d));
                }
            // Worst diff first so callers can take the top N.
            offenders.Sort((a, b) => b.Diff.CompareTo(a.Diff));
            return new GoldenGridComparison(worst, tolerance, offenders);
        }

        /// <summary>
        /// Serialize <paramref name="grid"/> (<paramref name="gridW"/>x<paramref name="gridH"/>, 3 floats/cell) to
        /// the canonical golden text format: a <c># KhaozEngine golden grid WxH ...</c> header line, then one line
        /// per cell of <c>r g b</c> formatted to four decimal places (invariant culture), each terminated by
        /// <c>\n</c>. Byte-identical to the committed engine goldens for the default grid.
        /// </summary>
        public static string Serialize(float[] grid, int gridW = DefaultGridW, int gridH = DefaultGridH)
        {
            ArgumentNullException.ThrowIfNull(grid);
            if (gridW <= 0 || gridH <= 0) throw new ArgumentOutOfRangeException(nameof(gridW), "gridW/gridH must be positive.");
            if (grid.Length != gridW * gridH * 3)
                throw new ArgumentException($"grid length {grid.Length} != gridW*gridH*3 ({gridW * gridH * 3}).", nameof(grid));

            var sb = new StringBuilder();
            sb.Append("# KhaozEngine golden grid ").Append(gridW).Append('x').Append(gridH)
              .Append(" (one line per cell: r g b, row-major)\n");
            int cells = gridW * gridH;
            for (int c = 0; c < cells; c++)
            {
                int i = c * 3;
                sb.Append(grid[i].ToString("0.0000", CultureInfo.InvariantCulture)).Append(' ')
                  .Append(grid[i + 1].ToString("0.0000", CultureInfo.InvariantCulture)).Append(' ')
                  .Append(grid[i + 2].ToString("0.0000", CultureInfo.InvariantCulture)).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Parse a golden text file (as produced by <see cref="Serialize"/>) back into a flat grid. Comment lines
        /// (starting <c>#</c>), blank lines, and lines that are not exactly three space-separated floats are
        /// skipped, so the header round-trips transparently. Returns 3 floats per cell, row-major.
        /// </summary>
        public static float[] Deserialize(string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            var vals = new List<float>(DefaultGridW * DefaultGridH * 3);
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
        /// Reconstruct a <paramref name="gridW"/>x<paramref name="gridH"/> grid (3 floats/cell, 0..1) into a
        /// <paramref name="w"/>x<paramref name="h"/> RGBA8 image, each cell painted as a flat nearest-neighbour
        /// block so it lines up dimensionally with a captured frame. Alpha is opaque (255).
        /// </summary>
        public static byte[] GridToImage(float[] grid, int w, int h, int gridW = DefaultGridW, int gridH = DefaultGridH)
        {
            ArgumentNullException.ThrowIfNull(grid);
            if (w <= 0 || h <= 0) throw new ArgumentOutOfRangeException(nameof(w), "w/h must be positive.");
            if (gridW <= 0 || gridH <= 0) throw new ArgumentOutOfRangeException(nameof(gridW), "gridW/gridH must be positive.");
            if (grid.Length != gridW * gridH * 3)
                throw new ArgumentException($"grid length {grid.Length} != gridW*gridH*3 ({gridW * gridH * 3}).", nameof(grid));

            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                int cy = Math.Min(y * gridH / h, gridH - 1);
                for (int x = 0; x < w; x++)
                {
                    int cx = Math.Min(x * gridW / w, gridW - 1);
                    int gi = (cy * gridW + cx) * 3;
                    int i = (y * w + x) * 4;
                    px[i] = ToByte(grid[gi]); px[i + 1] = ToByte(grid[gi + 1]); px[i + 2] = ToByte(grid[gi + 2]);
                    px[i + 3] = 255;
                }
            }
            return px;
        }

        /// <summary>
        /// Build a <paramref name="w"/>x<paramref name="h"/> per-cell diff heat map between grids
        /// <paramref name="got"/> and <paramref name="want"/>: black for zero diff, scaling to full red at/above
        /// 2x <paramref name="tolerance"/> (per-cell max channel abs diff). Cells over tolerance are painted
        /// full-saturation red with a black inner border so they are unmistakable. Alpha is opaque (255).
        /// </summary>
        public static byte[] DiffHeatMap(float[] got, float[] want, int w, int h,
            int gridW = DefaultGridW, int gridH = DefaultGridH, float tolerance = DefaultTolerance)
        {
            ArgumentNullException.ThrowIfNull(got);
            ArgumentNullException.ThrowIfNull(want);
            if (w <= 0 || h <= 0) throw new ArgumentOutOfRangeException(nameof(w), "w/h must be positive.");
            if (gridW <= 0 || gridH <= 0) throw new ArgumentOutOfRangeException(nameof(gridW), "gridW/gridH must be positive.");
            int expectedLen = gridW * gridH * 3;
            if (got.Length != expectedLen || want.Length != expectedLen)
                throw new ArgumentException($"grid length must be gridW*gridH*3 ({expectedLen}).", nameof(got));

            // Per-cell max channel abs diff.
            var cellDiff = new float[gridW * gridH];
            for (int c = 0; c < gridW * gridH; c++)
            {
                float d = 0f;
                for (int ch = 0; ch < 3; ch++)
                    d = Math.Max(d, Math.Abs(got[c * 3 + ch] - want[c * 3 + ch]));
                cellDiff[c] = d;
            }

            var px = new byte[w * h * 4];
            float scaleMax = 2f * tolerance;
            for (int y = 0; y < h; y++)
            {
                int cy = Math.Min(y * gridH / h, gridH - 1);
                for (int x = 0; x < w; x++)
                {
                    int cx = Math.Min(x * gridW / w, gridW - 1);
                    int c = cy * gridW + cx;
                    float d = cellDiff[c];
                    byte red = ToByte(scaleMax <= 0f ? 0f : Math.Min(1f, d / scaleMax));
                    byte g = 0, b = 0;
                    if (d > tolerance)
                    {
                        // Over-tolerance cells: unmistakable full-saturation red, with a black inner border.
                        red = 255;
                        int cx0 = cx * w / gridW, cx1 = (cx + 1) * w / gridW;
                        int cy0 = cy * h / gridH, cy1 = (cy + 1) * h / gridH;
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
    }

    /// <summary>
    /// One channel of one cell whose absolute difference exceeded the compare tolerance. <see cref="Cell"/> is the
    /// row-major cell index, <see cref="Channel"/> is 0=R, 1=G, 2=B.
    /// </summary>
    /// <param name="Cell">Row-major cell index (<c>cy * gridW + cx</c>).</param>
    /// <param name="Channel">Colour channel: 0=R, 1=G, 2=B.</param>
    /// <param name="Got">The observed channel value (0..1).</param>
    /// <param name="Want">The reference channel value (0..1).</param>
    /// <param name="Diff">Absolute difference <c>|Got - Want|</c>.</param>
    public readonly record struct GoldenGridOffender(int Cell, int Channel, float Got, float Want, float Diff);

    /// <summary>
    /// Result of <see cref="GoldenGrid.Compare"/>: the worst per-channel absolute diff, the tolerance used, and
    /// every over-tolerance channel sorted worst-first. <see cref="Passed"/> is true when there are no offenders.
    /// </summary>
    public sealed class GoldenGridComparison
    {
        internal GoldenGridComparison(float worstDiff, float tolerance, IReadOnlyList<GoldenGridOffender> offenders)
        {
            WorstDiff = worstDiff;
            Tolerance = tolerance;
            Offenders = offenders;
        }

        /// <summary>Largest per-channel absolute difference across the whole grid (reported even when it passes).</summary>
        public float WorstDiff { get; }

        /// <summary>The tolerance the comparison used.</summary>
        public float Tolerance { get; }

        /// <summary>Over-tolerance channels, sorted worst-first. Empty when the comparison passed.</summary>
        public IReadOnlyList<GoldenGridOffender> Offenders { get; }

        /// <summary>True when no channel exceeded the tolerance.</summary>
        public bool Passed => Offenders.Count == 0;
    }
}
