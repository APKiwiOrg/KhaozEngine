using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Offscreen coverage for filled radial primitives. These assertions use geometric containment and alpha
    /// bounds instead of backend-specific golden pixels.
    /// </summary>
    public sealed class PrimitiveArcFillGpuTests
    {
        const int Width = 160;
        const int Height = 160;
        const float InnerRadius = 32f;
        const float OuterRadius = 54f;
        static readonly Vector2 Center = new(80f, 80f);

        [GpuTheory]
        [InlineData(1.4f)]
        [InlineData(-1.4f)]
        [InlineData(6.2831855f)]
        public void Filled_arc_band_stays_inside_its_authored_annulus(float sweep)
        {
            byte[] rgba = Capture((primitives, batch) =>
                primitives.DrawFilledArcBand(
                    batch, Center, InnerRadius, OuterRadius, -0.7f, sweep, Color.White));

            int escaped = 0;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    if (Alpha(rgba, x, y) == 0)
                        continue;
                    float radius = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), Center);
                    if (radius < InnerRadius - 1f || radius > OuterRadius + 1f)
                        escaped++;
                }
            }

            Assert.Equal(0, escaped);
        }

        [GpuFact]
        public void Filled_sector_stays_inside_its_authored_angular_span()
        {
            const float halfAngle = 0.45f;
            byte[] rgba = Capture((primitives, batch) =>
                primitives.DrawFilledSector(batch, Center, 0f, halfAngle, OuterRadius, Color.White));

            int escaped = 0;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    if (Alpha(rgba, x, y) == 0)
                        continue;
                    Vector2 offset = new(x + 0.5f - Center.X, y + 0.5f - Center.Y);
                    float angle = MathF.Abs(MathF.Atan2(offset.Y, offset.X));
                    if (offset.Length() > OuterRadius + 1f || angle > halfAngle + 0.03f)
                        escaped++;
                }
            }

            Assert.Equal(0, escaped);
        }

        [GpuTheory]
        [InlineData(2.4f)]
        [InlineData(-2.4f)]
        [InlineData(6.2831855f)]
        public void Translucent_arc_band_never_composites_over_itself(float sweep)
        {
            const byte authoredAlpha = 64;
            Color color = new(1f, 1f, 1f, authoredAlpha / 255f);
            byte[] singleDraw = Capture((primitives, batch) =>
                primitives.DrawFilledRect(batch, new Rect(20f, 20f, 40f, 40f), color));
            byte[] rgba = Capture((primitives, batch) =>
                primitives.DrawFilledArcBand(
                    batch,
                    Center,
                    InnerRadius,
                    OuterRadius,
                    -1.2f,
                    sweep,
                    color));

            byte singleDrawAlpha = Alpha(singleDraw, 30, 30);
            byte maximumAlpha = 0;
            for (int i = 3; i < rgba.Length; i += 4)
                maximumAlpha = Math.Max(maximumAlpha, rgba[i]);

            Assert.InRange(maximumAlpha, singleDrawAlpha, (byte)(singleDrawAlpha + 1));
        }

        static byte[] Capture(Action<PrimitiveRenderer, SpriteBatch> draw) =>
            Render2DSnapshot.Capture(Width, Height, Color.Transparent, ctx =>
            {
                var primitives = new PrimitiveRenderer(ctx);
                ctx.Batch.Begin();
                draw(primitives, ctx.Batch);
                ctx.Batch.End();
            });

        static byte Alpha(byte[] rgba, int x, int y) => rgba[(y * Width + x) * 4 + 3];
    }
}
