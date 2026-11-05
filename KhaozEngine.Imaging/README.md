# KhaozEngine.Imaging

Image IO for KhaozEngine with no third-party image library. The sole engine dependency is
`KhaozEngine.Primitives`, which supplies the shared `MalformedDataException` base. Image codecs use the BCL:
a minimal PNG encoder for 8-bit RGBA buffers, a baseline JPEG
encoder for the same buffers, and a tolerance-based golden-grid regression primitive. This is the
canonical home of the PNG encoder the headless snapshot harness (`KhaozEngine.Snapshot`) and
Render2D's clipboard-copy path use, plus the grid-compare core the golden-image tests and the
snapshot-diff tool share.

## PngWriter

- `PngWriter.Encode(rgba, width, height)` - encodes a top-to-bottom row-major RGBA8 buffer
  (length must be `width * height * 4`) to a PNG byte array. Filter type 0 on every scanline.
- `PngWriter.Save(path, rgba, width, height)` - `Encode` plus `File.WriteAllBytes`.

```csharp
byte[] rgba = Render2DSnapshot.Capture(320, 180, Color.Black, ctx => DrawScene(ctx));
PngWriter.Save("/tmp/scene.png", rgba, 320, 180);
```

## JpegWriter

`JpegWriter.Encode(rgba, width, height, quality)` encodes a top-to-bottom row-major RGBA8 buffer (length
must be `width * height * 4`) to a baseline JPEG byte array. Width and height are 1 to 65535 and quality
is 1 to 100. Alpha is ignored. The stream is JFIF 1.01, sequential (never progressive), with full-range
BT.601 YCbCr and 4:2:0 chroma subsampling. Quantization uses the ITU T.81 Annex K tables scaled by the
IJG quality rule, entropy coding uses the Annex K standard Huffman tables and the forward DCT is the float
AAN factorization. Edges that do not fill a 16x16 MCU repeat the last row and column. The output is a pure
function of the input, so the same pixels always encode to the same bytes.

```csharp
byte[] jpeg = JpegWriter.Encode(rgba, 960, 540, quality: 70);
```

One 960x540 frame at quality 70 encodes in a mean of 10.18 ms with a p95 of 31.04 ms (125,460 bytes) on
macOS, machine model `Mac14,6`, measured on a shared machine under heavy load, so the p95 is pessimistic.
Reproduce with `dotnet run --project KhaozEngine.Benchmarks -c Release -- --jpeg`. Windows and Linux
figures are not yet measured.

## JpegThumbnailDecoder

`JpegThumbnailDecoder.DecodeEighthScale(jpeg)` returns a `JpegThumbnail` with a row-major RGBA8 buffer
at `ceil(width / 8)` by `ceil(height / 8)`, with alpha 255. It reads sequential Huffman JPEG (SOF0 or
8-bit SOF1), one grayscale or three YCbCr components, sampling factors of one or two, and restart
markers. Only DC coefficients are dequantized, AC coefficients are consumed without an inverse DCT.
Chroma maps to the output block grid by sampling ratio and uses the JFIF conversion matrix. Malformed
or truncated input and edges outside 1 to 16384 throw `InvalidDataException`. Progressive, arithmetic,
12-bit and other unsupported coding processes throw `NotSupportedException`, as do component counts
other than one or three. Three components are always read as YCbCr, so an Adobe RGB JPEG decodes with
wrong colours. The decoder is meant for frames that `JpegWriter` produced.

## PngReader

`PngReader.Decode(png)` decodes every PNG colour type and bit depth, either noninterlaced or Adam7-interlaced:
1, 2, 4, 8 and 16-bit greyscale, 8 and 16-bit greyscale plus alpha, RGB and RGBA, and 1, 2, 4 and 8-bit palette.
It handles all five PNG row filters and validates the signature, chunk order, CRCs, dimensions, palette indices
and the exact decompressed payload size. Decoded allocation is capped at `PngReader.MaxDecodedBytes`.

