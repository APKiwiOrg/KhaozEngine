# KhaozEngine.Imaging

Dependency-free image IO for KhaozEngine. One type, one job: a minimal PNG encoder for 8-bit RGBA
buffers, built on nothing but the BCL (`ZLibStream` for the IDAT stream plus a CRC-32 table), so no
third-party image library ever enters your dependency graph. This is the canonical home of the
encoder the headless snapshot harness (`KhaozEngine.Snapshot`) and Render2D's clipboard-copy path use.

- `PngWriter.Encode(rgba, width, height)` - encodes a top-to-bottom row-major RGBA8 buffer
  (length must be `width * height * 4`) to a PNG byte array. Filter type 0 on every scanline.
- `PngWriter.Save(path, rgba, width, height)` - `Encode` plus `File.WriteAllBytes`.

```csharp
byte[] rgba = Render2DSnapshot.Capture(320, 180, Color.Black, ctx => DrawScene(ctx));
PngWriter.Save("/tmp/scene.png", rgba, 320, 180);
```

It is a tooling / test helper, not a general image library: RGBA8 encode only. No decode, no
palette, no interlace, no other color types. If you need real image processing, bring your own
library. If you need "turn this captured buffer into a .png on disk", this is enough.
