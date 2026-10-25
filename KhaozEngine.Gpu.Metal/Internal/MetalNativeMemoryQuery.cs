using System;

namespace KhaozEngine.Gpu.Metal.Internal;

internal interface IMetalDeviceMemoryNative
{
    bool HasAllocationQuery { get; }
    bool HasUnifiedQuery { get; }
    nuint CurrentAllocatedSize { get; }
    bool HasUnifiedMemory { get; }
}

/// <summary>Native snapshot interpretation, separate from selector dispatch and safe to exercise headlessly.</summary>
internal static class MetalNativeMemoryQuery
{
    internal static MetalNativeMemorySnapshot Read(IMetalDeviceMemoryNative native)
    {
        bool? unified = null;
        string provenance;
        try
        {
            if (native.HasUnifiedQuery) unified = native.HasUnifiedMemory;
            provenance = unified == true ? "unified CPU/GPU memory" : unified == false
                ? "non-unified device memory" : "unified-memory provenance unavailable (hasUnifiedMemory selector absent)";
        }
        catch (Exception e) { provenance = "unified-memory provenance query failed: " + e.Message; }
        string source = "MTLDevice.currentAllocatedSize, all resources allocated by this device including internal/driver resources, "
            + provenance + ", not residency or dedicated VRAM capacity, not additive with logical payload";
        GpuDiagnosticMetric allocation;
        try
        {
            allocation = native.HasAllocationQuery
                ? new(new(GpuDiagnosticValueType.UInt64, UInt64: native.CurrentAllocatedSize), "bytes", source,
                    GpuDiagnosticStatus.Available, null, GpuDiagnosticProvenance.BackendAllocation)
                : new(null, "bytes", source, GpuDiagnosticStatus.Unsupported,
                    "The device does not respond to currentAllocatedSize (macOS 10.13+).", GpuDiagnosticProvenance.Unknown);
        }
        catch (Exception e)
        {
            allocation = new(null, "bytes", source, e is NotSupportedException ? GpuDiagnosticStatus.Unsupported
                : GpuDiagnosticStatus.Invalid, e.Message, GpuDiagnosticProvenance.BackendAllocation);
        }
        return new(allocation, unified);
    }
}
