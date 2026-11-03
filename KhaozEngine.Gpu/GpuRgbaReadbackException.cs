using System;

namespace KhaozEngine.Gpu;

/// <summary>A structured GpuRgbaReadback refusal. Existing ArgumentException catches remain valid.</summary>
public sealed class GpuRgbaReadbackException : ArgumentException
{
    /// <summary>Construct a refusal with its required reason and original BCL diagnostic data.</summary>
    public GpuRgbaReadbackException(GpuRgbaReadbackRefusal reason, string message, string? paramName)
        : base(message, paramName) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public GpuRgbaReadbackRefusal Reason { get; }

    /// <summary>The requested width, including a negative request.</summary>
    public int RequestedWidth { get; init; }

    /// <summary>The requested height, including a negative request.</summary>
    public int RequestedHeight { get; init; }

    /// <summary>The source width.</summary>
    public uint SourceWidth { get; init; }

    /// <summary>The source height.</summary>
    public uint SourceHeight { get; init; }

    /// <summary>The source pixel format.</summary>
    public GpuPixelFormat SourceFormat { get; init; }

    /// <summary>The source mip count.</summary>
    public uint SourceMipLevels { get; init; }

    /// <summary>The source sample count.</summary>
    public uint SourceSampleCount { get; init; }

    /// <summary>The per-level readback operation for a mip chain.</summary>
    public string? MipReadbackOperation { get; init; }

    /// <summary>The resolve operation for a multisampled source.</summary>
    public string? ResolveOperation { get; init; }
}
