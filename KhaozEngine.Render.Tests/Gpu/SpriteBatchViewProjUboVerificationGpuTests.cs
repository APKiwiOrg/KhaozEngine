using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Imaging;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // VERIFICATION TEST (not a committed-grid golden - deliberately no "Golden" in the name, so it stays out of the
    // cross-backend golden bake filter). It exercises the view-projection UBO path added when SpriteBatch stopped
    // transforming quad corners on the CPU: corners are now emitted in authoring space and the vertex shader
    // multiplies a per-Begin view-projection uniform. The core risk is the per-Begin dynamic-offset slot design: if
    // two Begins in one frame shared/overwrote a single UBO slot, Metal/Veldrid could bind the last-written matrix to
    // both (the documented mid-command-list uniform-overwrite hazard), so a sprite drawn under one transform would
    // render at another's location. This renders THREE Begins with distinct transforms in one frame and probes that
    // each sprite lands where its own transform puts it, plus a rounded-rect panel, on-panel text, and a
    // scissor-clipped fill, then dumps a PNG for eyeballing.
    public sealed class SpriteBatchViewProjUboVerificationGpuTests
    {
        const int W = 512, H = 512;

        static readonly string FontPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Roboto-Regular.ttf");

        [GpuFact]
        public void ViewProjUbo_multiple_begins_each_use_their_own_transform()
        {
            byte[] rgba = Render2DSnapshot.Capture(W, H, new Color(0.06f, 0.07f, 0.10f, 1f), ctx =>
            {
                Texture2D white = ctx.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
                SpriteFont font = ctx.LoadFont(FontPath, 48f);

                // Begin 1 (slot 0): plain screen space. Red square at the top-left.
                ctx.Batch.Begin();
                ctx.Batch.Draw(white, new Vector4(40, 40, 120, 120), new Color(0.85f, 0.2f, 0.2f, 1f));
                ctx.Batch.End();

                // Begin 2 (slot 1): screen space with a +300px X model translate. A green square drawn at the SAME
                // local rect as the red one must land 300px to the right - proving Begin 2's slot carries its own
                // matrix and did not inherit (or get overwritten by) Begin 1's.
                ctx.Batch.Begin(Matrix4x4.CreateTranslation(300f, 0f, 0f));
                ctx.Batch.Draw(white, new Vector4(40, 40, 120, 120), new Color(0.2f, 0.8f, 0.3f, 1f));
                ctx.Batch.End();

                // Begin 3 (slot 2): screen space again. A rounded blue panel, yellow text on it, and a
                // scissor-clipped magenta fill in a narrow band on the right.
                ctx.Batch.Begin();
                ctx.Batch.DrawRounded(white, new Vector4(40, 300, 200, 150), new Color(0.2f, 0.4f, 0.9f, 1f), cornerRadius: 30f);
                ctx.Batch.DrawString(font, "OK", new Vector2(60, 340), new Color(0.95f, 0.95f, 0.4f, 1f), 2f);
                ctx.Batch.SetScissor(new Rect(300, 300, 180, 60));
                ctx.Batch.Draw(white, new Vector4(0, 0, W, H), new Color(0.9f, 0.2f, 0.9f, 1f));
                ctx.Batch.ClearScissor();
                ctx.Batch.End();
            });

            // Dump a PNG for the orchestrator to inspect. KE_VERIFY_DUMP_DIR overrides the location (set by the run).
            string dumpDir = Environment.GetEnvironmentVariable("KE_VERIFY_DUMP_DIR") ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(dumpDir);
            string png = Path.Combine(dumpDir, "spritebatch_viewproj_ubo_verification.png");
            PngWriter.Save(png, rgba, W, H);

            // --- Red square from Begin 1 sits at its screen rect [40..160] and NOT under Begin 2's transform. ---
            Assert.True(IsColor(rgba, 100, 100, r => r > 200, g => g < 80, b => b < 80), "red square missing at its own (screen) location");

            // --- Green square from Begin 2 lands shifted +300px, so its centre is near x=400 (not x=100). ---
            Assert.True(IsColor(rgba, 400, 100, r => r < 100, g => g > 150, b => b < 110), "green square missing at its translated location (x~400)");
            // The translate must be REAL: nothing green sits at the un-translated x=100 column, and nothing red at x=400.
            Assert.False(IsColor(rgba, 100, 100, r => r < 100, g => g > 150, b => b < 110), "green leaked into the un-translated column (slots collided)");
            Assert.False(IsColor(rgba, 400, 100, r => r > 200, g => g < 80, b => b < 80), "red leaked into the translated column (slots collided)");

            // --- Rounded blue panel (Begin 3) is solid blue to the right of the "OK" glyphs. ---
            Assert.True(IsColor(rgba, 210, 375, r => r < 120, g => g > 60, b => b > 150), "blue rounded panel missing where it should be solid");

            // --- Scissor-clipped magenta appears in the band [300..360) and NOT below it. ---
            Assert.True(IsColor(rgba, 400, 330, r => r > 150, g => g < 120, b => b > 150), "scissor-clipped magenta missing inside the band");
            Assert.False(IsColor(rgba, 400, 420, r => r > 150, g => g < 120, b => b > 150), "magenta bled past the scissor band");

            // --- Yellow text glyphs are present on the panel. ---
            int yellow = 0;
            for (int y = 335; y < 395; y++)
                for (int x = 55; x < 180; x++)
                    if (IsColor(rgba, x, y, r => r > 180, g => g > 180, b => b < 170)) yellow++;
            Assert.True(yellow > 25, $"expected yellow text glyph pixels on the panel, found {yellow}");
        }

        static bool IsColor(byte[] rgba, int x, int y, Func<byte, bool> r, Func<byte, bool> g, Func<byte, bool> b)
        {
            int i = (y * W + x) * 4;
            return r(rgba[i]) && g(rgba[i + 1]) && b(rgba[i + 2]);
        }
    }
}
