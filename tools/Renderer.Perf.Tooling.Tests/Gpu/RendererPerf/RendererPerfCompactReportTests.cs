using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    [Fact]
    public void Frame_metrics_encode_to_the_vector_the_metric_map_encoder_writes()
    {
        var report = RendererPerfProtocolTests.CompleteReport();
        Assert.True(report.Conditioning.Length >= 2 && report.Controls.Length >= 3 && !report.Blocks[0].Measured.IsEmpty);
        var harvested = RendererPerfFrameMetricRetentionTests.Harvest(4, 2);
        var native = new RendererPerfNativeSizeFixture().Replay(report.Blocks[0].Measured[0], true);
        native = native with { Metrics = native.Metrics.Add("signed", RendererPerfDriver.Value(-0d, "count", "signed delta")) };
        RendererPerfFrameSample[] frames = [report.Conditioning[0] with { Metrics = harvested[0].Metrics },
            report.Conditioning[1] with { Metrics = harvested[1].Metrics }, native];
        report = report with
        {
            Conditioning = report.Conditioning.SetItem(0, frames[0]).SetItem(1, frames[1]),
            Blocks = report.Blocks.SetItem(0, report.Blocks[0] with { Measured = report.Blocks[0].Measured.SetItem(0, frames[2]) }),
            Controls = [.. report.Controls.Select((control, i) => i < frames.Length
                ? control with { Metrics = frames[i].Metrics.ToImmutableDictionary(StringComparer.Ordinal) } : control)]
        };
        byte[] bytes = RendererPerfRawJson.Serialize(report, RendererPerfReportJson.MaximumBytes);
        var json = JsonNode.Parse(bytes)!;
        JsonNode?[] encoded = [json["conditioning"]![0]!["metrics"], json["conditioning"]![1]!["metrics"],
            json["blocks"]![0]!["measured"]![0]!["metrics"]];
        for (int i = 0; i < frames.Length; i++)
            Assert.True(JsonNode.DeepEquals(json["controls"]![i]!["metrics"], encoded[i]), $"Frame {i} encodes differently.");
        Assert.Equal(encoded[0]!["layout"]!.GetValue<int>(), encoded[1]!["layout"]!.GetValue<int>());
        Assert.False(JsonNode.DeepEquals(encoded[0]!["values"], encoded[1]!["values"]));
        var restored = RendererPerfRawJson.Parse(bytes, RendererPerfReportJson.MaximumBytes).Blocks[0].Measured[0].Metrics;
        Assert.Equal(frames[2].Metrics.OrderBy(p => p.Key, StringComparer.Ordinal), restored.OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    internal static IEnumerable<RendererPerfFrameSample> Frames(RendererPerfReport report) => report.Conditioning
        .Concat(report.Blocks.SelectMany(b => b.Warmup.Concat(b.Measured)))
        .Concat(report.Controls.SelectMany(c => c.Warmup.Concat(c.Measured)));
}
