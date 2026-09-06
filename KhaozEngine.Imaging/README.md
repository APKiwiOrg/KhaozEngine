# KhaozEngine.Imaging

Dependency-free image IO for KhaozEngine. Two jobs, both BCL-only so no third-party image library
ever enters your dependency graph: a minimal PNG encoder for 8-bit RGBA buffers, and a
tolerance-based golden-grid regression primitive. This is the canonical home of the encoder the
headless snapshot harness (`KhaozEngine.Snapshot`) and Render2D's clipboard-copy path use, plus the
grid-compare core the golden-image tests and the snapshot-diff tool share.

## PngWriter

- `PngWriter.Encode(rgba, width, height)` - encodes a top-to-bottom row-major RGBA8 buffer
  (length must be `width * height * 4`) to a PNG byte array. Filter type 0 on every scanline.
- `PngWriter.Save(path, rgba, width, height)` - `Encode` plus `File.WriteAllBytes`.

```csharp
byte[] rgba = Render2DSnapshot.Capture(320, 180, Color.Black, ctx => DrawScene(ctx));
PngWriter.Save("/tmp/scene.png", rgba, 320, 180);
```

## PngReader

`PngReader.Decode(png)` decodes noninterlaced 8-bit and 16-bit greyscale, greyscale plus alpha, RGB and
RGBA PNGs. It handles all five PNG row filters and validates the signature, chunk order, CRCs, dimensions and
the exact decompressed payload size. Palette and interlaced images are rejected. Decoded allocation is capped at
`PngReader.MaxDecodedBytes`.

The returned `PngImage.Bytes` is top-to-bottom in the PNG's channel order. Each 16-bit channel sample remains
two bytes in most-significant-byte-first order, so no precision is discarded. A greyscale or RGB `tRNS`
chunk promotes the decoded output to GA or RGBA. Matching samples receive zero alpha and all other samples
receive full alpha, with the full 16-bit value compared when applicable.

This stays a focused PNG utility rather than a general image-processing library. Encoding is RGBA8 only.
Decoding deliberately excludes palettes, interlace, color conversion, transforms and other metadata interpretation.

## GoldenGrid

The reusable core of tolerance-based image regression: downsample a raw RGBA8 capture to a small grid
of average RGB per cell, compare two grids per channel, and serialize/deserialize a grid to the
committed golden text format. It knows nothing about files, GPU backends, or test frameworks, so games
can golden-test their own scenes and the `SnapshotTool` can diff images without pulling in xUnit. The
defaults (`DefaultGridW` 32, `DefaultGridH` 18, `DefaultTolerance` 0.06) match the committed engine
goldens, and `Serialize` output is byte-identical to those committed files.

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
