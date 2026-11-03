using System;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Metal.Internal;

/// <summary>CPU pointer handoff and explicit drains, with native queue operations supplied by the device.</summary>
internal sealed class MetalCpuAccess(MetalCpuReadProtection protection, Func<bool> flushSetup,
    Action drainTimeline, Action drainQueue, IDeviceLiveness liveness)
{
    internal void WaitForIdle()
    {
        if (liveness.IsDead) return;
        bool committed = flushSetup();
        drainTimeline();
        if (liveness.IsDead) return;
        protection.DrainCommittedPrefixes(committed, drainQueue);
    }

    internal MappedData Map(MetalBuffer buffer, GpuMapMode mode)
    {
        ulong physicalBase;
        lock (protection.Gate)
        {
            physicalBase = buffer.Ring?.CurrentSegmentBaseBytes ?? 0;
            protection.Map(buffer, buffer.CpuResource, physicalBase, buffer.SizeInBytes, mode != GpuMapMode.Read);
        }
        // Setup flushing and readback may block. Segment rotation must remain possible during those waits.
        try { DrainForRead(mode); return buffer.Mapped(physicalBase); }
        catch { protection.Unmap(buffer); throw; }
    }

    internal MappedData Map(MetalTexture texture, GpuMapMode mode)
    {
        var layout = texture.SubresourceLayout(0, 0);
        protection.Map(texture, texture.CpuResource, layout.Offset, layout.Size, mode != GpuMapMode.Read);
        try { DrainForRead(mode); return texture.Mapped(subresource: 0); }
        catch { protection.Unmap(texture); throw; }
    }

    void DrainForRead(GpuMapMode mode)
    {
        if (mode == GpuMapMode.Write) flushSetup();
        else WaitForIdle();
    }
}
