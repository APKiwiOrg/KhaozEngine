using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // Single-line fields clip their own text (#110). TextInput.Draw and NumberField.Draw both drew their content
    // at a fixed offset from the field's left edge and let it run as wide as it measured, with no scissor
    // anywhere in either file, so a value wider than the box painted over whatever was drawn beside it. Both
    // fields sit at the left of a wide canvas here, with a marker band drawn to their right: nothing the field
    // draws may reach it.
    public sealed class TextFieldClipGpuTests
    {
        const int W = 400, H = 60;
        static readonly Rect Field = new(10, 10, 120, 32);
        const int RightOfField = 130;   // Field.Right, the first column the field must never paint in

        static readonly string FontPath = System.IO.Path.Combine(
            System.AppContext.BaseDirectory, "Assets", "Roboto-Regular.ttf");

        [GpuFact]
        public void TextInput_does_not_paint_its_value_past_the_field_box()
        {
            byte[] rgba = Render2DSnapshot.Capture(W, H, new Color(0, 0, 0, 1), ctx =>
            {
                SpriteFont font = ctx.LoadFont(FontPath, 20f, oversample: 1);
                Texture2D white = ctx.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
                var vp = new DesignViewport(W, H, ScaleMode.Fit);
                vp.Update(W, H);
                ctx.Batch.Begin(vp);

                var input = new TextInput(Field, font)
                {
                    MaxLength = 128,
                    IsFocused = true,   // draws the caret too, which trails the text and spills first
                };
                input.SetText("a value far too long for this narrow field");
                input.Draw(ctx.Batch, white);

                ctx.Batch.End();
            });

            Assert.True(HasLitPixels(rgba, Field.X, RightOfField), "the field drew nothing at all, so this proves nothing");
            AssertNothingLitRightOf(rgba);
        }

        [GpuFact]
        public void NumberField_does_not_paint_its_value_past_the_field_box()
        {
            byte[] rgba = Render2DSnapshot.Capture(W, H, new Color(0, 0, 0, 1), ctx =>
            {
                SpriteFont font = ctx.LoadFont(FontPath, 20f, oversample: 1);
                Texture2D white = ctx.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
                var vp = new DesignViewport(W, H, ScaleMode.Fit);
                vp.Update(W, H);
                ctx.Batch.Begin(vp);

                // Wide enough to overflow a 120-unit box at this font size: 10 digits plus 6 decimals.
                var field = new NumberField(Field, 1234567890f) { Decimals = 6 };
                field.Draw(ctx.Batch, white, font);

                ctx.Batch.End();
            });

            Assert.True(HasLitPixels(rgba, Field.X, RightOfField), "the field drew nothing at all, so this proves nothing");
            AssertNothingLitRightOf(rgba);
        }

        // Nothing the field draws may reach a column at or past the field's right edge. The capture's clear is
        // black, so any non-black pixel out there came from the field.
        static void AssertNothingLitRightOf(byte[] rgba)
        {
            for (int y = 0; y < H; y++)
            {
                for (int x = RightOfField; x < W; x++)
                {
                    int i = (y * W + x) * 4;
                    Assert.True(rgba[i] + rgba[i + 1] + rgba[i + 2] == 0,
                        $"the field painted at ({x}, {y}), past its right edge at {RightOfField}");
                }
            }
        }

        static bool HasLitPixels(byte[] rgba, float fromX, int toX)
        {
            for (int y = 0; y < H; y++)
            {
                for (int x = (int)fromX; x < toX; x++)
                {
                    int i = (y * W + x) * 4;
                    if (rgba[i] + rgba[i + 1] + rgba[i + 2] > 0) return true;
                }
            }
            return false;
        }
    }
}
