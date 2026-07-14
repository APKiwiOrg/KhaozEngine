using System;
using System.IO;
using KhaozEngine.App;
using KhaozEngine.Game;
using KhaozEngine.Gui;
using KhaozEngine.Imaging;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// Renders the boot screen through the same <see cref="BootScreenRenderer"/> the live scene uses and writes viewable
/// PNGs (mid-pipeline with the bar partway filled + a step label, and the failure state with retry / quit buttons).
/// Not a golden compare - it captures evidence for inspection, so it never turns another backend red. Output dir:
/// <c>KE_BOOT_PNG_DIR</c> or a temp folder. Gated by <see cref="GpuFactAttribute"/> (needs <c>KE_GPU_TESTS</c>).
/// </summary>
public class BootScreenVisualTests
{
    const int W = 900;
    const int H = 600;

    [GpuFact]
    public void Captures_MidPipeline_And_Error_Pngs()
    {
        string dir = Environment.GetEnvironmentVariable("KE_BOOT_PNG_DIR")
            ?? Path.Combine(Path.GetTempPath(), "boot-screen");
        Directory.CreateDirectory(dir);

        var theme = BootScreenTheme.Default;

        var mid = new BootView(BootState.Running, 0.42f, false, LocalizedText.Raw("Contacting server"), null);
        string midPath = Shot(dir, "boot-mid", mid, theme, allowRetry: false, allowQuit: false, elapsed: 0.3f);

        var error = new BootView(BootState.Failed, 0.42f, false, default, BootStrings.ErrorUpdateRequired);
        string errPath = Shot(dir, "boot-error", error, theme, allowRetry: true, allowQuit: true, elapsed: 0f);

        Assert.True(File.Exists(midPath));
        Assert.True(File.Exists(errPath));
    }

    static string Shot(string dir, string name, BootView view, BootScreenTheme theme,
        bool allowRetry, bool allowQuit, float elapsed)
    {
        byte[] rgba = Render2DSnapshot.Capture(W, H, new Color(0.02f, 0.02f, 0.03f, 1f), ctx =>
        {
            Texture2D white = ctx.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
            SpriteFont font = ctx.LoadDefaultFont(30f, oversample: 2);
            var gui = new GuiSurface(white);

            ctx.Batch.Begin();
            gui.Begin(ctx.Batch, new Pointer());
            BootScreenRenderer.Draw(ctx.Batch, gui, white, font, new Rect(0, 0, W, H), view, theme,
                allowRetry, allowQuit, elapsed, out _, out _);
            ctx.Batch.End();
        });

        string path = Path.Combine(dir, name + ".png");
        PngWriter.Save(path, rgba, W, H);
        return path;
    }
}
