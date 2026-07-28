using System;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Asset-free procedural flipbook sheets shared by the flipbook GpuFacts and the flipbook golden. A cols x rows
    /// atlas paints each cell as an opaque coloured disc (a distinct hue per cell index, so a rendered frame's
    /// dominant colour identifies the cell), and the motion sheets are uniform-colour so the decoded displacement is
    /// a known constant.
    /// </summary>
    internal static class FlipbookTestSheets
    {
        // Distinct hue per cell index over a full sweep, at full saturation and value, so adjacent cells stay well
        // apart in RGB and a blended frame lands between its two neighbours.
        public static (byte r, byte g, byte b) CellRgb(int index, int frameCount)
        {
            float h = index / (float)frameCount * 6f;   // hue in [0, 6)
            float x = 1f - MathF.Abs(h % 2f - 1f);
            int seg = (int)h % 6;
            (float r, float g, float b) = seg switch
            {
                0 => (1f, x, 0f),
                1 => (x, 1f, 0f),
                2 => (0f, 1f, x),
                3 => (0f, x, 1f),
                4 => (x, 0f, 1f),
                _ => (1f, 0f, x),
            };
            return ((byte)(r * 255f + 0.5f), (byte)(g * 255f + 0.5f), (byte)(b * 255f + 0.5f));
        }

        /// <summary>A cols x rows atlas, each cell a centred opaque hue disc on a transparent field.</summary>
        public static (byte[] rgba, int w, int h) Atlas(int cols, int rows, int cellPx)
        {
            int w = cols * cellPx, h = rows * cellPx;
            var px = new byte[w * h * 4];
            float radius = cellPx * 0.45f;
            float r2 = radius * radius;
            int frameCount = cols * rows;
            for (int cr = 0; cr < rows; cr++)
                for (int cc = 0; cc < cols; cc++)
                {
                    (byte r, byte g, byte b) = CellRgb(cr * cols + cc, frameCount);
                    float ccx = cc * cellPx + cellPx * 0.5f;
                    float ccy = cr * cellPx + cellPx * 0.5f;
                    for (int y = cr * cellPx; y < (cr + 1) * cellPx; y++)
                        for (int x = cc * cellPx; x < (cc + 1) * cellPx; x++)
                        {
                            float dx = x + 0.5f - ccx, dy = y + 0.5f - ccy;
                            byte a = dx * dx + dy * dy <= r2 ? (byte)255 : (byte)0;
                            int i = (y * w + x) * 4;
                            px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = a;
                        }
                }
            return (px, w, h);
        }

        /// <summary>
        /// A cols x rows atlas whose cell is asymmetric on BOTH axes: the cell's hue is one opaque blob filling a
        /// single quadrant (low x, low y in the row-major byte order), the rest of the cell transparent. A
        /// horizontal mirror moves the blob to the opposite column, a vertical mirror to the opposite row, and both
        /// to the diagonal, so all four of {none, FlipU, FlipV, both} render to distinguishable, measurable
        /// positions.
        /// <para>This exists because <see cref="Atlas"/> cannot see a flip at all: its cell is a CENTRED RADIALLY
        /// SYMMETRIC disc, so it is byte-identical under any mirror. That is precisely why the flipbook suite could
        /// not catch a UV-origin bug.</para>
        /// </summary>
        public static (byte[] rgba, int w, int h) AsymmetricAtlas(int cols, int rows, int cellPx)
        {
            int w = cols * cellPx, h = rows * cellPx;
            var px = new byte[w * h * 4];
            int margin = Math.Max(1, cellPx / 8);
            int half = cellPx / 2;
            int frameCount = cols * rows;
            for (int cr = 0; cr < rows; cr++)
                for (int cc = 0; cc < cols; cc++)
                {
                    (byte r, byte g, byte b) = CellRgb(cr * cols + cc, frameCount);
                    for (int y = cr * cellPx + margin; y < cr * cellPx + half; y++)
                        for (int x = cc * cellPx + margin; x < cc * cellPx + half; x++)
                        {
                            int i = (y * w + x) * 4;
                            px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = 255;
                        }
                }
            return (px, w, h);
        }

        /// <summary>
        /// A cols x rows atlas of FLAT, fully opaque cells: cell <paramref name="hotCell"/> is pure red, every other
        /// cell pure green. A cell has no internal structure at all, so blurring one is a no-op and any green in a
        /// render of the hot cell can only have come from a neighbouring cell. That is what turns cross-cell mip
        /// bleed into a number, where <see cref="Atlas"/>'s hue sweep only makes it a shade.
        /// </summary>
        public static (byte[] rgba, int w, int h) ContrastAtlas(int cols, int rows, int cellPx, int hotCell)
        {
            int w = cols * cellPx, h = rows * cellPx;
            var px = new byte[w * h * 4];
            for (int cr = 0; cr < rows; cr++)
                for (int cc = 0; cc < cols; cc++)
                {
                    bool hot = cr * cols + cc == hotCell;
                    for (int y = cr * cellPx; y < (cr + 1) * cellPx; y++)
                        for (int x = cc * cellPx; x < (cc + 1) * cellPx; x++)
                        {
                            int i = (y * w + x) * 4;
                            px[i] = hot ? (byte)255 : (byte)0;
                            px[i + 1] = hot ? (byte)0 : (byte)255;
                            px[i + 2] = 0;
                            px[i + 3] = 255;
                        }
                }
            return (px, w, h);
        }

        /// <summary>A uniform-colour motion sheet: every texel is (r, g, 0, 255). (128, 128) is neutral (zero
        /// displacement). A value away from 128 encodes a constant per-frame warp.</summary>
        public static (byte[] rgba, int w, int h) UniformMotion(int cols, int rows, int cellPx, byte r, byte g)
        {
            int w = cols * cellPx, h = rows * cellPx;
            var px = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                px[i * 4] = r; px[i * 4 + 1] = g; px[i * 4 + 2] = 0; px[i * 4 + 3] = 255;
            }
            return (px, w, h);
        }
    }
}
