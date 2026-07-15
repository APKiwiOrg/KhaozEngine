using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// GPU coverage for the opt-in <see cref="SpriteBatch.GroupByTexture"/> flush path: for a scene of
    /// non-overlapping quads across interleaved textures (draw order cannot affect the final image here),
    /// grouped output must be pixel-identical to the default submission-order path - proving the merged
    /// -draw-call path uploads the right vertices to the right destination offsets, not just that the CPU-side
    /// key-grouping logic groups keys correctly (see <c>SpriteBatchOrderTests</c> for that headless coverage).
    /// </summary>
    public class SpriteBatchGroupByTextureGpuTests
    {
        const int W = 64, H = 16;

        [GpuFact]
        public void Grouped_And_UnGrouped_ProduceIdenticalPixels_ForInterleavedTextures()
        {
            byte[] Render(bool grouped) => Render2DSnapshot.Capture(W, H, Color.Black, ctx =>
            {
                Texture2D red = ctx.CreateTexture(new byte[] { 255, 0, 0, 255 }, 1, 1);
                Texture2D blue = ctx.CreateTexture(new byte[] { 0, 0, 255, 255 }, 1, 1);

                ctx.Batch.Begin();
                ctx.Batch.GroupByTexture = grouped;
                // Interleaved A,B,A,B across four non-overlapping bands - the exact pattern that used to force
                // four separate submission-order runs (see SpriteBatchOrderTests.InterleavedTextures...).
                ctx.Batch.Draw(red, new Rect(0, 0, 16, H), Color.White);
                ctx.Batch.Draw(blue, new Rect(16, 0, 16, H), Color.White);
                ctx.Batch.Draw(red, new Rect(32, 0, 16, H), Color.White);
                ctx.Batch.Draw(blue, new Rect(48, 0, 16, H), Color.White);
                ctx.Batch.End();
            });

            byte[] unGrouped = Render(grouped: false);
            byte[] grouped = Render(grouped: true);

            Assert.Equal(unGrouped, grouped);

            // Sanity: each band actually landed the expected colour, so the two renders match because the
            // grouped draw spans the right vertices at the right destination offset - not because both are
            // equally wrong.
            Assert.Equal(255, PixelR(grouped, 8, 8));
            Assert.Equal(255, PixelB(grouped, 24, 8));
            Assert.Equal(255, PixelR(grouped, 40, 8));
            Assert.Equal(255, PixelB(grouped, 56, 8));
        }

        [GpuFact]
        public void Grouped_AllSameTexture_MatchesUngrouped()
        {
            byte[] Render(bool grouped) => Render2DSnapshot.Capture(W, H, Color.Black, ctx =>
            {
                Texture2D white = ctx.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
                ctx.Batch.Begin();
                ctx.Batch.GroupByTexture = grouped;
                ctx.Batch.Draw(white, new Rect(0, 0, W, H), Color.White);
                ctx.Batch.End();
            });

            Assert.Equal(Render(grouped: false), Render(grouped: true));
        }

        static byte PixelR(byte[] rgba, int x, int y) => rgba[(y * W + x) * 4];
        static byte PixelB(byte[] rgba, int x, int y) => rgba[(y * W + x) * 4 + 2];
    }
}
