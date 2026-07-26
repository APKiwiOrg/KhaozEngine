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
/// PNGs (mid-pipeline with the bar partway filled + a step label, an indeterminate step with the marquee swept off
/// the left edge and no fill underneath, and the failure state with retry / quit buttons). Rendered through a
/// DpiScale-2 <see cref="UiViewport"/> with the DPI-aware <see cref="DpiFont"/> overload - the real HiDPI point-space
/// path - so the capture shows the texel-crisp boot text. Not a golden compare: it captures evidence for inspection,
/// so it never turns another backend red. Output dir: <c>KE_BOOT_PNG_DIR</c> or a temp folder. Gated by
/// <see cref="GpuFactAttribute"/> (needs <c>KE_GPU_TESTS</c>).
/// </summary>
public class BootScreenVisualTests
{
    // Logical (point) boot canvas. The framebuffer is 2x (Retina), so the point-space UI viewport has DpiScale 2.
    const int LogW = 900;
    const int LogH = 600;
    const int Dpi = 2;
    const int FbW = LogW * Dpi;
    const int FbH = LogH * Dpi;

    [GpuFact]
    public void Captures_MidPipeline_Indeterminate_And_Error_Pngs()
    {
        string dir = Environment.GetEnvironmentVariable("KE_BOOT_PNG_DIR")
            ?? Path.Combine(Path.GetTempPath(), "boot-screen");
        Directory.CreateDirectory(dir);

        var theme = BootScreenTheme.Default;

        var mid = new BootView(BootState.Running, 0.42f, false, LocalizedText.Raw("Contacting server"), null);
        string midPath = Shot(dir, "boot-mid", mid, theme, allowRetry: false, allowQuit: false, elapsed: 0.3f);

        // Issue #327: an indeterminate step used to draw the stale determinate fill under the marquee. elapsed
        // 0.55f puts the marquee visibly off the left edge, so the capture shows a bare track behind it.
        var indeterminate = new BootView(BootState.Running, 0.42f, true, LocalizedText.Raw("Contacting server"), null);
        string indeterminatePath = Shot(dir, "boot-indeterminate", indeterminate, theme,
            allowRetry: false, allowQuit: false, elapsed: 0.55f);

        var error = new BootView(BootState.Failed, 0.42f, false, default, BootStrings.ErrorUpdateRequired);
        string errPath = Shot(dir, "boot-error", error, theme, allowRetry: true, allowQuit: true, elapsed: 0f);

        Assert.True(File.Exists(midPath));
        Assert.True(File.Exists(indeterminatePath));
        Assert.True(File.Exists(errPath));
    }

    static string Shot(string dir, string name, BootView view, BootScreenTheme theme,
        bool allowRetry, bool allowQuit, float elapsed)
    {
        byte[] rgba = Render2DSnapshot.Capture(FbW, FbH, new Color(0.02f, 0.02f, 0.03f, 1f), ctx =>
        {
            // Neither the texture nor the font is disposed here: the callback runs mid-command-recording, so the
            // not-yet-submitted command list still references them (Veldrid's Vulkan backend rejects the submit
            // and lavapipe can crash the host if they are disposed early). The snapshot's per-capture device is
            // torn down inside Capture, reclaiming both. See Render2DSnapshot.Capture's lifetime contract.
            Texture2D white = ctx.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
            DpiFont font = ctx.LoadDefaultDpiFont(30f, cacheSlots: 4);
            var gui = new GuiSurface(white);
            var ui = new UiViewport(FbW, FbH, LogW, LogH); // point space, DpiScale 2

            ctx.Batch.Begin(ui);
            gui.Begin(ctx.Batch, new Pointer());
            BootScreenRenderer.Draw(ctx.Batch, gui, white, font, ui.DpiScale, new Rect(0, 0, LogW, LogH),
                view, theme, allowRetry, allowQuit, elapsed, out _, out _);
            ctx.Batch.End();
        });

        string path = Path.Combine(dir, name + ".png");
        PngWriter.Save(path, rgba, FbW, FbH);
        return path;
    }
}
