using System;

namespace KhaozEngine.Gpu;

/// <summary>A structured GpuDynamicBinding refusal.</summary>
public sealed class GpuDynamicBindingException : ArgumentException
{
    /// <summary>Creates a refusal with its required reason and existing diagnostic data.</summary>
    public GpuDynamicBindingException(GpuDynamicBindingRefusal reason, string message, string? paramName) : base(message, paramName)
    {
        Reason = reason;
    }

    /// <summary>The rule that refused the operation.</summary>
    public GpuDynamicBindingRefusal Reason { get; }

    /// <summary>Backend associated with the refused operation, when applicable.</summary>
    public GpuBackendKind? Backend { get; init; }

    /// <summary>Set associated with the refused operation, when applicable.</summary>
    public uint? Set { get; init; }

    /// <summary>DynamicOffset associated with the refused operation, when applicable.</summary>
    public uint? DynamicOffset { get; init; }
}
