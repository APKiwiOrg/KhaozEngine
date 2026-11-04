using System;

namespace KhaozEngine.Gpu;

/// <summary>A structured GpuFramebuffer refusal. Existing ArgumentException catches remain valid.</summary>
public sealed class GpuFramebufferException : ArgumentException
{
    /// <summary>Construct a refusal with its required reason and original diagnostic data.</summary>
    public GpuFramebufferException(GpuFramebufferRefusal reason, string message, string? paramName)
        : base(message, paramName) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public GpuFramebufferRefusal Reason { get; }

    /// <summary>The attachment reported by the refusing validator, when relevant to the reason.</summary>
    public string? Attachment { get; init; }

    /// <summary>The actual usage reported by the refusing validator, when relevant to the reason.</summary>
    public GpuTextureUsage? ActualUsage { get; init; }

    /// <summary>The required usage reported by the refusing validator, when relevant to the reason.</summary>
    public GpuTextureUsage? RequiredUsage { get; init; }

    /// <summary>The is staging reported by the refusing validator, when relevant to the reason.</summary>
    public bool? IsStaging { get; init; }

    /// <summary>The native handle kind reported by the refusing validator, when relevant to the reason.</summary>
    public string? NativeHandleKind { get; init; }

    /// <summary>The width reported by the refusing validator, when relevant to the reason.</summary>
    public uint? Width { get; init; }

    /// <summary>The height reported by the refusing validator, when relevant to the reason.</summary>
    public uint? Height { get; init; }

    /// <summary>The sample count reported by the refusing validator, when relevant to the reason.</summary>
    public uint? SampleCount { get; init; }

    /// <summary>The expected width reported by the refusing validator, when relevant to the reason.</summary>
    public uint? ExpectedWidth { get; init; }

    /// <summary>The expected height reported by the refusing validator, when relevant to the reason.</summary>
    public uint? ExpectedHeight { get; init; }

    /// <summary>The expected sample count reported by the refusing validator, when relevant to the reason.</summary>
    public uint? ExpectedSampleCount { get; init; }

    /// <summary>The colour attachment count reported by the refusing validator, when relevant to the reason.</summary>
    public int? ColourAttachmentCount { get; init; }

    /// <summary>The has depth reported by the refusing validator, when relevant to the reason.</summary>
    public bool? HasDepth { get; init; }
}
