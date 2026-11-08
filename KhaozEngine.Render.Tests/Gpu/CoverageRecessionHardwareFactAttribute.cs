using System;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Hardware-only opt-in for an explicitly selected Diagnostic measurement.</summary>
public sealed class CoverageRecessionHardwareFactAttribute : FactAttribute
{
    static readonly Lazy<string?> Unavailable = new(Probe);
    internal const string CostUnitName = "Real_body_size_carry_and_scene_overhead_for_1_and_32_actors";
    public CoverageRecessionHardwareFactAttribute()
        : this(Environment.GetEnvironmentVariable("KE_GPU_TESTS"), Environment.GetEnvironmentVariable("KE_COVERAGE_COST_UNIT"),
            static () => Unavailable.Value)
    { }

    // The injected probe lets headless tests exercise the exact metadata gate without opening a device.
    internal CoverageRecessionHardwareFactAttribute(string? gpuTests, string? costUnit, Func<string?> probe)
    {
        Skip = gpuTests != "1" ? "Coverage cost requires an approved KE_GPU_TESTS=1 invocation."
            : costUnit != CostUnitName ? "Set KE_COVERAGE_COST_UNIT to the exact separately approved cost method."
            : probe();
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
