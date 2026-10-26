using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Internal;

/// <summary>Shared lit-receiver preflight, before a scene or standalone renderer allocates GPU resources.</summary>
internal static class LitReceiverRequirements
{
    internal static IGpuDevice RequireSupportedDevice(IGpuDevice device)
    {
        // Reserve the count lit pipelines will need when receiver shading first reads and binds water volumes.
        // The current layouts still have only the point-light record and cluster buffers.
        const int required = 3;
        int available = device.Capabilities.MaxFragmentStorageBuffers;
        if (available < required)
            throw new NotSupportedException($"Lit receivers require maxPerStageDescriptorStorageBuffers >= {required}, "
                + $"but the device reports {available}.");
        return device;
    }
}
