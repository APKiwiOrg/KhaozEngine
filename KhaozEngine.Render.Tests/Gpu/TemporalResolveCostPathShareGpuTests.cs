using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Readback of the cost fixtures' own boxes and thin field, never a timing. Frame 9 is the count request's
/// render (draw callback 8), after four split and four fused frames. Frame 90 samples later ongoing motion. Entry
/// switches preserve history. Export and unmodified production scenes advance one frame each in lockstep, with only
/// coverage, named slow reasons and bit-identical stored locks asserted, as in the shadowed yard.</summary>
public sealed class TemporalResolveCostPathShareGpuTests(ITestOutputHelper output)
{
    [GpuTheory]
    [InlineData(false, 0, TemporalUpscale.Native)]
    [InlineData(false, 0, TemporalUpscale.Quality)]
    [InlineData(false, 1, TemporalUpscale.Native)]
    [InlineData(false, 1, TemporalUpscale.Quality)]
    [InlineData(true, 0, TemporalUpscale.Native)]
    [InlineData(true, 0, TemporalUpscale.Quality)]
    [InlineData(true, 1, TemporalUpscale.Native)]
    [InlineData(true, 1, TemporalUpscale.Quality)]
    public void TheCostScenesReportTheirVisibilityPathsAndWholeFootprints(bool field, int choice, TemporalUpscale preset)
    {
        var entry = (TemporalResolveEntry)choice;
        (int w, int h) = TemporalVisibilityPathShareReadback.DisplaySize();
        var stage = new FrontStage(w, h, 4.5f);
        using var exporting = new TemporalFixture(w, h, s => stage.Setup(s, AntiAliasing.Temporal, preset));
        using var production = new TemporalFixture(w, h, s => stage.Setup(s, AntiAliasing.Temporal, preset));
        TemporalVisibilityPathShareReadback.UseExportingResolve(exporting, TemporalVisibilityFootprintExport.Rewrite);
        Action<Scene3D, int> draw = field ? TemporalResolveCostPerfGpuTests.FieldScene(stage)
            : TemporalResolveCostPerfGpuTests.BoxesScene(stage);
        output.WriteLine($"{exporting.Device.Backend} on {exporting.Device.Capabilities.DeviceName}, "
            + $"{(field ? "moving thin field" : "twelve keyed boxes")}, {entry} {preset}, {w}x{h} display");

        for (int frame = 1; frame <= 90; frame++)
        {
            TemporalResolveEntry active = frame <= 4 ? TemporalResolveEntry.Split
                : frame <= 8 ? TemporalResolveEntry.Fused : entry;
            exporting.Scene.TemporalResolveEntryForTests = production.Scene.TemporalResolveEntryForTests = active;
            if (frame == 9) production.Scene.RequestTemporalCounts();
            exporting.Frames(1, draw);
            production.Frames(1, draw);
            if (frame is not (9 or 90)) continue;
            production.Scene.CollectTemporalCountsForTests();
            output.WriteLine($"  sample scene frame {production.Scene.CurrentFrameView.FrameIndex}, draw frame {frame - 1}, "
                + $"cost counts frame {production.Scene.LastTemporalDiagnostics.CountsFrameIndex}");
            foreach (TemporalFixture fx in new[] { exporting, production })
            {
                Assert.True(fx.Scene.ResolvedLastRenderForTests, "the frame did not resolve");
                Assert.Equal(entry, fx.Scene.TemporalResolveRendererForTests!.LastEntry);
            }
            ReportSceneAndSignals(exporting);
            TemporalVisibilityPathShareReadback.ReportAndAssert(exporting, production, frame, output,
                colour => TemporalVisibilityFootprintExport.ReportAndDecode(colour, output));
        }
    }

    void ReportSceneAndSignals(TemporalFixture fx)
    {
        Scene3D scene = fx.Scene;
        TemporalResolveRenderer renderer = scene.TemporalResolveRendererForTests!;
        TemporalHistory history = scene.TemporalHistory;
        ShadowMode effective = scene.Post.Quality.Shadows.ResolveFor(fx.Device.Capabilities).Effective;
        output.WriteLine($"  default directional key: direction {scene.Post.LightDirection}, colour {scene.Post.LightColor}, "
            + $"shadows requested {scene.Post.Quality.Shadows.Mode}, effective {effective}, "
            + $"key shadow pass active {scene.LastShadowPassDiagnostics.Active}");
        output.WriteLine($"    R8 internal {history.InternalWidth}x{history.InternalHeight}, "
            + $"history ready {renderer.LastVisibilityUniforms.PreviousJitter.Z > 0.5f}");
        if (scene.ResolvedVisibilityTarget is { } target) Histogram("current R8 target", fx.Device, target);
        else output.WriteLine("    current R8 target absent (the resolve binds its fully-lit fallback)");
        Histogram("current stored R8 (write index)", fx.Device, renderer.VisibilityHistory.Texture(history.WriteIndex));
        Histogram("previous stored R8 (read index)", fx.Device, renderer.VisibilityHistory.Texture(history.ReadIndex));
    }

    void Histogram(string label, IGpuDevice device, IGpuTexture texture)
    {
        byte[] values = TemporalVisibilityHistoryGpuTests.Read(device, texture);
        var bins = new long[256];
        foreach (byte value in values) bins[value]++;
        IEnumerable<string> nonzero = Enumerable.Range(0, 256).Where(v => bins[v] != 0)
            .Select(v => $"{v.ToString(CultureInfo.InvariantCulture)}={bins[v]} "
                + $"({TemporalVisibilityPathShareReadback.Share(bins[v], values.LongLength)})");
        output.WriteLine($"    {label} histogram: {string.Join(", ", nonzero)}");
    }
}
