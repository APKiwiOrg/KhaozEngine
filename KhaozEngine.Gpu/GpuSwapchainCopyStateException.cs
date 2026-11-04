using System;

namespace KhaozEngine.Gpu;

/// <summary>A structured GpuSwapchainCopy refusal. Existing InvalidOperationException catches remain valid.</summary>
public sealed class GpuSwapchainCopyStateException : InvalidOperationException
{
    /// <summary>Construct a refusal with its required reason and original diagnostic data.</summary>
    public GpuSwapchainCopyStateException(GpuSwapchainCopyRefusal reason, string message)
        : base(message) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public GpuSwapchainCopyRefusal Reason { get; }

    /// <summary>The command reported by the refusing validator, when relevant to the reason.</summary>
    public string? Command { get; init; }
}
