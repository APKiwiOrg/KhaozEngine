using System;

namespace KhaozEngine.Gpu;

/// <summary>A buffer usage refusal. Existing ArgumentException catches remain valid.</summary>
public sealed class GpuBufferUsageArgumentException : ArgumentException
{
    /// <summary>Creates a refusal with its required reason and original parameter data.</summary>
    public GpuBufferUsageArgumentException(GpuBufferUsageRefusal reason, string message, string? paramName)
        : base(message, paramName) => Reason = reason;

    /// <summary>The rule that refused buffer creation.</summary>
    public GpuBufferUsageRefusal Reason { get; }

    /// <summary>The caller's declared buffer usage.</summary>
    public GpuBufferUsage? Usage { get; init; }

    /// <summary>The declared usages that cannot share the uniform buffer's frame base.</summary>
    public GpuBufferUsage? ConflictingUsage { get; init; }
}
