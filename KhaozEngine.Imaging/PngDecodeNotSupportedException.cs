using System;

namespace KhaozEngine.Imaging;

/// <summary>A structured refusal of an unsupported PNG feature.</summary>
public sealed class PngDecodeNotSupportedException : NotSupportedException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public PngDecodeNotSupportedException(PngDecodeRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal preserving the original error.</summary>
    public PngDecodeNotSupportedException(PngDecodeRefusal reason, string message, Exception inner) : base(message, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the PNG input was refused.</summary>
    public PngDecodeRefusal Reason { get; }

    /// <summary>The four-character type of the refused chunk. Null when this identifier does not apply.</summary>
    public string? ChunkType { get; init; }

    /// <summary>The PNG color type involved in the refusal. Null when this identifier does not apply.</summary>
    public int? ColorType { get; init; }

    /// <summary>The PNG sample or index bit depth involved in the refusal. Null when this identifier does not apply.</summary>
    public int? BitDepth { get; init; }

    /// <summary>The compression method stored in IHDR. Null when this identifier does not apply.</summary>
    public int? CompressionMethod { get; init; }

    /// <summary>The filter method stored in IHDR. Null when this identifier does not apply.</summary>
    public int? FilterMethod { get; init; }

    /// <summary>The interlace method stored in IHDR. Null when this identifier does not apply.</summary>
    public int? InterlaceMethod { get; init; }
}
