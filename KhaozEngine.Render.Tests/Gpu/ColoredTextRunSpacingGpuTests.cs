using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class ColoredTextRunSpacingGpuTests
{
    [GpuFact]
    public void ColourBoundaryKeepsTheGlyphDestinationsOfOneUniformString()
    {
        Render2DSnapshot.Capture(64, 64, new KhaozEngine.Primitives.Color(0, 0, 0, 1), ctx =>
        {
            DpiFont dpi = ctx.LoadDefaultDpiFont(32f);
            var ui = new UiViewport(1779, 1156, 890, 578);
            SpriteFont font = dpi.For(ui.DpiScale);
            ColoredTextRun[] runs =
            [
                new("Equip ", new Vector4(1f, 0.9f, 0.3f, 1f)),
                new("Stone pickaxe", Vector4.One),
            ];
            var position = new Vector2(20.3f, 33.4f);
            const float scale = 0.84f;

            ctx.Batch.Begin(ui);
            var uniform = ctx.Batch.DebugGlyphDests(font, "Equip Stone pickaxe", position, scale);
            var coloured = ctx.Batch.DebugGlyphDestsRuns(font, runs, position, scale);
            ctx.Batch.End();

            Assert.Equal(uniform, coloured);
        });
    }
}
