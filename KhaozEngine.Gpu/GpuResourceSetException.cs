using System;

namespace KhaozEngine.Gpu;

/// <summary>A structured GpuResourceSet refusal. Existing ArgumentException catches remain valid.</summary>
public sealed class GpuResourceSetException : ArgumentException
{
    /// <summary>Construct a refusal with its required reason and original diagnostic data.</summary>
    public GpuResourceSetException(GpuResourceSetRefusal reason, string message, string? paramName)
        : base(message, paramName) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public GpuResourceSetRefusal Reason { get; }

    /// <summary>The element name reported by the refusing validator, when relevant to the reason.</summary>
    public string? ElementName { get; init; }

    /// <summary>The binding reported by the refusing validator, when relevant to the reason.</summary>
    public int? Binding { get; init; }

    /// <summary>The range offset reported by the refusing validator, when relevant to the reason.</summary>
    public uint? RangeOffset { get; init; }

    /// <summary>The range bytes reported by the refusing validator, when relevant to the reason.</summary>
    public uint? RangeBytes { get; init; }

    /// <summary>The buffer bytes reported by the refusing validator, when relevant to the reason.</summary>
    public uint? BufferBytes { get; init; }

    /// <summary>The resource kind reported by the refusing validator, when relevant to the reason.</summary>
    public GpuResourceKind? ResourceKind { get; init; }

    /// <summary>The native handle kind reported by the refusing validator, when relevant to the reason.</summary>
    public string? NativeHandleKind { get; init; }

    /// <summary>The declared count reported by the refusing validator, when relevant to the reason.</summary>
    public int? DeclaredCount { get; init; }

    /// <summary>The supplied count reported by the refusing validator, when relevant to the reason.</summary>
    public int? SuppliedCount { get; init; }

    /// <summary>The actual type reported by the refusing validator, when relevant to the reason.</summary>
    public Type? ActualType { get; init; }

    /// <summary>The expected type reported by the refusing validator, when relevant to the reason.</summary>
    public Type? ExpectedType { get; init; }

    /// <summary>The actual usage reported by the refusing validator, when relevant to the reason.</summary>
    public GpuTextureUsage? ActualUsage { get; init; }

    /// <summary>The required usage reported by the refusing validator, when relevant to the reason.</summary>
    public GpuTextureUsage? RequiredUsage { get; init; }
}
