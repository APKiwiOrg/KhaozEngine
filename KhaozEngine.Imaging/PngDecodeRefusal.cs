namespace KhaozEngine.Imaging;

/// <summary>The stable reasons PNG input or an unsupported PNG feature is refused.</summary>
public enum PngDecodeRefusal
{
    /// <summary>Refuses input without the PNG signature.</summary>
    SignatureInvalid,

    /// <summary>Refuses input shorter than a complete PNG chunk envelope.</summary>
    ChunkHeaderTruncated,

    /// <summary>Refuses a PNG chunk longer than the supported signed length.</summary>
    ChunkLengthUnsupported,

    /// <summary>Refuses a PNG chunk extending beyond the input.</summary>
    ChunkDataTruncated,

    /// <summary>Refuses a PNG chunk with a mismatched CRC.</summary>
    ChunkCrcMismatch,

    /// <summary>Refuses a repeated IHDR or one not first.</summary>
    HeaderOutOfOrder,

    /// <summary>Refuses IDAT before IHDR or after the data sequence ends.</summary>
    DataOutOfOrder,

    /// <summary>Refuses compressed input above the allocation cap.</summary>
    CompressedAllocationCapExceeded,

    /// <summary>Refuses an IEND with missing preceding chunks or payload bytes.</summary>
    EndChunkInvalid,

    /// <summary>Refuses a misplaced repeated or post-transparency PLTE chunk.</summary>
    PaletteOutOfOrder,

    /// <summary>Refuses a misplaced or repeated tRNS chunk.</summary>
    TransparencyOutOfOrder,

    /// <summary>Refuses indexed transparency before the palette.</summary>
    PaletteTransparencyBeforePalette,

    /// <summary>Refuses trailing bytes after IEND.</summary>
    DataAfterEnd,

    /// <summary>Refuses a PNG without IEND.</summary>
    EndChunkMissing,

    /// <summary>Refuses an indexed PNG without PLTE.</summary>
    PaletteMissing,

    /// <summary>Refuses an IHDR not containing thirteen bytes.</summary>
    HeaderLengthMismatch,

    /// <summary>Refuses zero or unsupported PNG dimensions.</summary>
    DimensionsInvalid,

    /// <summary>Refuses the decoded or filtered raster above the allocation cap.</summary>
    DecodedAllocationCapExceeded,

    /// <summary>Refuses decompressed data of a different length than IHDR requires.</summary>
    DecodedLengthMismatch,

    /// <summary>Refuses an unimplemented critical PNG chunk.</summary>
    CriticalChunkUnsupported,

    /// <summary>Refuses an unsupported PNG colour type.</summary>
    ColorTypeUnsupported,

    /// <summary>Refuses a bit depth unsupported for the PNG colour type.</summary>
    BitDepthUnsupported,

    /// <summary>Refuses unsupported PNG compression or filter methods.</summary>
    CompressionOrFilterMethodUnsupported,

    /// <summary>Refuses an unsupported interlace method.</summary>
    InterlaceUnsupported,

    /// <summary>Refuses an empty malformed or oversized PLTE table.</summary>
    PaletteLengthInvalid,

    /// <summary>Refuses a palette exceeding its index bit depth.</summary>
    PaletteExceedsIndexDepth,

    /// <summary>Refuses a transparency table longer than its palette.</summary>
    PaletteTransparencyTooLong,

    /// <summary>Refuses a pixel index outside the PNG palette.</summary>
    PaletteIndexOutsideRange,

    /// <summary>Refuses tRNS for a colour type already carrying alpha.</summary>
    TransparencyWithAlphaColorType,

    /// <summary>Refuses a colour key with a byte length incompatible with its colour type.</summary>
    TransparencyLengthMismatch,

    /// <summary>Refuses a colour-key sample outside its image bit depth.</summary>
    TransparencySampleOutsideDepth,

    /// <summary>Refuses transparency expansion above the decoded allocation cap.</summary>
    TransparencyAllocationCapExceeded,

    /// <summary>Refuses an unimplemented PNG row filter.</summary>
    RowFilterUnsupported,

    /// <summary>Refuses an IDAT zlib stream whose compressed data or checksum is corrupt.</summary>
    CompressedStreamCorrupt,
}
