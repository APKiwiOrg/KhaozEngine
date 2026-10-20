using System;
using KhaozEngine.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

// Setup evidence only. No frames, queries, samples or elapsed times are collected here.
public sealed class CoveragePerfDeviceFactsGpuTests(ITestOutputHelper output)
{
    [GpuFact]
    public void Hosted_device_reports_native_T4_identity_and_timestamp_capability()
    {
        using var context = GpuDeviceContext.CreateHeadless();
        var caps = context.Capabilities;
        output.WriteLine($"backend={context.Backend} adapter='{caps.DeviceName}' vendor={caps.PciVendorId:x} device={caps.PciDeviceId:x} driver='{caps.DriverVersion}' timestamps={caps.SupportsTimestamps} compute={caps.SupportsCompute}");
        // Ordinary GPU correctness runs still execute this diagnostic on their own backend.
        // The stronger hosted admission checks apply only to an explicitly selected performance dispatch.
        Assert.Equal(caps, context.GpuDevice.Capabilities);
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KE_COVERAGE_PERF_UNIT"))) return;
        Assert.Equal(0x10deu, caps.PciVendorId);
        Assert.Equal(0x1eb8u, caps.PciDeviceId);
        Assert.True(CoveragePerfHardwareIdentity.IsT4(new()
        {
            Adapter = caps.DeviceName,
            Vendor = CoveragePerfHardwareIdentity.FormatId(caps.PciVendorId),
            Device = CoveragePerfHardwareIdentity.FormatId(caps.PciDeviceId),
            HardwareIdentityBasis = CoveragePerfHardwareIdentity.Basis(caps),
        }));
        Assert.False(string.IsNullOrWhiteSpace(caps.DriverVersion));
        Assert.True(caps.SupportsTimestamps);
        Assert.True(caps.SupportsCompute);
        string leg = Environment.GetEnvironmentVariable("KE_COVERAGE_PERF_LEG") ?? "";
        Assert.Equal(leg == "windows-d3d11" ? GpuBackendKind.Direct3D11Native : GpuBackendKind.VulkanNative, context.Backend);
    }
}