The returned `PngImage.Bytes` is top-to-bottom in the PNG's channel order. Each 16-bit channel sample remains
two bytes in most-significant-byte-first order, so no precision is discarded. A greyscale or RGB `tRNS`
chunk promotes the decoded output to GA or RGBA. Matching samples receive zero alpha and all other samples
receive full alpha, with the full 16-bit value compared when applicable. Greyscale at 1, 2 or 4 bits scales to
8 bits as `value * 255 / (2^depth - 1)`, and its `tRNS` key matches the stored sample before scaling. A palette
image expands to 8-bit RGB,
or to RGBA when it carries a `tRNS` chunk. Palette entries past the end of `tRNS` are opaque. An interlaced
image is returned in ordinary raster order.

This stays a focused PNG utility rather than a general image-processing library. Encoding is RGBA8 only.
Decoding deliberately excludes color conversion, transforms and other metadata interpretation.

### Refusals

`PngDecodeDataException : MalformedDataException` carries malformed PNG refusals, including corrupt zlib
streams. This replaces the sealed `InvalidDataException` at PNG admission sites. Catch `MalformedDataException`
to handle malformed engine data across families. `PngDecodeNotSupportedException : NotSupportedException`
carries unsupported PNG features. Both require a non-nullable `PngDecodeRefusal Reason` in every constructor.
Messages are developer prose. Branch on `Reason` and read the nullable identifiers for that reason instead.

The decoder translates a native `ZLibStream` `InvalidDataException` to `CompressedStreamCorrupt`, preserving
its message and the original error in `InnerException`. A short decompressed payload or extra decompressed byte
still has reason `DecodedLengthMismatch`. Its `ActualLength` counts bytes read into the expected-size buffer,
while `HasTrailingData` records the extra-byte probe. `AvailableBytes` counts bytes from the refused chunk's
`Offset`, including its envelope, or the total input length for `SignatureInvalid`.

| `PngDecodeRefusal` | Refuses | Identifiers |
|---|---|---|
| `SignatureInvalid` | Input without the PNG signature. | `AvailableBytes` |
| `ChunkHeaderTruncated` | Input shorter than a complete PNG chunk envelope. | `Offset`, `AvailableBytes` |
| `ChunkLengthUnsupported` | A PNG chunk longer than the supported signed length. | `Offset`, `DeclaredLength` |
| `ChunkDataTruncated` | A PNG chunk extending beyond the input. | `Offset`, `DeclaredLength`, `AvailableBytes` |
| `ChunkCrcMismatch` | A PNG chunk with a mismatched CRC. | `ChunkType`, `Offset`, `ExpectedCrc`, `ActualCrc` |
| `HeaderOutOfOrder` | A repeated IHDR or one not first. | `Offset` |
| `DataOutOfOrder` | IDAT before IHDR or after the data sequence ends. | `ChunkType`, `Offset` |
| `CompressedAllocationCapExceeded` | Compressed input above the allocation cap. | `RequiredBytes`, `MaximumBytes` |
| `EndChunkInvalid` | An IEND with missing preceding chunks or payload bytes. | `Offset`, `PayloadLength`, `HeaderSeen`, `DataSeen` |
| `PaletteOutOfOrder` | A misplaced repeated or post-transparency PLTE chunk. | `Offset` |
| `TransparencyOutOfOrder` | A misplaced or repeated tRNS chunk. | `Offset` |
| `PaletteTransparencyBeforePalette` | Indexed transparency before the palette. | `Offset` |
| `DataAfterEnd` | Trailing bytes after IEND. | `Offset`, `TrailingBytes` |
| `EndChunkMissing` | A PNG without IEND. | `HeaderSeen`, `DataSeen` |
| `PaletteMissing` | An indexed PNG without PLTE. | `ColorType` |
| `HeaderLengthMismatch` | An IHDR not containing thirteen bytes. | `ActualLength`, `ExpectedLength` |
| `DimensionsInvalid` | Zero or unsupported PNG dimensions. | `Width`, `Height` |
| `DecodedAllocationCapExceeded` | The decoded or filtered raster above the allocation cap. | `RasterBytes`, `ExpandedBytes`, `FilteredBytes`, `MaximumBytes` |
| `DecodedLengthMismatch` | Decompressed data of a different length than IHDR requires. | `ExpectedLength`, `ActualLength`, `HasTrailingData` |
| `CriticalChunkUnsupported` | An unimplemented critical PNG chunk. | `ChunkType` |
| `ColorTypeUnsupported` | An unsupported PNG colour type. | `ColorType` |
| `BitDepthUnsupported` | A bit depth unsupported for the PNG colour type. | `BitDepth`, `ColorType` |
| `CompressionOrFilterMethodUnsupported` | Unsupported PNG compression or filter methods. | `CompressionMethod`, `FilterMethod` |
| `InterlaceUnsupported` | An unsupported interlace method. | `InterlaceMethod` |
| `PaletteLengthInvalid` | An empty malformed or oversized PLTE table. | `ByteLength` |
| `PaletteExceedsIndexDepth` | A palette exceeding its index bit depth. | `EntryCount`, `BitDepth` |
| `PaletteTransparencyTooLong` | A transparency table longer than its palette. | `AlphaCount`, `PaletteCount` |
| `PaletteIndexOutsideRange` | A pixel index outside the PNG palette. | `PixelIndex`, `Index`, `PaletteCount` |
| `TransparencyWithAlphaColorType` | A tRNS chunk for a colour type already carrying alpha. | `ColorType` |
| `TransparencyLengthMismatch` | A colour key with a byte length incompatible with its colour type. | `ActualLength`, `ExpectedLength`, `ColorType` |
| `TransparencySampleOutsideDepth` | A colour-key sample outside its image bit depth. | `BitDepth`, `SampleIndex`, `SampleValue` |
| `TransparencyAllocationCapExceeded` | Transparency expansion above the decoded allocation cap. | `RequiredBytes`, `MaximumBytes`, `Width`, `Height`, `OutputChannels` |
| `RowFilterUnsupported` | An unimplemented PNG row filter. | `FilterType`, `RowIndex` |
| `CompressedStreamCorrupt` | A zlib decode failure inside IDAT. | `InnerException` |

