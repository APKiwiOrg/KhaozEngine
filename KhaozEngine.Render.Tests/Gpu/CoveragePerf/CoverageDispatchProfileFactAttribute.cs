using System;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Opt-in diagnostic that requires hardware timestamps rather than a software adapter.</summary>
public sealed class CoverageDispatchProfileFactAttribute : FactAttribute
{
    static readonly Lazy<string?> Unavailable = new(Probe);

    public CoverageDispatchProfileFactAttribute()
    {
        Skip = Environment.GetEnvironmentVariable("KE_GPU_TESTS") == "1"
            ? Unavailable.Value : "Set KE_GPU_TESTS=1 to run the coverage dispatch diagnostic.";
    }

    static string? Probe()
    {
        try
        {
            using var context = GpuDeviceContext.CreateHeadless();
            var device = context.GpuDevice;
            if (device.Backend is not (GpuBackendKind.MetalNative or GpuBackendKind.VulkanNative or GpuBackendKind.Direct3D11Native)
                || device.Diagnostics.SoftwareAdapter == true || GpuFactAttribute.IsVirtualisedAdapter(device.Capabilities.DeviceName))
                return "The coverage dispatch diagnostic requires a native hardware GPU.";
            return device.Capabilities.SupportsCompute && device.Capabilities.SupportsTimestamps && device.Capabilities.SupportsCompletionFences
                ? null : "The native GPU does not support compute, timestamps and completion fences.";
        }
        catch (Exception error)
        {
            return "No native timestamp device is available: " + error.Message;
        }
    }
}
