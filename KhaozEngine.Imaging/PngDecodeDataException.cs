using System;

namespace KhaozEngine.Imaging;

/// <summary>A structured refusal of malformed PNG input.</summary>
public sealed class PngDecodeDataException : MalformedDataException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public PngDecodeDataException(PngDecodeRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal preserving the original error.</summary>
    public PngDecodeDataException(PngDecodeRefusal reason, string message, Exception inner) : base(message, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the PNG input was refused.</summary>
    public PngDecodeRefusal Reason { get; }

    /// <summary>The input bytes available from Offset, including the chunk envelope, or the total for an invalid signature. Null when this identifier does not apply.</summary>
    public int? AvailableBytes { get; init; }

    /// <summary>The byte offset of the refused chunk in the PNG input. Null when this identifier does not apply.</summary>
    public int? Offset { get; init; }

    /// <summary>The unsigned payload byte length declared by the refused chunk. Null when this identifier does not apply.</summary>
    public uint? DeclaredLength { get; init; }

    /// <summary>The four-character type of the refused chunk. Null when this identifier does not apply.</summary>
    public string? ChunkType { get; init; }

    /// <summary>The CRC stored in the refused chunk. Null when this identifier does not apply.</summary>
    public uint? ExpectedCrc { get; init; }

    /// <summary>The CRC computed from the refused chunk type and payload. Null when this identifier does not apply.</summary>
    public uint? ActualCrc { get; init; }

    /// <summary>The byte count that would exceed the allocation cap. Null when this identifier does not apply.</summary>
    public long? RequiredBytes { get; init; }

    /// <summary>The allocation cap in bytes. Null when this identifier does not apply.</summary>
    public int? MaximumBytes { get; init; }

    /// <summary>The byte length of the refused IEND payload. Null when this identifier does not apply.</summary>
    public int? PayloadLength { get; init; }

    /// <summary>Whether IHDR was seen before the refusal. Null when this identifier does not apply.</summary>
    public bool? HeaderSeen { get; init; }

    /// <summary>Whether IDAT was seen before the refusal. Null when this identifier does not apply.</summary>
    public bool? DataSeen { get; init; }

    /// <summary>The byte count after IEND. Null when this identifier does not apply.</summary>
    public int? TrailingBytes { get; init; }

    /// <summary>The PNG color type involved in the refusal. Null when this identifier does not apply.</summary>
    public int? ColorType { get; init; }

    /// <summary>The refused byte length, or bytes read into the expected-size buffer for DecodedLengthMismatch. Null when this identifier does not apply.</summary>
    public int? ActualLength { get; init; }

    /// <summary>The byte length required by the PNG header or chunk format. Null when this identifier does not apply.</summary>
    public int? ExpectedLength { get; init; }

    /// <summary>The width stored in IHDR. Null when this identifier does not apply.</summary>
    public uint? Width { get; init; }

    /// <summary>The height stored in IHDR. Null when this identifier does not apply.</summary>
    public uint? Height { get; init; }

    /// <summary>The decoded raster byte count calculated from IHDR. Null when this identifier does not apply.</summary>
    public long? RasterBytes { get; init; }

    /// <summary>The maximum palette-expanded byte count, or raster byte count for other color types. Null when this identifier does not apply.</summary>
    public long? ExpandedBytes { get; init; }

    /// <summary>The filtered byte count calculated from IHDR, including row filter bytes. Null when this identifier does not apply.</summary>
    public long? FilteredBytes { get; init; }

    /// <summary>Whether the decompressed stream has an extra byte after the expected-size buffer. Null when this identifier does not apply.</summary>
    public bool? HasTrailingData { get; init; }

    /// <summary>The refused PLTE payload byte length. Null when this identifier does not apply.</summary>
    public int? ByteLength { get; init; }

    /// <summary>The number of entries in the refused palette. Null when this identifier does not apply.</summary>
    public int? EntryCount { get; init; }

    /// <summary>The PNG sample or index bit depth involved in the refusal. Null when this identifier does not apply.</summary>
    public int? BitDepth { get; init; }

    /// <summary>The number of palette transparency entries. Null when this identifier does not apply.</summary>
    public int? AlphaCount { get; init; }

    /// <summary>The number of available palette entries. Null when this identifier does not apply.</summary>
    public int? PaletteCount { get; init; }

    /// <summary>The zero-based raster pixel with an invalid palette index. Null when this identifier does not apply.</summary>
    public int? PixelIndex { get; init; }

    /// <summary>The palette index stored for the refused pixel. Null when this identifier does not apply.</summary>
    public int? Index { get; init; }

    /// <summary>The zero-based color-key sample outside the image bit depth. Null when this identifier does not apply.</summary>
    public int? SampleIndex { get; init; }

    /// <summary>The stored color-key sample outside the image bit depth. Null when this identifier does not apply.</summary>
    public ushort? SampleValue { get; init; }

    /// <summary>The channel count after transparency expansion. Null when this identifier does not apply.</summary>
    public int? OutputChannels { get; init; }

    /// <summary>The unsupported row filter byte. Null when this identifier does not apply.</summary>
    public int? FilterType { get; init; }

    /// <summary>The zero-based row within the current noninterlaced or Adam7 pass. Null when this identifier does not apply.</summary>
    public int? RowIndex { get; init; }
}
