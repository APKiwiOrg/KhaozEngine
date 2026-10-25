using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfDeviceReportTests
{
    internal static void AssertPublishable(RendererPerfFrameSample sample)
    {
        RendererPerfReport report = RendererPerfProtocolTests.CompleteReport();
        RendererPerfBlockReport block = report.Blocks[1];
        report = report with
        {
            Blocks = report.Blocks.SetItem(1, block with
            {
                Measured = block.Measured.SetItem(0, sample with { FrameId = 64 })
            })
        };
        foreach (bool completed in new[] { false, true })
        {
            RendererPerfReport candidate = completed ? report : report with
            {
                Assessment = report.Assessment with { ValidForPromotion = false, Reasons = ["partial fixture"] },
                Publication = report.Publication with
                {
                    State = RendererPerfPublicationState.Failed,
                    FinalUnitPublished = false,
                    OriginalFailure = "partial fixture",
                    Errors = ["partial fixture"]
                }
            };
            byte[] bytes = RendererPerfReportJson.Serialize(candidate);
            RendererPerfReport restored = RendererPerfReportJson.Parse(bytes);
            Assert.Equal(completed, restored.Assessment.ValidForPromotion);
            Assert.Equal(sample.Passes.ToArray(), restored.Blocks[1].Measured[0].Passes.ToArray());
            using JsonDocument json = JsonDocument.Parse(bytes);
            foreach (JsonElement pass in json.RootElement.GetProperty("blocks")[1].GetProperty("measured")[0].GetProperty("passes").EnumerateArray())
                Assert.Equal(json.RootElement.GetProperty("raw").GetProperty("passes")[pass[0].GetInt32()]
                    .GetProperty("id").GetString()!.StartsWith("device.", StringComparison.Ordinal),
                    json.RootElement.GetProperty("raw").GetProperty("passes")[pass[0].GetInt32()].GetProperty("deviceOnly").GetBoolean());
        }
    }

    [Theory]
    [InlineData("zero-markers")]
    [InlineData("reversed-markers")]
    [InlineData("unsupported")]
    public void Gpu_rows_keep_marker_and_promotion_requirements(string fault)
    {
        var pass = new RendererPerfPassSample("opaque", 0, null, null, 1, 2, 1,
            new(1, "ns", "native GPU timestamps", RendererPerfMetricStatus.Available, null), false);
        pass = fault switch
        {
            "zero-markers" => pass with { BeginMarker = 0, EndMarker = 0 },
            "reversed-markers" => pass with { BeginMarker = 2, EndMarker = 1 },
            _ => pass with
            {
                ElapsedTicks = null,
                Duration = new(null, "ns", "native GPU timestamps",
                RendererPerfMetricStatus.Unsupported, "no GPU timestamps")
            }
        };
        RendererPerfReport report = RendererPerfProtocolTests.CompleteReport();
        RendererPerfBlockReport block = report.Blocks[1];
        report = report with
        {
            Blocks = report.Blocks.SetItem(1, block with
            {
                Measured = block.Measured.SetItem(0, block.Measured[0] with { Passes = [pass] })
            })
        };
        var error = Assert.Throws<RendererPerfProtocolException>(() => RendererPerfReportJson.Serialize(report));
        Assert.Equal(fault == "unsupported" ? "Publication" : "Sample", error.Code);
    }

    [Theory]
    [InlineData("markers")]
    [InlineData("ticks")]
    [InlineData("duration")]
    public void Device_rows_cannot_claim_gpu_intervals(string fault)
    {
        var json = JsonNode.Parse(RendererPerfReportJson.Serialize(RendererPerfProtocolTests.CompleteReport()))!.AsObject();
        JsonNode pass = new JsonObject
        {
            ["id"] = "device.water.geometry",
            ["occurrence"] = 0,
            ["parentId"] = null,
            ["parentOccurrence"] = null,
            ["beginMarker"] = fault == "markers" ? 1 : 0,
            ["endMarker"] = fault == "markers" ? 2 : 0,
            ["elapsedTicks"] = fault is "ticks" or "duration" ? 1 : null,
            ["deviceOnly"] = true,
            ["duration"] = new JsonObject
            {
                ["value"] = fault == "duration" ? 1 : null,
                ["unit"] = "ns",
                ["source"] = "CPU-only device owner",
                ["status"] = fault == "duration" ? "available" : "unsupported",
                ["reason"] = fault == "duration" ? null : "no GPU interval"
            }
        };
        pass = RendererPerfRawTestJson.Pass(json, pass);
        ((JsonArray)json["blocks"]![1]!["measured"]![0]!["passes"]!).Add(pass);
        var error = Assert.Throws<RendererPerfProtocolException>(() => RendererPerfReportJson.Parse(Encoding.UTF8.GetBytes(json.ToJsonString())));
        Assert.Equal("Sample", error.Code);
    }
}
