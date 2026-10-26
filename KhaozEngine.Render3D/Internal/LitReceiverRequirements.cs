using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Internal;

/// <summary>Shared lit-receiver preflight, before a scene or standalone renderer allocates GPU resources.</summary>
internal static class LitReceiverRequirements
{
    internal static IGpuDevice RequireSupportedDevice(IGpuDevice device)
    {
        // Point-light records, point-light clusters and water volumes are all read by lit fragments.
        const int required = 3;
        int available = device.Capabilities.MaxFragmentStorageBuffers;
        if (available < required)
            throw new NotSupportedException($"Lit receivers require maxPerStageDescriptorStorageBuffers >= {required}, "
                + $"but the device reports {available}.");
        return device;
    }
}
