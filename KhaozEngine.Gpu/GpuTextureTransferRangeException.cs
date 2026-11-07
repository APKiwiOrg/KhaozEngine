using System;

namespace KhaozEngine.Gpu;

/// <summary>A structured GpuTextureTransfer refusal. Existing ArgumentOutOfRangeException catches remain valid.</summary>
public sealed class GpuTextureTransferRangeException : ArgumentOutOfRangeException
{
    /// <summary>Construct a refusal with its required reason and original diagnostic data.</summary>
    public GpuTextureTransferRangeException(GpuTextureTransferRefusal reason, string? paramName, object? actualValue, string message)
        : base(paramName, actualValue, message) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public GpuTextureTransferRefusal Reason { get; }

    /// <summary>The side reported by the refusing validator, when relevant to the reason.</summary>
    public string? Side { get; init; }

    /// <summary>The mip level reported by the refusing validator, when relevant to the reason.</summary>
    public uint? MipLevel { get; init; }

    /// <summary>The array layer reported by the refusing validator, when relevant to the reason.</summary>
    public uint? ArrayLayer { get; init; }

    /// <summary>The mip levels reported by the refusing validator, when relevant to the reason.</summary>
    public uint? MipLevels { get; init; }

    /// <summary>The array layers reported by the refusing validator, when relevant to the reason.</summary>
    public uint? ArrayLayers { get; init; }
}
