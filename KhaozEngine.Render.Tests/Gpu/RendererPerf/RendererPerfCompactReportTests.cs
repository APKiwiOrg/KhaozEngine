using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfCompactReportTests
{
    [Fact]
    public void Full_native_shaped_unit_fits_the_cap_and_round_trips_every_frame_and_counter()
    {
        var report = new RendererPerfDriverTests.DriverRig(full: true).Run();
        var fixture = new RendererPerfNativeSizeFixture();
        report = report with
        {
            Conditioning = [.. report.Conditioning.Select(f => fixture.Replay(f, false))],
            Blocks = [.. report.Blocks.Select(b => b with
            {
                Warmup = [.. b.Warmup.Select(f => fixture.Replay(f, b.Block.Layout == RendererPerfLayout.PerPass))],
                Measured = [.. b.Measured.Select(f => fixture.Replay(f, b.Block.Layout == RendererPerfLayout.PerPass))]
            })],
            Controls = [.. report.Controls.Select(c => c with
            { Warmup = [.. c.Warmup.Select(f => fixture.Replay(f, false))], Measured = [.. c.Measured.Select(f => fixture.Replay(f, false))] })],
            Assessment = report.Assessment with { ValidForPromotion = false, Reasons = ["held recorder failure retained"] }
        };
        byte[] bytes = RendererPerfRawJson.Serialize(report, RendererPerfReportJson.MaximumBytes);
        string? directory = Environment.GetEnvironmentVariable("KE_RENDERER_PERF_FIX_EVIDENCE");
        if (directory is not null)
        {
            File.WriteAllText(Path.Combine(directory, "native-shaped-size.txt"), $"{bytes.Length}\n");
            File.WriteAllText(Path.Combine(directory, "native-shaped-sections.json"),
                JsonSerializer.Serialize(RendererPerfNativeSizeFixture.ByteBreakdown(bytes)));
        }
        Assert.InRange(bytes.Length, 1, RendererPerfReportJson.MaximumBytes);
        var restored = RendererPerfReportJson.Parse(bytes);
        var before = Frames(report).ToArray();
        var after = Frames(restored).ToArray();
        Assert.Equal(16944, before.Length);
        Assert.Equal(before.Length, after.Length);
        var compared = new HashSet<(object, object)>();
        for (int i = 0; i < before.Length; i++)
        {
            var a = before[i]; var b = after[i];
            Assert.Equal(a with { Metrics = b.Metrics, Passes = b.Passes, CpuPhases = b.CpuPhases, Errors = b.Errors }, b);
            Assert.Equal(a.Passes.ToArray(), b.Passes.ToArray());
            Assert.Equal(a.CpuPhases.ToArray(), b.CpuPhases.ToArray());
            Assert.Equal(a.Errors.ToArray(), b.Errors.ToArray());
            if (compared.Add((a.Metrics, b.Metrics)))
                Assert.Equal(a.Metrics.OrderBy(p => p.Key), b.Metrics.OrderBy(p => p.Key));
        }
        Assert.Contains(after.SelectMany(f => f.Passes), p => p.DeviceOnly && p.Duration.Status == RendererPerfMetricStatus.Unsupported);
        Assert.Contains(after.SelectMany(f => f.Metrics), p => p.Value.Status == RendererPerfMetricStatus.ModelIncomplete);
        Assert.Contains(after.SelectMany(f => f.Errors), e => e == "held recorder failure retained");
    }

    internal static IEnumerable<RendererPerfFrameSample> Frames(RendererPerfReport report) => report.Conditioning
        .Concat(report.Blocks.SelectMany(b => b.Warmup.Concat(b.Measured)))
        .Concat(report.Controls.SelectMany(c => c.Warmup.Concat(c.Measured)));
}
