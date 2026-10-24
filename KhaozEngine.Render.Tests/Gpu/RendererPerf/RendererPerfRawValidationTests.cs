using System;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfRawValidationTests
{
    [Theory]
    [InlineData("missing-raw")]
    [InlineData("unknown-format")]
    [InlineData("missing-table")]
    [InlineData("unknown-field")]
    [InlineData("duplicate-field")]
    [InlineData("duplicate-schema")]
    [InlineData("duplicate-layout")]
    [InlineData("null-schema")]
    [InlineData("missing-reason-field")]
    [InlineData("empty-source")]
    [InlineData("unknown-status")]
    [InlineData("empty-name")]
    [InlineData("negative-layout")]
    [InlineData("foreign-layout")]
    [InlineData("negative-schema")]
    [InlineData("foreign-schema")]
    [InlineData("duplicate-name")]
    [InlineData("wrong-slot-type")]
    [InlineData("missing-values")]
    [InlineData("extra-values")]
    [InlineData("negative-run")]
    [InlineData("extra-zero")]
    [InlineData("explicit-positive-zero")]
    [InlineData("null-value")]
    [InlineData("unknown-vector-field")]
    [InlineData("numeric-unavailable")]
    [InlineData("missing-scalar-value")]
    [InlineData("foreign-cpu")]
    [InlineData("foreign-pass")]
    [InlineData("foreign-zero-run")]
    [InlineData("duplicate-zero-run")]
    [InlineData("empty-zero-run")]
    public void Malformed_compact_data_cannot_bypass_strict_validation(string fault)
    {
        var report = RendererPerfProtocolTests.CompleteReport();
        var metrics = ImmutableDictionary<string, RendererPerfMetric>.Empty
            .Add("a", new(2, "count", "test counter", RendererPerfMetricStatus.Available, null))
            .Add("b", new(0, "bytes", "test payload", RendererPerfMetricStatus.Available, null))
            .Add("c", new(null, "count", "optional counter", RendererPerfMetricStatus.Unsupported, "no counter"));
        report = report with { Conditioning = report.Conditioning.SetItem(0, report.Conditioning[0] with { Metrics = metrics }) };
        var json = JsonNode.Parse(RendererPerfReportJson.Serialize(report))!.AsObject();
        var raw = json["raw"]!.AsObject();
        var schemas = raw["metrics"]!.AsArray();
        var layouts = raw["layouts"]!.AsArray();
        var frame = json["conditioning"]![0]!;
        var vector = frame["metrics"]!.AsObject();
        var layout = layouts[vector["layout"]!.GetValue<int>()]!.AsArray();
        var zeroRuns = raw["zeroRuns"]![vector["zeroRuns"]!.GetValue<int>()]!.AsArray();
        int first = layout[0]!.GetValue<int>();
        switch (fault)
        {
            case "missing-raw": json.Remove("raw"); break;
            case "unknown-format": raw["format"] = "metric-vectors/unknown"; break;
            case "missing-table": raw.Remove("metrics"); break;
            case "unknown-field": raw["unknown"] = true; break;
            case "duplicate-schema": schemas.Add(schemas[first]!.DeepClone()); break;
            case "duplicate-layout": layouts.Add(layout.DeepClone()); break;
            case "null-schema": schemas[first] = null; break;
            case "missing-reason-field": schemas[first]!.AsObject().Remove("reason"); break;
            case "empty-source": schemas[first]!["source"] = ""; break;
            case "unknown-status": schemas[first]!["status"] = "unmeasured-success"; break;
            case "empty-name": schemas[first]!["name"] = ""; break;
            case "negative-layout": vector["layout"] = -1; break;
            case "foreign-layout": vector["layout"] = int.MaxValue; break;
            case "negative-schema": layout[0] = -1; break;
            case "foreign-schema": layout[0] = int.MaxValue; break;
            case "duplicate-name": layout.Add(layout[0]!.DeepClone()); break;
            case "wrong-slot-type": vector["values"]![0] = "two"; break;
            case "missing-values": vector["values"]!.AsArray().Clear(); break;
            case "extra-values": vector["values"]!.AsArray().Add(3); break;
            case "negative-run": zeroRuns[0] = -1; break;
            case "extra-zero": zeroRuns[1] = 2; break;
            case "foreign-zero-run": vector["zeroRuns"] = int.MaxValue; break;
            case "duplicate-zero-run": raw["zeroRuns"]!.AsArray().Add(zeroRuns.DeepClone()); break;
            case "empty-zero-run": zeroRuns.Clear(); break;
            case "explicit-positive-zero": vector["values"]![0] = 0; break;
            case "null-value": vector["values"]![0] = null; break;
            case "unknown-vector-field": vector["extra"] = 1; break;
            case "numeric-unavailable":
                RendererPerfRawTestJson.Unavailable(json, frame["gpuFrame"]!, "no timing");
                frame["gpuFrame"]!["value"] = 0; break;
            case "missing-scalar-value": frame["gpuFrame"]!.AsObject().Remove("value"); break;
            case "foreign-cpu":
                frame["cpuPhases"]!["schemas"]!.AsArray().Add(int.MaxValue);
                frame["cpuPhases"]!["startOffsets"]!.AsArray().Add(0);
                frame["cpuPhases"]!["durations"]!.AsArray().Add(0); break;
            case "foreign-pass":
                json["blocks"]![1]!["measured"]![0]!["passes"]!.AsArray().Add(new JsonArray(
                    int.MaxValue, new JsonArray(), frame["gpuFrame"]!["schema"]!.DeepClone(), new JsonArray())); break;
        }
        string text = json.ToJsonString();
        if (fault == "duplicate-field") text = text.Insert(1, "\"raw\":{},");
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfReportJson.Parse(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void Zero_runs_preserve_signed_values_and_every_unavailable_reason()
    {
        var report = RendererPerfProtocolTests.CompleteReport();
        var metrics = ImmutableDictionary<string, RendererPerfMetric>.Empty
            .Add("negative", new(-5, "count", "signed delta", RendererPerfMetricStatus.Available, null))
            .Add("negative-zero", new(-0d, "count", "signed delta", RendererPerfMetricStatus.Available, null))
            .Add("zero", new(0, "count", "signed delta", RendererPerfMetricStatus.Available, null))
            .Add("missing", new(null, "count", "optional", RendererPerfMetricStatus.Unsupported, "counter absent"))
            .Add("invalid", new(null, "count", "optional", RendererPerfMetricStatus.Invalid, "counter lost"));
        report = report with { Conditioning = report.Conditioning.SetItem(0, report.Conditioning[0] with { Metrics = metrics }) };
        var restored = RendererPerfReportJson.Parse(RendererPerfReportJson.Serialize(report)).Conditioning[0].Metrics;
        Assert.Equal(-5, restored["negative"].Value);
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(restored["negative-zero"].Value!.Value));
        Assert.Equal(0, BitConverter.DoubleToInt64Bits(restored["zero"].Value!.Value));
        Assert.Equal(metrics["missing"], restored["missing"]);
        Assert.Equal(metrics["invalid"], restored["invalid"]);
    }

    [Fact]
    public void Positive_zero_vector_is_rejected_after_a_matching_negative_zero_cache_entry()
    {
        var report = RendererPerfProtocolTests.CompleteReport();
        var metrics = ImmutableDictionary<string, RendererPerfMetric>.Empty
            .Add("signed", new(-0d, "count", "signed delta", RendererPerfMetricStatus.Available, null));
        report = report with
        {
            Conditioning = report.Conditioning.SetItem(0, report.Conditioning[0] with { Metrics = metrics })
                .SetItem(1, report.Conditioning[1] with { Metrics = metrics })
        };
        var json = JsonNode.Parse(RendererPerfReportJson.Serialize(report))!;
        json["conditioning"]![1]!["metrics"]!["values"]![0] = 0d;
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfReportJson.Parse(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pass_duration_cache_preserves_both_zero_signs(bool positiveFirst)
    {
        var report = RendererPerfProtocolTests.CompleteReport();
        var block = report.Blocks[1];
        RendererPerfPassSample Pass(double value) => new("post.blit", 0, null, null, 1, 2, 0,
            new(value, "ns", "held zero interval", RendererPerfMetricStatus.Available, null), false);
        var first = Pass(positiveFirst ? 0d : -0d);
        var second = Pass(positiveFirst ? -0d : 0d);
        report = report with
        {
            Blocks = report.Blocks.SetItem(1, block with
            {
                Measured = block.Measured.SetItem(0, block.Measured[0] with { Passes = [first] })
                    .SetItem(1, block.Measured[1] with { Passes = [second] })
            })
        };
        var restored = RendererPerfReportJson.Parse(RendererPerfReportJson.Serialize(report)).Blocks[1].Measured;
        Assert.Equal(positiveFirst ? 0L : long.MinValue, BitConverter.DoubleToInt64Bits(restored[0].Passes[0].Duration.Value!.Value));
        Assert.Equal(positiveFirst ? long.MinValue : 0L, BitConverter.DoubleToInt64Bits(restored[1].Passes[0].Duration.Value!.Value));
    }
}
