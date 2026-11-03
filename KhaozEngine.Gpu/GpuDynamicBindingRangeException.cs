using System;

namespace KhaozEngine.Gpu;

/// <summary>A dynamic binding range refusal. Existing ArgumentOutOfRangeException catches remain valid.</summary>
public sealed class GpuDynamicBindingRangeException : ArgumentOutOfRangeException
{
    /// <summary>Creates a refusal with its required reason and original parameter data.</summary>
    public GpuDynamicBindingRangeException(GpuDynamicBindingRefusal reason, string? paramName,
        object? actualValue, string message) : base(paramName, actualValue, message) => Reason = reason;

    /// <summary>The rule that refused the binding.</summary>
    public GpuDynamicBindingRefusal Reason { get; }

    /// <summary>The resource-set number, when the validator has it.</summary>
    public uint? Set { get; init; }

    /// <summary>The dynamic uniform binding number, when the validator has it.</summary>
    public uint? Binding { get; init; }

    /// <summary>The composed native binding offset in bytes, when available.</summary>
    public ulong? ComposedOffset { get; init; }

    /// <summary>The required native binding alignment in bytes, when available.</summary>
    public ulong? AlignmentBytes { get; init; }

    /// <summary>The ring segment base included in the composed offset, when available.</summary>
    public ulong? RingBase { get; init; }

    /// <summary>The resource-set window's byte offset.</summary>
    public ulong? RangeOffset { get; init; }

    /// <summary>The caller's per-draw byte offset applied to the binding.</summary>
    public ulong? DynamicOffset { get; init; }

    /// <summary>The bound window's length in bytes.</summary>
    public ulong? RangeBytes { get; init; }

    /// <summary>The length in bytes of one frame-ring segment, when the binding is ring-backed.</summary>
    public ulong? SegmentStride { get; init; }

    /// <summary>The binding operation that leaves room for a per-draw offset, when relevant.</summary>
    public string? RecoveryOperation { get; init; }
}
