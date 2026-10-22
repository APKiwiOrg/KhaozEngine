using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using KhaozEngine.Tests.Gpu.Performance;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfCompactReportTests
{
    [Fact]
    public void Full_native_shaped_unit_fits_the_cap_and_round_trips_every_frame_and_counter()
    {
        var report = new RendererPerfDriverTests.DriverRig(full: true).Run();
        var total = NativeSample(false);
        var detailed = NativeSample(true);
        RendererPerfFrameSample Shape(RendererPerfFrameSample f, bool perPass) => f with
        {
            Metrics = perPass ? detailed.Metrics : total.Metrics,
            Passes = perPass ? detailed.Passes : [],
            CpuPhases = [new("device.scene.begin", null, f.CpuStartTicks, f.CpuEndTicks)]
        };
        report = report with
        {
            Conditioning = [.. report.Conditioning.Select(f => Shape(f, false))],
            Blocks = [.. report.Blocks.Select(b => b with
            {
                Warmup = [.. b.Warmup.Select(f => Shape(f, b.Block.Layout == RendererPerfLayout.PerPass))],
                Measured = [.. b.Measured.Select(f => Shape(f, b.Block.Layout == RendererPerfLayout.PerPass))]
            })],
            Controls = [.. report.Controls.Select(c => c with
            { Warmup = [.. c.Warmup.Select(f => Shape(f, false))], Measured = [.. c.Measured.Select(f => Shape(f, false))] })]
        };
        // Refuse the known expanded representation before allocating its hundreds of megabytes.
        using (var probe = JsonDocument.Parse(RendererPerfReportJson.Serialize(RendererPerfProtocolTests.CompleteReport())))
            Assert.True(probe.RootElement.TryGetProperty("raw", out _), "The expanded native report exceeds the fixed 256 MiB cap.");
        byte[] bytes = RendererPerfReportJson.Serialize(report);
        Assert.InRange(bytes.Length, 1, 256 * 1024 * 1024);
        string? directory = Environment.GetEnvironmentVariable("KE_RENDERER_PERF_FIX_EVIDENCE");
        if (directory is not null) File.WriteAllText(Path.Combine(directory, "native-shaped-size.txt"), $"{bytes.Length}\n");
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
    }

    internal static IEnumerable<RendererPerfFrameSample> Frames(RendererPerfReport report) => report.Conditioning
        .Concat(report.Blocks.SelectMany(b => b.Warmup.Concat(b.Measured)))
        .Concat(report.Controls.SelectMany(c => c.Warmup.Concat(c.Measured)));

    static RendererPerfFrameSample NativeSample(bool detailed)
    {
        string[] ids = ["shadow.cascade.rigid", "shadow.cascade.skinned", "shadow.cascade.foliage", "opaque.rigid",
            "opaque.terrain", "opaque.foliage", "opaque.skinned", "water.surface", "ocean.fft.column", "ocean.fft.row",
            "post.bloom.bright", "post.bloom.horizontal", "post.bloom.vertical", "post.bloom.composite", "post.tonemap",
            "post.sharpen", "post.fxaa", "post.blit", "temporal.resolve", "temporal.depth-store", "background.sky",
            "background.starfield", "atmosphere.transmittance", "atmosphere.multiple-scattering", "atmosphere.sky-view",
            "outline.mask", "outline.composite", "display.fills", "display.lines", "ui.sprites",
            "device.scene.begin", "device.water.geometry", "device.water.bathymetry"];
        var passes = Enumerable.Range(0, 255).Select(i => new RendererPerfPassDefinition(
            ids[i < 252 ? i % 30 : i - 222], i < 252 ? i / 30 : 0, -1,
            i < 252 ? (uint)(i * 2 + 1) : 0, i < 252 ? (uint)(i * 2 + 2) : 0, i >= 252)).ToArray();
        var recorder = new RendererPerfRecorder(1, detailed ? RendererPerfLayout.PerPass : RendererPerfLayout.TotalOnly,
            detailed ? passes : [], () => 1, 1000);
        recorder.BeginFrame(0, 0, 0);
        recorder.Count(RendererPerfCounter.Draws, 2048);
        recorder.Count(RendererPerfCounter.BufferUploadBytes, 262144);
        recorder.Count(RendererPerfCounter.UnknownCopyBytes);
        recorder.EndFrame();
        var sample = recorder.Harvest(0, new GpuPerfTimestampResult([.. Enumerable.Range(0, (int)recorder.MarkerCount).Select(i => (ulong)i)], 1, true, ""));
        return sample with
        {
            Passes = [.. sample.Passes.Select(p => p.DeviceOnly ? p : p with
            { ElapsedTicks = 1, Duration = new(1, "ns", "native GPU timestamp interval", RendererPerfMetricStatus.Available, null) })]
        };
    }
}
