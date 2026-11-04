using System;

namespace KhaozEngine.Gpu;

/// <summary>A structured GpuTextureTransfer refusal. Existing ArgumentException catches remain valid.</summary>
public sealed class GpuTextureTransferArgumentException : ArgumentException
{
    /// <summary>Construct a refusal with its required reason and original diagnostic data.</summary>
    public GpuTextureTransferArgumentException(GpuTextureTransferRefusal reason, string message, string? paramName)
        : base(message, paramName) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public GpuTextureTransferRefusal Reason { get; }

    /// <summary>The mip levels reported by the refusing validator, when relevant to the reason.</summary>
    public uint? MipLevels { get; init; }

    /// <summary>The is staging reported by the refusing validator, when relevant to the reason.</summary>
    public bool? IsStaging { get; init; }

    /// <summary>The native handle missing reported by the refusing validator, when relevant to the reason.</summary>
    public bool? NativeHandleMissing { get; init; }

    /// <summary>The source sample count reported by the refusing validator, when relevant to the reason.</summary>
    public uint? SourceSampleCount { get; init; }

    /// <summary>The destination sample count reported by the refusing validator, when relevant to the reason.</summary>
    public uint? DestinationSampleCount { get; init; }

    /// <summary>The source width reported by the refusing validator, when relevant to the reason.</summary>
    public uint? SourceWidth { get; init; }

    /// <summary>The source height reported by the refusing validator, when relevant to the reason.</summary>
    public uint? SourceHeight { get; init; }

    /// <summary>The destination width reported by the refusing validator, when relevant to the reason.</summary>
    public uint? DestinationWidth { get; init; }

    /// <summary>The destination height reported by the refusing validator, when relevant to the reason.</summary>
    public uint? DestinationHeight { get; init; }

    /// <summary>The source format reported by the refusing validator, when relevant to the reason.</summary>
    public GpuPixelFormat? SourceFormat { get; init; }

    /// <summary>The destination format reported by the refusing validator, when relevant to the reason.</summary>
    public GpuPixelFormat? DestinationFormat { get; init; }

    /// <summary>The source is staging reported by the refusing validator, when relevant to the reason.</summary>
    public bool? SourceIsStaging { get; init; }

    /// <summary>The destination is staging reported by the refusing validator, when relevant to the reason.</summary>
    public bool? DestinationIsStaging { get; init; }

    /// <summary>The source mip levels reported by the refusing validator, when relevant to the reason.</summary>
    public uint? SourceMipLevels { get; init; }

    /// <summary>The source array layers reported by the refusing validator, when relevant to the reason.</summary>
    public uint? SourceArrayLayers { get; init; }

    /// <summary>The destination mip levels reported by the refusing validator, when relevant to the reason.</summary>
    public uint? DestinationMipLevels { get; init; }

    /// <summary>The destination array layers reported by the refusing validator, when relevant to the reason.</summary>
    public uint? DestinationArrayLayers { get; init; }
}