## GoldenGrid

The reusable core of tolerance-based image regression: downsample a raw RGBA8 capture to a small grid
of average RGB per cell, compare two grids per channel, and serialize/deserialize a grid to the
committed golden text format. It knows nothing about files, GPU backends, or test frameworks, so games
can golden-test their own scenes and the `SnapshotTool` can diff images without pulling in xUnit. The
grid defaults (`DefaultGridW` 32, `DefaultGridH` 18) match the committed engine goldens, and `Serialize`
output is byte-identical to those committed files. `DefaultTolerance` 0.06 is the consumer default. The
engine's own goldens compare at a tighter 0.01 (see `docs/CROSS-PLATFORM.md`, "Tolerance and rebakes").

- `GoldenGrid.Downsample(rgba, w, h, gridW=32, gridH=18)` - average RGB per cell, `float[]` row-major,
  3 floats/cell (0..1), alpha ignored.
- `GoldenGrid.Compare(got, want, tolerance=0.06)` - returns a `GoldenGridComparison` carrying `Passed`,
  `WorstDiff`, and `Offenders` (each a `GoldenGridOffender` with cell, channel, got, want, diff), sorted
  worst-first, so callers format their own failure messages.
- `GoldenGrid.Serialize(grid, gridW=32, gridH=18)` / `GoldenGrid.Deserialize(text)` - the canonical
  `# KhaozEngine golden grid WxH ...` header plus one `r g b` line per cell at four decimals.
- `GoldenGrid.GridToImage(grid, w, h, ...)` and `GoldenGrid.DiffHeatMap(got, want, w, h, ...)` - paint a
  grid as flat nearest-neighbour blocks, or a per-cell heat map (black to red at 2x tolerance,
  over-tolerance cells bordered), for viewable evidence PNGs.

```csharp
float[] got  = GoldenGrid.Downsample(capture, 480, 320);
float[] want = GoldenGrid.Deserialize(File.ReadAllText("scene.metal.txt"));
var cmp = GoldenGrid.Compare(got, want);
if (!cmp.Passed)
    Console.WriteLine($"regressed: worst {cmp.WorstDiff:0.###}, {cmp.Offenders.Count} cell(s) over tol");
```
