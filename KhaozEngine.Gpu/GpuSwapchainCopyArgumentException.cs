using System;

namespace KhaozEngine.Gpu;

/// <summary>A structured GpuSwapchainCopy refusal. Existing ArgumentException catches remain valid.</summary>
public sealed class GpuSwapchainCopyArgumentException : ArgumentException
{
    /// <summary>Construct a refusal with its required reason and original diagnostic data.</summary>
    public GpuSwapchainCopyArgumentException(GpuSwapchainCopyRefusal reason, string message, string? paramName)
        : base(message, paramName) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public GpuSwapchainCopyRefusal Reason { get; }

    /// <summary>The colour attachment count reported by the refusing validator, when relevant to the reason.</summary>
    public int? ColourAttachmentCount { get; init; }

    /// <summary>The source width reported by the refusing validator, when relevant to the reason.</summary>
    public uint? SourceWidth { get; init; }

    /// <summary>The source height reported by the refusing validator, when relevant to the reason.</summary>
    public uint? SourceHeight { get; init; }

    /// <summary>The source format reported by the refusing validator, when relevant to the reason.</summary>
    public GpuPixelFormat? SourceFormat { get; init; }

    /// <summary>The destination width reported by the refusing validator, when relevant to the reason.</summary>
    public uint? DestinationWidth { get; init; }

    /// <summary>The destination height reported by the refusing validator, when relevant to the reason.</summary>
    public uint? DestinationHeight { get; init; }

    /// <summary>The destination format reported by the refusing validator, when relevant to the reason.</summary>
    public GpuPixelFormat? DestinationFormat { get; init; }

    /// <summary>The destination mip levels reported by the refusing validator, when relevant to the reason.</summary>
    public uint? DestinationMipLevels { get; init; }

    /// <summary>The destination sample count reported by the refusing validator, when relevant to the reason.</summary>
    public uint? DestinationSampleCount { get; init; }
}
