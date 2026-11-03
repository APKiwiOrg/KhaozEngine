using System;

namespace KhaozEngine.Gpu.Metal;

/// <summary>Why CPU access to a native Metal frame's storage was refused.</summary>
public enum MetalFrameAccessFailure
{
    /// <summary>The write overlaps bytes an early-committed frame can read.</summary>
    OverlappingWrite,
    /// <summary>A writable mapping would expose bytes an early-committed frame can read.</summary>
    WritableMapping,
    /// <summary>The prefix would read storage whose writable mapping is still open.</summary>
    MappedPrefix,
    /// <summary>An early-committed frame never submitted its final buffer, so its slot is quarantined.</summary>
    AbandonedFrameSlot,
}

/// <summary>A CPU access that would race an early-committed Metal frame. Resource and byte range identify the
/// refused access. FrameId identifies the protecting frame. RingSegment identifies a quarantined slot.</summary>
public sealed class MetalFrameAccessException : InvalidOperationException
{
    /// <summary>The refusal's structured reason.</summary>
    public MetalFrameAccessFailure Reason { get; }
    /// <summary>Process-local identity of the CPU-visible allocation, or zero for a slot refusal.</summary>
    public ulong ResourceId { get; }
    /// <summary>The first byte of the refused access within the allocation.</summary>
    public ulong OffsetBytes { get; }
    /// <summary>The refused access's length.</summary>
    public ulong LengthBytes { get; }
    /// <summary>The early-committed frame that owns the range or slot.</summary>
    public ulong FrameId { get; }
    /// <summary>The quarantined slot, or -1 for a resource access refusal.</summary>
    public int RingSegment { get; }

    internal MetalFrameAccessException(MetalFrameAccessFailure reason, ulong resourceId, ulong offset,
        ulong length, ulong frameId, int segment = -1)
        : base($"Metal frame {frameId} protects resource {resourceId}, bytes [{offset}, {offset + length}). "
            + $"CPU access was refused ({reason}). Use disjoint frame slots and close writable mappings before drawable binding. "
            + "A committed prefix stays immutable until its final buffer completes. An abandoned frame quarantines its slot.")
    {
        Reason = reason;
        ResourceId = resourceId;
        OffsetBytes = offset;
        LengthBytes = length;
        FrameId = frameId;
        RingSegment = segment;
    }
}
