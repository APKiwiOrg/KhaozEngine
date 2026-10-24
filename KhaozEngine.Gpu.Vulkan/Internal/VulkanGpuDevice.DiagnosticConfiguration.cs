using System;
using System.Globalization;
using KhaozEngine.Gpu.Internal;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal;

internal sealed unsafe partial class VulkanGpuDevice : IGpuDeviceDiagnosticConfiguration
{
    GpuEffectiveDiagnosticConfiguration? _diagnosticConfiguration;
    internal VulkanPipelineCacheIdentity? DiagnosticAdapterIdentity { get; private set; }
    internal VulkanValidationMode DiagnosticValidationRequest { get; private set; }
    public GpuEffectiveDiagnosticConfiguration DiagnosticConfiguration => _diagnosticConfiguration
        ?? throw new InvalidOperationException("This device did not use explicit frozen diagnostic settings.");
    static string AdapterKey(Vk vk, PhysicalDevice device)
    {
        var id = new PhysicalDeviceIDProperties { SType = StructureType.PhysicalDeviceIDProperties };
        var properties = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &id };
        vk.GetPhysicalDeviceProperties2(device, &properties);
        var bytes = new ReadOnlySpan<byte>(id.DeviceUuid, 16);
        bool present = false; foreach (byte value in bytes) present |= value != 0;
        if (!present) throw new InvalidOperationException("The Vulkan device supplied no usable native UUID.");
        return "uuid:" + Convert.ToHexStringLower(bytes);
    }
    static GpuEffectiveDiagnosticConfiguration EffectiveConfiguration(Vk vk, PhysicalDevice device,
        VulkanPhysicalDeviceRead read, VulkanHeadlessDiagnosticSettings settings, VulkanPipelineDiagnostics diagnostics,
        VulkanInstanceValidation validation, string? pipelineCache, string? spirvCache)
        => new(GpuBackendKind.VulkanNative, AdapterKey(vk, device),
            "VkPhysicalDeviceProperties.driverVersion=" + read.PipelineCacheIdentity.DriverVersion.ToString(CultureInfo.InvariantCulture),
            settings.Diagnostics, diagnostics.CaptureFlags != 0, false, false, !validation.EnabledLayers.IsEmpty,
            diagnostics.StatisticsSupported, diagnostics.CaptureFlags != 0, (uint)diagnostics.CaptureFlags,
            [new("frontend", SpirvFrontEndPin.Identity), new("toolchain", SpirvToolchainVersion.Identity)],
            [new("cache.pipeline", pipelineCache ?? "disabled"), new("cache.spirv", spirvCache ?? "disabled"),
             new("cache.spirv.memo", settings.CompileCache.ToString()), new("drain", "timeline"),
             new("frames-in-flight", settings.FramesInFlight.ToString(CultureInfo.InvariantCulture)), new("recording", "vulkan-native"),
             new("validation.actual", validation.DiagnosticConfiguration)]);
}
