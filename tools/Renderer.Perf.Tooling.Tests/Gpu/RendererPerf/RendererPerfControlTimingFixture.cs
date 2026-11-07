using System;
using System.Collections.Immutable;
using System.Linq;
using KhaozEngine.Tests.Gpu.Performance;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// Fixed logical work and query results enter through the recorder, which owns status and raw samples.
internal static class RendererPerfControlTimingFixture
{
    internal static RendererPerfReport Execute(RendererPerfReport report)
    {
        var rig = new RendererPerfDriverTests.DriverRig(template: report)
        {
            RetainFrame = (block, frame) => block.SceneId is "control:1" or "control:2" or "control:3"
                ? Sample(int.Parse(block.SceneId.AsSpan(8)), frame, frame.FrameId == 0 ? 2528ul : 0ul) : frame
        };
        var executed = rig.Run();
        if (executed.Publication.OriginalFailure is { } failure) throw new InvalidOperationException(failure);
        return report with
        {
            Controls = [.. executed.Controls.Select(c => c with
            {
                Metrics = c.Control.Id <= 3 ? report.Controls.Single(original => original.Control.Id == c.Control.Id).Metrics
                    .SetItem("control.outputValidated", RendererPerfDriver.Value(1, "count", "synthetic checked outputs"))
                    : report.Controls.Single(original => original.Control.Id == c.Control.Id).Metrics
            })]
        };
    }

    internal static RendererPerfFrameSample Sample(int id, RendererPerfFrameSample template, ulong ticks = 0)
    {
        long clock = 0;
        var recorder = new RendererPerfRecorder(1, RendererPerfLayout.TotalOnly, [], () => ++clock, 1000,
            cpuSpanCapacity: 1, resourceCapacity: 1);
        recorder.BeginFrame(0, template.FrameId, template.SequenceIndex);
        recorder.Count(RendererPerfCounter.Submissions);
        recorder.Count(RendererPerfCounter.ColorClears, id == 2 ? 5 : 1);
        recorder.Count(RendererPerfCounter.DepthClears, id is 2 or 3 ? 1 : 0);
        recorder.EndFrame();
        var sample = recorder.Harvest(0, new([0, ticks], 1, true, "synthetic D3D11 query result"));
        return sample with
        {
            CpuStartTicks = template.CpuStartTicks,
            CpuEndTicks = template.CpuEndTicks,
            CpuFrequency = template.CpuFrequency,
            CpuPhases = template.CpuPhases
        };
    }

    internal static RendererPerfReport MatchControls(RendererPerfReport report)
    {
        var controls = report.Manifest.Controls.Select(c => c.Id <= 3
            ? c with { WorkSha256 = RendererPerfControls.Definition(c.Id).WorkSha256 } : c).ToImmutableArray();
        return report with
        {
            Manifest = report.Manifest with { Controls = controls },
            Controls = [.. report.Controls.Select(c => c with { Control = controls.Single(m => m.Id == c.Control.Id) })]
        };
    }

    internal static RendererPerfReport Censor(RendererPerfReport report)
    {
        report = MatchControls(report);
        return report with
        {
            Controls = [.. report.Controls.Select(c => c.Control.Id > 3 ? c : c with
            {
                Warmup = [.. c.Warmup.Select(f => Sample(c.Control.Id, f, f.FrameId == 0 ? 2528ul : 0ul))],
                Measured = [.. c.Measured.Select(f => Sample(c.Control.Id, f))],
                Metrics = c.Metrics.SetItem("control.outputValidated", RendererPerfDriver.Value(1, "count", "synthetic checked outputs"))
            })]
        };
    }
}
