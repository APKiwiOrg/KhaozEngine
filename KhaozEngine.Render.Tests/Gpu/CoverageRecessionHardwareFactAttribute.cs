using System;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Hardware-only opt-in for an explicitly selected Diagnostic measurement.</summary>
public sealed class CoverageRecessionHardwareFactAttribute : FactAttribute
{
    static readonly Lazy<string?> Unavailable = new(Probe);
    public CoverageRecessionHardwareFactAttribute()
    {
        Skip = Environment.GetEnvironmentVariable("KE_GPU_TESTS") == "1"
            ? Unavailable.Value : "Coverage cost requires an approved KE_GPU_TESTS=1 invocation.";
    }

    static string? Probe()
    {
        try
        {
            using var context = GpuDeviceContext.CreateHeadless();
            var device = context.GpuDevice;
            if (device.Backend is not (GpuBackendKind.MetalNative or GpuBackendKind.VulkanNative or GpuBackendKind.Direct3D11Native)
                || device.Diagnostics.SoftwareAdapter != false || GpuFactAttribute.IsVirtualisedAdapter(device.Capabilities.DeviceName))
                return "Coverage cost requires a native nonsoftware, nonvirtualized hardware device.";
            return device.Capabilities.SupportsCompute && device.Capabilities.SupportsTimestamps && device.Capabilities.SupportsCompletionFences
                ? null : "Coverage cost requires compute, timestamps and completion fences.";
        }
        catch (Exception error) { return "Native hardware unavailable: " + error.Message; }
    }
}
