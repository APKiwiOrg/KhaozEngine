using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfCpuIntervalsTests
{
    [Theory]
    [InlineData(long.MinValue, long.MaxValue)]
    [InlineData(9_000_000_000_000_001L, 9_000_000_000_001_003L)]
    public void Relative_columns_preserve_every_tick_parent_and_segment_order(long start, long end)
    {
        var report = RendererPerfProtocolTests.CompleteReport();
        var frame = report.Conditioning[0] with
        {
            CpuStartTicks = start,
            CpuEndTicks = end,
            CpuPhases = [new("child", "parent", start + 1, end - 1), new("parent", null, start, start + 2)]
        };
        report = report with { Conditioning = report.Conditioning.SetItem(0, frame) };
        var restored = RendererPerfReportJson.Parse(RendererPerfReportJson.Serialize(report)).Conditioning[0];
        Assert.Equal(frame.CpuPhases.ToArray(), restored.CpuPhases.ToArray());
        Assert.Equal(start, restored.CpuStartTicks);
        Assert.Equal(end, restored.CpuEndTicks);
    }

    [Theory]
    [InlineData("missing-column")]
    [InlineData("extra-column")]
    [InlineData("short-column")]
    [InlineData("null-column")]
    [InlineData("negative-offset")]
    [InlineData("fractional-duration")]
    [InlineData("start-overflow")]
    [InlineData("end-overflow")]
    [InlineData("foreign-schema")]
    public void Malformed_relative_intervals_are_rejected(string fault)
    {
        var report = RendererPerfProtocolTests.CompleteReport();
        var frame = report.Conditioning[0];
        report = report with
        {
            Conditioning = report.Conditioning.SetItem(0,
                frame with { CpuPhases = [new("prepare", null, frame.CpuStartTicks, frame.CpuEndTicks)] })
        };
        var json = JsonNode.Parse(RendererPerfReportJson.Serialize(report))!;
        var columns = json["conditioning"]![0]!["cpuPhases"]!;
        switch (fault)
        {
            case "missing-column": columns.AsObject().Remove("durations"); break;
            case "extra-column": columns["other"] = 1; break;
            case "short-column": columns["durations"]!.AsArray().Clear(); break;
            case "null-column": columns["schemas"] = null; break;
            case "negative-offset": columns["startOffsets"]![0] = -1; break;
            case "fractional-duration": columns["durations"]![0] = 0.5; break;
            case "start-overflow": columns["startOffsets"]![0] = ulong.MaxValue; break;
            case "end-overflow": columns["durations"]![0] = ulong.MaxValue; break;
            case "foreign-schema": columns["schemas"]![0] = int.MaxValue; break;
        }
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfReportJson.Parse(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }
}
