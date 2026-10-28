using System;
using System.Collections.Immutable;
using System.Linq;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// The baseline fixture's synthetic frames, expanded to six profiles without opening a device.
internal static class RendererPerfProfileFixture
{
    internal static RendererPerfReport Report(string preset = "water-optics-v2", string leg = "linux-vulkan")
    {
        var seed = RendererPerfAnalysisFixture.Report(full: true);
        var manifest = RendererPerfRehearsalManifestTests.Parse(RendererPerfWaterOpticsPresetTests.ManifestJson(preset, leg));
        ImmutableArray<RendererPerfFrameSample> Frames(int count, int first, int sequence, RendererPerfBlock? block = null)
            => [.. Enumerable.Range(0, count).Select(i =>
            {
                var frame = seed.Blocks[0].Measured[0] with { FrameId = first + i, SequenceIndex = (sequence + i) % 256 };
                if (block is null) return frame;
                int scene = int.Parse(block.SceneId.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture);
                int layout = block.Layout == RendererPerfLayout.PerPass ? 5 : 0;
                double duration = 100 * (scene + 1) + 2 * block.Iteration + layout + (scene + 1) * (i % 16 + 1);
                frame = RendererPerfAnalysisFixture.Duration(frame, duration);
                return block.Layout == RendererPerfLayout.TotalOnly ? frame : frame with
                {
                    Passes = [new("opaque", 0, null, null, 1, 2, (ulong)(10 * (scene + 1) + 2 * block.Iteration + i % 16),
                        RendererPerfAnalysisFixture.Value(10 * (scene + 1) + 2 * block.Iteration + i % 16), false)]
                };
            })];
        var report = seed with
        {
            Manifest = manifest,
            Recipes = [.. manifest.Recipes.Select(r => seed.Recipes[0] with { Identity = r })],
            Blocks = [.. manifest.Blocks.Select(b => new RendererPerfBlockReport(b,
                Frames(b.WarmupFrames, 0, (256 - b.WarmupFrames) % 256, b),
                Frames(b.MeasuredFrames, b.WarmupFrames, 0, b), []))],
            Controls = [.. manifest.Controls.Select(c => seed.Controls[0] with
            {
                Control = c,
                Warmup = Frames(c.WarmupFrames, 0, (256 - c.WarmupFrames) % 256),
                Measured = Frames(c.MeasuredFrames, c.WarmupFrames, 0)
            })]
        };
        return RendererPerfExecutionEvidenceTests.WithTrace(report);
    }
}
