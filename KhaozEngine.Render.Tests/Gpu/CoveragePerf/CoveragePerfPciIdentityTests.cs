using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoveragePerfPciIdentityTests
{
    const string PciBasis = "PCI-based (reported vendor/device IDs with adapter-name corroboration)";

    [Theory]
    [InlineData("G1", "Tesla T4")]
    [InlineData("G2", "NVIDIA Tesla T4")]
    [InlineData("G3", "Tesla T4")]
    public void HostedT4IdentitiesPassAndDisclosePciCorroboration(string preset, string adapter)
    {
        var report = Identity(preset, adapter, "0x10de", "0x1eb8");
        Assert.Equal(CoveragePerfBudgetStatus.Pass, CoveragePerfBudgets.Evaluate(report).Gpu.Status);
        using var json = JsonDocument.Parse(CoveragePerfReportJson.Serialize(report));
        var session = json.RootElement.GetProperty("report").GetProperty("blocks")[0].GetProperty("session");
        Assert.Equal(PciBasis, session.GetProperty("hardwareIdentityBasis").GetString());
        Assert.Equal("0x10de", session.GetProperty("vendor").GetString());
        Assert.Equal("0x1eb8", session.GetProperty("device").GetString());
        Assert.Contains(PciBasis, CoveragePerfReportJson.Markdown(report));
    }

    [Theory]
    [InlineData("NVIDIA T400", "0x10de", "0x1fb2")]
    [InlineData("NVIDIA T4000", "0x10de", "0x1eb9")]
    [InlineData("Tesla T4", "0x1002", "0x1eb8")]
    [InlineData("Tesla T4", "0x10de", "0x1fb2")]
    [InlineData("NVIDIA T400", "0x10de", "0x1eb8")]
    [InlineData("Tesla T4", "0x10de", "T4")]
    [InlineData("Tesla T4", "NVIDIA", "0x1eb8")]
    public void WrongOrPartialIdsCannotUseTheNameFallback(string adapter, string vendor, string device)
    {
        var report = Identity("G1", adapter, vendor, device);
        Assert.Equal(CoveragePerfBudgetStatus.Unproved, CoveragePerfBudgets.Evaluate(report).Gpu.Status);
        report = Map(report, info => info with { HardwareIdentityBasis = CoveragePerfHardwareIdentity.NameBased });
        Assert.Equal(CoveragePerfBudgetStatus.Unproved, CoveragePerfBudgets.Evaluate(report).Gpu.Status);
    }

    [Theory]
    [InlineData("C0")]
    [InlineData("C1")]
    public void RealMesaSoftwareIdentityPassesIncludingItsZeroDeviceId(string preset)
    {
        // The recorded native device string is in VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md.
        var report = Identity(preset, "llvmpipe (LLVM 20.1.2, 256 bits)", "0x10005", "0x0000");
        Assert.Empty(CoveragePerfValidity.Reasons(report));
        if (preset == "C1") Assert.Equal(CoveragePerfBudgetStatus.Pass, CoveragePerfBudgets.Evaluate(report).Cpu.Status);
    }

    [Theory]
    [InlineData("0x10de", "llvmpipe (LLVM 20.1.2, 256 bits)", true)]
    [InlineData("0x10005", "NVIDIA Tesla T4", true)]
    [InlineData("0x10005", "notllvmpipe", true)]
    [InlineData("0x10005", "llvmpipe (LLVM 20.1.2, 256 bits)", false)]
    [InlineData("0x10005", "llvmpipe (LLVM 20.1.2, 256 bits)", true, "unavailable")]
    public void MesaAdmissionNeedsItsVendorNameSoftwareFlagAndDriver(string vendor, string adapter, bool software,
        string driver = "reported driver")
    {
        var report = Identity("C1", adapter, vendor, "0x0000");
        report = Map(report, info => info with { SoftwareAdapter = software, Driver = driver });
        Assert.Equal(CoveragePerfBudgetStatus.Unproved, CoveragePerfBudgets.Evaluate(report).Cpu.Status);
    }

    [Theory]
    [InlineData("G1", "unavailable")]
    [InlineData("G1", "")]
    [InlineData("G1", " ")]
    [InlineData("C1", "unavailable")]
    public void ResolvedDriverIsRequiredForAdmission(string preset, string driver)
    {
        var report = Identity(preset, preset == "G1" ? "Tesla T4" : "llvmpipe (LLVM 20.1.2, 256 bits)",
            preset == "G1" ? "0x10de" : "0x10005", preset == "G1" ? "0x1eb8" : "0x0000");
        report = Map(report, info => info with { Driver = driver });
        Assert.Contains(CoveragePerfValidity.Reasons(report), reason => reason.Contains("driver identity is unresolved", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0x10deu, 0x1eb8u, "0x10de", "0x1eb8")]
    [InlineData(0x10005u, 0u, "0x10005", "0x0000")]
    public void NativeSessionReadsReportedIdsAndDriverWithoutInferringFromTheName(uint vendor, uint device,
        string expectedVendor, string expectedDevice)
    {
        using var run = CoveragePerfSceneProbe.Open(1, covered: false);
        run.ReportedCapabilities = new GpuCapabilities(false, true, "reported adapter")
        { PciVendorId = vendor, PciDeviceId = device, DriverVersion = "reported driver" };
        var info = run.Session.ReadInfo();
        Assert.Equal(expectedVendor, info.Vendor);
        Assert.Equal(expectedDevice, info.Device);
        Assert.Equal("reported driver", info.Driver);
        Assert.Equal(PciBasis, info.HardwareIdentityBasis);
    }

    [Fact]
    public void NativeSessionPreservesUnansweredIdentityAndDisclosesTheNameBasis()
    {
        using var run = CoveragePerfSceneProbe.Open(1, covered: false);
        var info = run.Session.ReadInfo();
        Assert.Equal("unavailable", info.Vendor);
        Assert.Equal("unavailable", info.Device);
        Assert.Equal("unavailable", info.Driver);
        Assert.Equal(CoveragePerfHardwareIdentity.NameBased, info.HardwareIdentityBasis);
    }

    static CoveragePerfReport Identity(string preset, string adapter, string vendor, string device)
        => Map(CoveragePerfReportVectors.Create(preset), info => info with
        { Adapter = adapter, Vendor = vendor, Device = device, HardwareIdentityBasis = PciBasis, Driver = "reported driver" });

    static CoveragePerfReport Map(CoveragePerfReport report, Func<CoveragePerfSessionInfo, CoveragePerfSessionInfo> change)
        => report with { Blocks = report.Blocks.Select(b => b with { Session = change(b.Session!) }).ToImmutableArray() };
}
