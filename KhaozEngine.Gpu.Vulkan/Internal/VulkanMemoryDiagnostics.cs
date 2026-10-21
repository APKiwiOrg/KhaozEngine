using System.Collections.Generic;

namespace KhaozEngine.Gpu.Vulkan.Internal;

internal readonly record struct VulkanMemoryPayload(ulong BufferBytes = 0, ulong TextureBytes = 0,
    ulong StagingCapacityBytes = 0);

/// <summary>Separate resource-payload and native-allocation domains, under the allocator's lock.</summary>
internal sealed class VulkanMemoryDiagnostics
{
    readonly Dictionary<(VulkanMemoryChunk? Chunk, ulong Offset), VulkanMemoryPayload?> _payloads = new();
    ulong _buffers, _textures, _staging, _peak, _committed;
    int _unknown;
    bool _unknownPeak;

    internal void Allocate(in VulkanMemoryAllocation allocation, VulkanMemoryPayload? payload)
    {
        _payloads.Add((allocation.Chunk, allocation.Offset), payload);
        if (payload is { } known)
        {
            checked
            {
                _buffers += known.BufferBytes;
                _textures += known.TextureBytes;
                _staging += known.StagingCapacityBytes;
                _peak = System.Math.Max(_peak, _buffers + _textures);
            }
        }
        else { _unknown++; _unknownPeak = true; }
    }

    internal void Free(in VulkanMemoryAllocation allocation)
    {
        if (!_payloads.Remove((allocation.Chunk, allocation.Offset), out VulkanMemoryPayload? payload)) return;
        if (payload is { } known)
        {
            _buffers -= known.BufferBytes;
            _textures -= known.TextureBytes;
            _staging -= known.StagingCapacityBytes;
        }
        else _unknown--;
    }

    internal void Commit(ulong bytes) => _committed = checked(_committed + bytes);
    internal void ReleaseChunk(ulong bytes) => _committed -= bytes;

    internal void ClearPayload()
    {
        _payloads.Clear();
        _buffers = _textures = _staging = 0;
        _unknown = 0;
    }

    internal GpuResourceMemorySnapshot Snapshot(bool invalid = false)
    {
        GpuDiagnosticMetric Logical(ulong value, string source, string unit = "bytes") =>
            invalid ? Missing("Device disposed or lost.", unit, GpuDiagnosticStatus.Invalid)
                : _unknown != 0 ? Missing("Some live allocation requests have no logical payload metadata.", unit)
                : Available(value, unit, source, GpuDiagnosticProvenance.LogicalPayload);
        return new(Logical(_buffers, "Vulkan requested buffer payload, retained through native resource retirement"),
            Logical(_textures, "Vulkan texel payload including mips, layers and samples, retained through native retirement"),
            Logical(_staging, "Vulkan staging and ring capacity, overlaps buffer and staging-texture payload, do not add"),
            Missing("History is not identified by the resource API."),
            Missing("Scratch is not identified by the resource API."),
            _unknownPeak ? Missing("Peak includes allocation requests without logical payload metadata.")
                : Logical(_peak, "Vulkan peak buffer plus texture payload, excludes backing heaps"),
            invalid ? Missing("Device disposed or lost.", "count", GpuDiagnosticStatus.Invalid)
                : Available((ulong)_payloads.Count, "count", "Vulkan live resource suballocations, including staging blocks",
                    GpuDiagnosticProvenance.LogicalPayload),
            invalid ? Missing("Device disposed or lost.", status: GpuDiagnosticStatus.Invalid)
                : Available(_committed, "bytes", "vkAllocateMemory allocationSize, includes retained and retiring chunks, excludes driver-owned memory",
                    GpuDiagnosticProvenance.BackendAllocation),
            Missing("No native residency query is enabled. Committed allocation is not residency."),
            Missing("VK_EXT_memory_budget is not enabled. Heap size is not a memory budget."), null);
    }

    static GpuDiagnosticMetric Available(ulong value, string unit, string source, GpuDiagnosticProvenance provenance)
        => new(new(GpuDiagnosticValueType.UInt64, UInt64: value), unit, source, GpuDiagnosticStatus.Available, null, provenance);

    static GpuDiagnosticMetric Missing(string reason, string unit = "bytes", GpuDiagnosticStatus status = GpuDiagnosticStatus.Unsupported)
        => new(null, unit, "Vulkan memory diagnostics", status, reason, GpuDiagnosticProvenance.Unknown);
}

internal sealed partial class VulkanMemoryAllocator : IGpuResourceMemoryDiagnostics
{
    readonly VulkanMemoryDiagnostics _memoryDiagnostics = new();

    public GpuResourceMemorySnapshot ReadMemorySnapshot()
    {
        lock (_gate) return _memoryDiagnostics.Snapshot();
    }

    internal GpuResourceMemorySnapshot ReadMemorySnapshot(bool invalid)
    {
        lock (_gate) return _memoryDiagnostics.Snapshot(invalid);
    }
}

internal sealed unsafe partial class VulkanGpuDevice : IGpuResourceMemoryDiagnostics
{
    public GpuResourceMemorySnapshot ReadMemorySnapshot()
        => _memory.ReadMemorySnapshot(_disposed || _liveness.IsDead);
}
