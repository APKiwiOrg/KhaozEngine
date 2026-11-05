using System;

namespace KhaozEngine.Render3D;

/// <summary>A structured refusal of TileGroundUpload input.</summary>
public sealed class TileGroundUploadArgumentException : ArgumentException
{
    /// <summary>Create a refusal with a stable reason and the original parameter context.</summary>
    public TileGroundUploadArgumentException(TileGroundUploadRefusal reason, string message, string? paramName)
        : base(message, paramName)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal with a stable reason and the original parameter context.</summary>
    public TileGroundUploadArgumentException(TileGroundUploadRefusal reason, string message, string? paramName, Exception inner)
        : base(message, paramName, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input was refused.</summary>
    public TileGroundUploadRefusal Reason { get; }

    /// <summary>The number of supplied layers. Null when unused by the reason.</summary>
    public int? LayerCount { get; init; }

    /// <summary>The minimum supported layer count. Null when unused by the reason.</summary>
    public int? MinimumLayerCount { get; init; }

    /// <summary>The maximum supported layer count. Null when unused by the reason.</summary>
    public int? MaximumLayerCount { get; init; }

    /// <summary>The zero-based index of the refused layer. Null when unused by the reason.</summary>
    public int? LayerIndex { get; init; }

    /// <summary>The supplied RGBA byte length. Null when unused by the reason.</summary>
    public int? ActualByteLength { get; init; }

    /// <summary>The required RGBA byte length. Null when unused by the reason.</summary>
    public int? ExpectedByteLength { get; init; }

    /// <summary>The declared layer width in texels. Null when unused by the reason.</summary>
    public int? Width { get; init; }

    /// <summary>The declared layer height in texels. Null when unused by the reason.</summary>
    public int? Height { get; init; }
}
