using System;

namespace KhaozEngine.Gpu;

/// <summary>A structured refusal of a command requiring a bound framebuffer or its depth attachment.</summary>
public sealed class GpuBoundFramebufferException : InvalidOperationException
{
    /// <summary>Creates a refusal with its required reason and original diagnostic message.</summary>
    public GpuBoundFramebufferException(GpuBoundFramebufferRefusal reason, string message) : base(message)
        => Reason = reason;

    /// <summary>The framebuffer rule that refused the command.</summary>
    public GpuBoundFramebufferRefusal Reason { get; }

    /// <summary>The name of the refused <see cref="IGpuCommandList"/> member.</summary>
    public string? Command { get; init; }
}
