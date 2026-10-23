using System;
using System.Linq;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed partial class RendererPerfCompilerProtocolTests
{
    [Theory]
    [InlineData("unavailable-hash")]
    [InlineData("available-reason")]
    [InlineData("missing-reason")]
    [InlineData("missing-emitted")]
    [InlineData("null-digest")]
    [InlineData("null-identity")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("missing-count")]
    [InlineData("missing-compiler")]
    [InlineData("missing-scalar-member")]
    [InlineData("raw-v1")]
    public void Every_nullable_contract_property_is_required_and_conflicts_are_refused(string fault)
    {
        var json = JsonNode.Parse(RendererPerfReportJson.Serialize(RendererPerfProtocolTests.CompleteReport()))!.AsObject();
        var identity = json["pipelines"]![0]!["identity"]!;
        var digest = identity["sourceSha256"]!;
        switch (fault)
        {
            case "unavailable-hash": digest["status"] = "unsupported"; digest["reason"] = "missing"; break;
            case "available-reason": digest["reason"] = "cannot be available"; break;
            case "missing-reason": digest.AsObject().Remove("reason"); break;
            case "missing-emitted": identity["stages"]![0]!.AsObject().Remove("emittedSourceSha256"); break;
            case "null-digest": identity["sourceSha256"] = null; break;
            case "null-identity": json["pipelines"]![0]!["identity"] = null; break;
            case "unknown": digest["guessed"] = 1; break;
            case "missing-count": json["compiler"]!["results"]![0]!["read"]!["counts"]!.AsObject().Remove("omittedStatistics"); break;
            case "missing-compiler": json.Remove("compiler"); break;
            case "missing-scalar-member": json["pipelines"]![0]!["executables"]![0]!["statistics"]![0]!["metric"]!["value"]!.AsObject().Remove("boolean"); break;
            case "raw-v1": json["raw"]!["format"] = "metric-vectors/1"; break;
        }
        string text = json.ToJsonString();
        if (fault == "duplicate") text = text.Replace("\"sha256\":", "\"sha256\":null,\"sha256\":", StringComparison.Ordinal);
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfReportJson.Parse(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void Stage_aggregates_match_independent_little_endian_vectors()
    {
        GpuDiagnosticDigest Digest(string hash, string source) => new(hash, source, GpuDiagnosticStatus.Available, null);
        var first = new GpuPipelineStageIdentity("vertex", "main", Digest(new('0', 64), "glsl.utf8/1"), null, Digest(new('f', 64), "spirv.bytes/1"));
        var second = new GpuPipelineStageIdentity("fragment", "entry", Digest(new('f', 64), "glsl.utf8/1"), null, Digest(new('0', 64), "spirv.bytes/1"));
        Assert.Equal("969b55c3b1ff0b8870b47011b5bb1da61f970bd85c2b5989288377850a3219bd", GpuDiagnosticIdentityEncoding.Stages("source", [first, second]).Sha256);
        Assert.Equal("e736908b0c1fec278691a65118a7faff4122a860e4fad66617da6e473f6cdab5", GpuDiagnosticIdentityEncoding.Stages("artifact", [first, second]).Sha256);
        Assert.NotEqual(GpuDiagnosticIdentityEncoding.Stages("source", [first, second]), GpuDiagnosticIdentityEncoding.Stages("source", [second, first]));
        Assert.Equal(first.SourceSha256, GpuDiagnosticIdentityEncoding.Stages("source", [first]));
        Assert.Throws<ArgumentException>(() => GpuDiagnosticIdentityEncoding.Stages("unknown", [first]));
        Assert.Throws<ArgumentException>(() => GpuDiagnosticIdentityEncoding.CodeGeneration((GpuBackendKind)999, []));
        Assert.Throws<EncoderFallbackException>(() => GpuDiagnosticIdentityEncoding.CodeGeneration(GpuBackendKind.VulkanNative, [new("key", "\ud800")]));
    }

    [Fact]
    public void Configuration_and_codegen_match_independent_vectors_and_keep_observation_separate()
    {
        System.Collections.Immutable.ImmutableArray<GpuDiagnosticOption> options = [new("compiler", "frontend/1"), new("precision", "strict")];
        var config = new GpuEffectiveDiagnosticConfiguration(GpuBackendKind.VulkanNative, "adapter-uuid", "driver",
            new(true, false), true, false, false, false, true, true, 0x40, options, [new("cache", "off")]);
        Assert.Equal("66a9608dd4af91c4d65ef3d6b55ea5dacdfe87ff1cfa224ac28b55dd56752acf", GpuDiagnosticIdentityEncoding.CodeGeneration(GpuBackendKind.VulkanNative, options).Sha256);
        Assert.Equal("546a4910735441fa89e16a5b993ac1989bb454715e207c56c088d7396da4c053", GpuDiagnosticIdentityEncoding.Configuration(config).Sha256);
        Assert.NotEqual(GpuDiagnosticIdentityEncoding.Configuration(config), GpuDiagnosticIdentityEncoding.Configuration(config with { RuntimeInvocationsEnabled = true }));
        Assert.Equal(GpuDiagnosticIdentityEncoding.CodeGeneration(config.Backend, config.CodeGenerationOptions),
            GpuDiagnosticIdentityEncoding.CodeGeneration(config.Backend, (config with { RuntimeInvocationsEnabled = true }).CodeGenerationOptions));
        Assert.Throws<ArgumentException>(() => GpuDiagnosticIdentityEncoding.CodeGeneration(config.Backend, [new("b", "1"), new("a", "2")]));
        Assert.Throws<ArgumentException>(() => GpuDiagnosticIdentityEncoding.CodeGeneration(config.Backend, [new("a", "1"), new("a", "2")]));
    }

    [Fact]
    public void Typed_scalar_roundtrip_keeps_both_int64_extremes_boolean_and_float_bits()
    {
        var report = RendererPerfProtocolTests.CompleteReport();
        var pipeline = report.Pipelines[0];
        var row = pipeline.Executables[0];
        var original = row.Statistics[0];
        var values = new[] { new GpuDiagnosticValue(GpuDiagnosticValueType.Int64, Int64: long.MinValue),
            new(GpuDiagnosticValueType.Int64, Int64: long.MaxValue), new(GpuDiagnosticValueType.UInt64, UInt64: ulong.MaxValue),
            new(GpuDiagnosticValueType.Boolean, Boolean: false), new(GpuDiagnosticValueType.Float64, Float64: -0.0),
            new(GpuDiagnosticValueType.Float64, Float64: double.MinValue) };
        var statistics = values.Select((v, i) => original with { Name = "value " + i, Metric = original.Metric with { Value = v } }).ToImmutableArray();
        pipeline = pipeline with { Executables = [row with { Statistics = statistics }] };
        report = report with
        {
            Pipelines = [pipeline],
            Compiler = report.Compiler with
            {
                Results = [report.Compiler.Results[0] with { Read = report.Compiler.Results[0].Read! with
            { Counts = new(true, 1, 6, 1, 6, 0, 0, true, null, null) } }]
            }
        };
        var restored = RendererPerfReportJson.Parse(RendererPerfReportJson.Serialize(report)).Pipelines[0].Executables[0].Statistics;
        Assert.Equal(values, restored.Select(s => s.Metric.Value));
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0.0), BitConverter.DoubleToInt64Bits(restored[4].Metric.Value!.Float64!.Value));
    }
}
