using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoveragePerfAdapterIdentityTests
{
    [Theory]
    [InlineData("NVIDIA T400", "NVIDIA")]
    [InlineData("NVIDIA T4000", "NVIDIA")]
    [InlineData("Quadro T4 something", "NVIDIA")]
    [InlineData("NVIDIA T4x", "NVIDIA")]
    [InlineData("Tesla T4", "AMD")]
    [InlineData("Tesla T4", "NotNVIDIA")]
    public void SimilarModelNamesAndOtherVendorsCannotPassTheT4Gate(string adapter, string vendor)
    {
        var report = Device(CoveragePerfReportVectors.Create("G1"), adapter, vendor);
        var gate = CoveragePerfBudgets.Evaluate(report).Gpu;
        Assert.Equal(CoveragePerfBudgetStatus.Unproved, gate.Status);
        Assert.Contains(gate.Reasons, reason => reason.Contains("approved NVIDIA T4", StringComparison.Ordinal));
        Assert.All(gate.Pairs, pair => Assert.Equal(CoveragePerfBudgetStatus.Unproved, pair.Status));
    }

    [Theory]
    [InlineData("G1", "Tesla T4")]
    [InlineData("G2", "NVIDIA T4")]
    [InlineData("G3", "NVIDIA Tesla T4")]
    public void ExactT4ModelPassesAndReportsTheNameBasedIdentity(string preset, string adapter)
    {
        var report = Device(CoveragePerfReportVectors.Create(preset), adapter, "NVIDIA");
        Assert.Equal(CoveragePerfBudgetStatus.Pass, CoveragePerfBudgets.Evaluate(report).Gpu.Status);
        using var json = JsonDocument.Parse(CoveragePerfReportJson.Serialize(report));
        var session = json.RootElement.GetProperty("report").GetProperty("blocks")[0].GetProperty("session");
        Assert.True(session.TryGetProperty("hardwareIdentityBasis", out var basis));
        Assert.Equal("name-based (PCI vendor/device IDs unavailable through the device seam)", basis.GetString());
        var copy = CoveragePerfReportJson.Deserialize(json.RootElement.GetRawText());
        Assert.Equal(report.Blocks[0].Session, copy.Blocks[0].Session! with { Environment = report.Blocks[0].Session!.Environment });
        Assert.Contains(adapter, CoveragePerfReportJson.Markdown(report));
        Assert.Contains("name-based", CoveragePerfReportJson.Markdown(report));
    }

    static CoveragePerfReport Device(CoveragePerfReport report, string adapter, string vendor) => report with
    {
        Blocks = report.Blocks.Select(block => block with
        { Session = block.Session! with { Adapter = adapter, Device = adapter, Vendor = vendor } }).ToImmutableArray()
    };
}
